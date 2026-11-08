using Microsoft.EntityFrameworkCore;

namespace Studio;

public static class AudioEndpoints
{
    public static void MapAudio(this WebApplication app)
    {
        app.MapPost(
            "/api/projects/{id:guid}/speech",
            async (
                Guid id,
                SpeechRequest req,
                StudioDb db,
                Storage store,
                AudioProvider provider,
                IConfiguration config,
                CancellationToken ct
            ) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
                    return Results.NotFound();
                if (
                    string.IsNullOrWhiteSpace(req.Text)
                    || req.Text.Length > 4000
                    || !AudioProvider.Voices.Contains(req.Voice)
                    || !double.IsFinite(req.Speed)
                    || req.Speed < .5
                    || req.Speed > 1.5
                    || string.IsNullOrWhiteSpace(req.Name)
                    || req.Name.Length > 120
                )
                    return Results.BadRequest(
                        new
                        {
                            error = "Lời đọc cần 1–4.000 ký tự, tên 1–120 ký tự, giọng hợp lệ và tốc độ 0,5–1,5.",
                        }
                    );
                if (string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]))
                    return Results.Problem(
                        "Chưa cấu hình OPENAI_API_KEY ở backend.",
                        statusCode: 503
                    );
                await AiBudget.Reserve(
                    db,
                    id,
                    Guid.NewGuid(),
                    [new("speech", req.Text.Length / 1000m)],
                    ct
                );
                return await Handle(
                    async () =>
                    {
                        var asset = new Asset
                        {
                            ProjectId = id,
                            Name = req.Name.Trim() + ".mp3",
                            Kind = "audio",
                            StoredName = Guid.NewGuid().ToString("N") + ".mp3",
                        };
                        var saved = false;
                        try
                        {
                            await provider.Speech(req, store.AssetPath(asset), ct);
                            try
                            {
                                await MediaProcess.AudioDuration(store.AssetPath(asset), ct);
                            }
                            catch (IOException)
                            {
                                throw new AudioFeatureException(
                                    "Dịch vụ AI trả audio không đọc được.",
                                    502
                                );
                            }
                            db.Assets.Add(asset);
                            await db.SaveChangesAsync(ct);
                            saved = true;
                            return Results.Ok(asset);
                        }
                        finally
                        {
                            if (!saved)
                                File.Delete(store.AssetPath(asset));
                        }
                    },
                    ct
                );
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/transcriptions",
            async (
                Guid id,
                TranscriptionRequest req,
                StudioDb db,
                Storage store,
                AudioProvider provider,
                IConfiguration config,
                CancellationToken ct
            ) =>
            {
                var asset = await db
                    .Assets.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == req.AssetId && x.ProjectId == id, ct);
                if (asset is null)
                    return Results.NotFound();
                if (asset.Kind != "audio" && asset.Kind != "video")
                    return Results.BadRequest(
                        new { error = "Chọn file audio hoặc video có lời nói." }
                    );
                if (req.Language != "vi" && req.Language != "en" && req.Language != "auto")
                    return Results.BadRequest(new { error = "Ngôn ngữ chưa hỗ trợ." });
                if (string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]))
                    return Results.Problem(
                        "Chưa cấu hình OPENAI_API_KEY ở backend.",
                        statusCode: 503
                    );
                return await Handle(
                    async () =>
                    {
                        var temp = Path.Combine(
                            store.Root,
                            "temp",
                            "transcribe-" + Guid.NewGuid().ToString("N")
                        );
                        Directory.CreateDirectory(temp);
                        try
                        {
                            double duration;
                            try
                            {
                                duration = await MediaProcess.AudioDuration(
                                    store.AssetPath(asset),
                                    ct
                                );
                            }
                            catch (IOException)
                            {
                                throw new AudioFeatureException(
                                    "Không tìm thấy audio hợp lệ trong file nguồn.",
                                    422
                                );
                            }
                            if (duration > 600)
                                throw new AudioFeatureException(
                                    "Bản này hỗ trợ nhận diện tối đa 10 phút mỗi file.",
                                    400
                                );
                            var audio = Path.Combine(temp, "source.mp3");
                            await MediaProcess.Run(
                                "ffmpeg",
                                [
                                    "-y",
                                    "-nostdin",
                                    "-hide_banner",
                                    "-loglevel",
                                    "error",
                                    "-i",
                                    store.AssetPath(asset),
                                    "-map",
                                    "0:a:0",
                                    "-vn",
                                    "-ac",
                                    "1",
                                    "-ar",
                                    "16000",
                                    "-c:a",
                                    "libmp3lame",
                                    "-b:a",
                                    "64k",
                                    "-threads",
                                    "2",
                                    audio,
                                ],
                                temp,
                                ct
                            );
                            await AiBudget.Reserve(
                                db,
                                id,
                                Guid.NewGuid(),
                                [new("transcription", (decimal)duration / 60m)],
                                ct
                            );
                            var cues = await provider.Transcribe(audio, req.Language, ct);
                            if (cues.Any(c => c.End > duration + 1))
                                throw new AudioFeatureException(
                                    "Phụ đề AI vượt quá thời lượng file nguồn.",
                                    502
                                );
                            return Results.Ok(
                                new
                                {
                                    asset = await SaveSubtitle(
                                        id,
                                        Path.GetFileNameWithoutExtension(asset.Name) + " · AI",
                                        Subtitles.Write(cues),
                                        db,
                                        store,
                                        ct
                                    ),
                                    cues,
                                }
                            );
                        }
                        finally
                        {
                            Directory.Delete(temp, true);
                        }
                    },
                    ct
                );
            }
        );
        app.MapPost(
            "/api/projects/{id:guid}/subtitles",
            async (
                Guid id,
                SubtitleRequest req,
                StudioDb db,
                Storage store,
                CancellationToken ct
            ) =>
            {
                if (!await db.Projects.AnyAsync(x => x.Id == id, ct))
                    return Results.NotFound();
                if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 120)
                    return Results.BadRequest(new { error = "Tên phụ đề cần từ 1–120 ký tự." });
                try
                {
                    var cues = Subtitles.Parse(req.Content);
                    var asset = await SaveSubtitle(
                        id,
                        req.Name,
                        Subtitles.Write(cues),
                        db,
                        store,
                        ct
                    );
                    return Results.Ok(new { asset, cues });
                }
                catch (ArgumentException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
            }
        );
    }

    private static async Task<Asset> SaveSubtitle(
        Guid id,
        string name,
        string content,
        StudioDb db,
        Storage store,
        CancellationToken ct
    )
    {
        var asset = new Asset
        {
            ProjectId = id,
            Name = name + ".srt",
            Kind = "subtitle",
            StoredName = Guid.NewGuid().ToString("N") + ".srt",
        };
        var saved = false;
        try
        {
            await File.WriteAllTextAsync(store.AssetPath(asset), content, ct);
            db.Assets.Add(asset);
            await db.SaveChangesAsync(ct);
            saved = true;
            return asset;
        }
        finally
        {
            if (!saved)
                File.Delete(store.AssetPath(asset));
        }
    }

    private static async Task<IResult> Handle(Func<Task<IResult>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (AudioFeatureException e)
        {
            return Results.Problem(e.Message, statusCode: e.Status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem(
                "Dịch vụ AI phản hồi quá chậm. Yêu cầu chưa được tự động gửi lại.",
                statusCode: 504
            );
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Không kết nối được dịch vụ AI.", statusCode: 502);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Problem("Dịch vụ AI trả dữ liệu không hợp lệ.", statusCode: 502);
        }
    }
}
