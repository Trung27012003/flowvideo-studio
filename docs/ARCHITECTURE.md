# Kiến trúc v0.5

Angular → HTTP API ASP.NET Core → EF Core SQLite; BackgroundService đọc job queued và gọi FFmpeg. Browser polling theo tải: 2,5 giây khi active, 15 giây khi idle, 30 giây khi lỗi; không gửi khi tab ẩn. Mỗi vòng đợi request hoàn tất trước khi hẹn lần sau; version guard tránh phản hồi cũ ghi đè dự án mới. File dùng ID sinh bởi server, không dùng tên upload làm đường dẫn. FFmpeg arguments được truyền bằng ArgumentList, không qua shell.

## Dữ liệu

Project → Asset và RenderJob. RenderRequest được serialize trong job, lưu danh sách asset có thứ tự, mode, duration, ratio và các audio/subtitle ID. API kiểm tra asset cùng dự án và đúng loại. Assets và exports lưu ngoài database.

## Pipeline

1. Xác thực request rồi lưu Queued.
2. Một worker đổi job thành Running.
3. Chuẩn hóa mỗi cảnh về resolution/frame rate/pixel format chung, bỏ audio gốc.
4. Nối clip, thêm audio và SRT, giới hạn thời lượng, xuất MP4.
5. Đánh dấu Succeeded hoặc Failed và dọn file tạm.

Lần chạy đầu dùng EnsureCreated. v0.4 thêm bảng ProjectDocuments bằng một lệnh CREATE TABLE IF NOT EXISTS có giới hạn rõ, chạy cho cả database mới/cũ; đã kiểm thử giữ nguyên project/media của schema cũ. v0.5 tiếp tục thêm bảng nhật ký ảnh bằng lệnh idempotent; thay đổi cột/bảng hiện có cần chuyển sang migration có phiên bản đầy đủ. Không chạy nhiều instance backend trên cùng data directory; chưa có distributed lock. File upload chưa hợp lệ có thể được nhận rồi thất bại lúc render; bổ sung ffprobe validation là bước kế tiếp.

## Quyết định có chủ ý

SQLite và hosted worker thay PostgreSQL/Hangfire để bản local ít dependency. Chưa cần Redis, Docker hay cloud storage. UI đã hỗ trợ cancel. Endpoint đổi trạng thái bằng update có điều kiện; JobControl liên kết CancellationToken với FFmpeg. Đăng ký token trước khi claim Queued → Running để tránh bỏ lỡ cancel. Hoàn tất cũng là update có điều kiện, nên job đã hủy không thể chuyển lại Succeeded. Chưa có pause. Polling thay SignalR trong bản đầu; có thể thay transport mà không đổi dữ liệu job. AI script dùng API thật, không retry tự động request tính phí.

## Nâng cấp

Tách renderer/provider interface khi thêm provider thứ hai. Tạo Scene, TimelineRevision, ProviderRequest, UsageRecord; lưu model, request ID, tham số và nguồn gốc media. Dùng capability matrix cho từng provider. Thêm authentication trước multi-user; thêm migrations, PostgreSQL và cơ chế claim job nguyên tử trước nhiều worker.

## Render tối ưu và trim

Probe duration với ffprobe; chỉ chuẩn hóa phần clip trong điểm vào/ra và ngân sách còn lại. Tái sử dụng cảnh trùng trong cùng job. Loop chuẩn hóa một chu kỳ nguồn rồi stream_loop ở bước xuất. Dùng video stream copy khi không có phụ đề; burn-in vẫn cần encode. Giới hạn encoder ở hai thread, log tiến trình giữ tail hữu hạn. Xuất vào file tạm rồi move khi xong; job lỗi/hủy không để lại MP4 dở ở exports.

Giữ nguyên schema SQLite hiện có. Cuts được lưu trong RequestJson, job cũ không có Cuts dùng toàn bộ nguồn như trước. API hàng đợi chỉ trả metadata 100 job gần nhất, không trả RequestJson; truy vấn media chỉ tải các asset được tham chiếu.

## Audio và phụ đề v0.3

`AudioProvider` dùng named HttpClient `OpenAiAudio` với endpoint OpenAI cố định. Speech và transcription không tự retry; thời hạn 90 giây bao gồm đọc body. Giọng đọc trả MP3, được ffprobe kiểm tra trước khi lưu asset. STT kiểm tra track audio và giới hạn 600 giây, trích mono MP3 16 kHz/64 kbps rồi gửi multipart `whisper-1`, `verbose_json`. Đầu ra được kiểm tra schema, timestamp và thời lượng trước khi lưu SRT.

`Subtitles` chuẩn hóa UTF-8/newline, kiểm tra thứ tự thời gian và đánh số lại. Lưu SRT luôn tạo asset mới để không sửa file mà render hiện tại có thể đang dùng. Không thay đổi schema database. Audio/sub xử lý đồng bộ theo HTTP request, chưa dùng hàng đợi render; reload/đứt kết nối có thể hủy xử lý local và không đảm bảo dịch vụ AI chưa tính phí.

`Studio.Api.Tests` dùng WebApplicationFactory và HTTP handler mô phỏng cho upstream, nhưng dùng SQLite/FFmpeg thật. CI không dùng key thật hay gọi mạng AI. `FlowVideo.sln` chứa API và test project.

## Storyboard v0.4

ProjectDocuments lưu một bản hiện tại cho mỗi project: JSON ý tưởng/kịch bản/cảnh, Version và UpdatedAt. Tạo lần đầu dựa vào primary key; cập nhật dùng UPDATE có điều kiện Version. Bản cũ nhận 409, không tự ghi đè. Version phục vụ kiểm tra đồng thời, chưa phải lịch sử có thể khôi phục.

Endpoint dựng đọc bản đã lưu theo version yêu cầu, kiểm tra asset cùng dự án rồi tạo RenderJob với snapshot SceneClip. Worker dùng duration/start theo từng occurrence nên một asset có thể dùng lại ở nhiều cảnh. Với video thiếu thời lượng, filter tpad giữ frame cuối; ảnh dùng đúng duration cảnh. SubtitleCue được tính sau normalize bằng thời lượng thực tế của mỗi part, lời rỗng được bỏ qua; phụ đề cảnh được burn-in, chưa thành asset SRT riêng. TTS không được gọi ngầm khi dựng.

Script và board được lưu cùng document. Frontend đọc lại theo project, giữ draft trong RAM đến khi bấm lưu. Component StoryboardEditor tách khỏi App. localStorage chỉ nhớ project ID gần nhất; dữ liệu nội dung nằm ở backend.

## Image generation v0.5

Bảng ImageGenerations lưu request ID, project/scene/version, prompt, model/size/quality, fingerprint, trạng thái, asset ID, provider request ID khi có và thời gian. CREATE TABLE/INDEX IF NOT EXISTS bổ sung cho schema cũ, không sửa dữ liệu cũ. Startup chuyển Running thành Unknown, giữ Queued để worker tiếp tục. Worker ảnh riêng, một request tại một thời điểm, claim bằng UPDATE có điều kiện.

Intake dùng semaphore trong một backend để kiểm tra idempotency, scene/version, reuse và rolling quota trước khi insert. Worker dùng cùng gate khi commit trạng thái cuối để completion không lọt giữa bước đọc cache và kiểm tra active rồi tạo lượt trùng. Chỉ hỗ trợ một backend dùng chung data directory; đây không phải giới hạn phân tán. Quota đếm cả lỗi/Unknown, không rollback phí hay ước lượng tiền. Succeeded cùng scene/prompt/model/size/quality được reuse khi asset/file còn tồn tại; lịch sử giữ nguyên và không gắn ảnh vào document tự động.

ImageProvider chỉ gọi endpoint OpenAI cố định, không retry; 90 giây bao gồm body, tối đa 24 MiB JSON và 16 MiB PNG giải mã. Kiểm tra PNG header, codec/dimensions bằng ffprobe rồi decode/re-encode một frame bằng FFmpeg trước khi lưu asset. File tạm/lỗi được dọn. Network/timeout → Unknown; provider/output lỗi → Failed, vẫn thông báo khả năng tính phí. Không tự tìm lại kết quả ở provider sau Unknown.

Frontend polling chung render và ảnh; ảnh mới được thêm vào thư viện, áp dụng vào scene chỉ khi người dùng bấm và cần lưu storyboard. Browser contract test mô phỏng capability/history/create API ảnh, nhưng upload asset, lưu document và render vẫn dùng backend/FFmpeg thật. ImageTests kiểm tra provider HTTP bằng fake handler, không gọi dịch vụ tính phí.
