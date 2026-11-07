# Kiểm chứng bản v0.14

Đã chạy trong môi trường Linux với .NET SDK 10.0.403, Node.js 24 và FFmpeg:

- Backend build thành công, 0 cảnh báo, 0 lỗi sau cập nhật EF Core/SQLite.
- Angular production build thành công.
- Smoke test FFmpeg thật: concat hai clip, loop 3 giây cùng audio/SRT, ảnh thành video; kiểm tra duration, resolution và audio stream qua ffprobe.
- Kiểm tra từ chối asset sai loại, request không hợp lệ và asset khác dự án.
- Kiểm tra cắt đúng nội dung theo điểm vào/ra, validation điểm cắt và dừng ở ngân sách thời lượng.
- Hủy queued/running, gọi hủy lặp lại, không để lại MP4 dở, worker tiếp tục xử lý job sau.
- Không cấu hình AI key: endpoint script trả 503 đúng dự kiến; không gọi API tính phí trong kiểm thử.
- File media lỗi chuyển job sang Failed; retry được chấp nhận.
- Dự án và job tồn tại sau restart backend.
- Playwright + Chromium: tạo dự án, upload, nhập điểm cắt, render, preview, hủy tác vụ, thư viện và trạng thái AI chưa cấu hình. Không có JavaScript page errors.
- Kiểm tra viewport 390 px không tràn ngang.
- npm audit với production dependencies: 0 vulnerabilities tại lúc kiểm tra. Audit toàn bộ còn 16 cảnh báo mức moderate trong toolchain Angular CLI (chuỗi glob/minimatch); không có mức high/critical. Chưa thay đổi major Angular để xử lý nhóm này.

Chưa kiểm chứng gọi OpenAI thực tế, Windows/macOS, tải lớn hoặc nhiều người dùng. Mã nguồn đã được push lên GitHub; trạng thái build và smoke test của từng commit xem tại [GitHub Actions](https://github.com/Trung27012003/flowvideo-studio/actions).

Phép đo trước/sau và giới hạn diễn giải xem tại [PERFORMANCE.md](PERFORMANCE.md).

## Audio/SRT

- HTTP integration tests dùng provider AI mô phỏng: speech request, multipart transcription, audio hợp lệ, timestamp, render với asset tạo ra; thiếu key, đầu vào sai, provider lỗi/malformed, file không có audio, nguồn quá 10 phút và dọn file khi thất bại.
- Editor SRT kiểm tra BOM/CRLF, chuẩn hóa đánh số, mốc sai, lưu bản mới giữ nguyên bản cũ; browser test mở/sửa/lưu/tải SRT và trạng thái nút AI khi chưa có key.
- Chưa thực hiện yêu cầu tính phí đến OpenAI; chất lượng giọng, nhận diện tiếng Việt, độ trễ và quyền model thực tế cần kiểm tra bằng tài khoản người dùng.

## Storyboard

- Lưu/mở lại ý tưởng và kịch bản; từ chối version cũ, asset khác project, scene không hợp lệ và scene chưa có tư liệu khi render.
- Dựng hai cảnh dùng cùng nguồn ngắn với duration khác nhau; xác nhận thời lượng đầu ra và snapshot job không đổi khi document được sửa.
- Database schema cũ với project/media có sẵn được nâng cấp thêm bảng document và giữ nguyên dữ liệu/file.
- Browser: lưu script, reload, chia cảnh, chọn media, dựng thật, đổi thứ tự, lưu/reload, kiểm tra mobile và menu có nhãn truy cập.

## Ảnh AI

- Queue → provider mô phỏng → PNG đúng kích thước → decode/asset → gắn vào document → render FFmpeg thật.
- Mã request trùng không gọi lại; yêu cầu khác khi cảnh đang chạy bị từ chối; ảnh cùng fingerprint được reuse trước quota. Đổi prompt khi hết quota bị từ chối.
- Lỗi HTTP, JSON, PNG và kích thước không tạo asset; network/timeout ghi Unknown; gọi lại cùng ID không retry.
- Startup đánh dấu Running Unknown, chỉ tiếp tục Queued. Kiểm tra thiếu key, phiên bản cũ, prompt rỗng và dọn file.
- Browser dùng capability/image job API mô phỏng để kiểm tra queued/succeeded, chọn ảnh vào cảnh và dựng thật. Không có API key thật hoặc yêu cầu tính phí trong test.

Tổng bộ integration tests hiện có 122 trường hợp. Chưa kiểm chứng chất lượng ảnh/quyền model/độ trễ thực tế bằng tài khoản OpenAI của người dùng.

## Đợt 10 chức năng

- JSON export/import: giữ script/prompt/options, đổi project/scene IDs, bỏ mọi media/audio reference và start; dữ liệu gốc giữ nguyên. JSON sai cấu trúc, profile không hợp lệ và file quá 2 MiB không tạo project.
- Tương thích default của JSON document/job cũ; từ chối resolution ngoài 720/1080 và gain ngoài 0–2.
- Render 1920×1080 thật, xuất SRT hai cảnh đúng timeline và không hiện SRT ở job không có phụ đề.
- Đo RMS PCM sau render voice-only, music-only và mix: gain 25% tạo mức tín hiệu khoảng 1/4 so với 100%.
- Browser: tìm không dấu, lọc, preview video/audio/image/SRT, Escape/focus modal, clone ID riêng, preset, lưu gain, download JSON/import thành project mới, SRT download, kiểm tra responsive.


## Đợt 30 chức năng v0.7

- Rename project/media: cập nhật bền vững, giữ nguyên byte file, chặn đổi extension và ký tự đường dẫn/điều khiển.
- Clone dự án: ID project/scene/media mới; remap voice/music; byte media độc lập khi file gốc mất; dữ liệu thiếu không tạo dự án dang dở; không gọi AI.
- ZIP: chọn giới hạn, ID trùng/khác project, sanitize tên legacy chứa đường dẫn; giải nén so byte file gốc. CSV: quote tiếng Việt/dấu phẩy/dấu nháy và chặn formula injection.
- Metadata: ffprobe đọc kích thước, thời lượng và byte; nguồn hỏng trả 422.
- Từ chối fit/fade không hợp lệ ở render/document; default document/job cũ tương thích.
- 7 Node tests: phân bổ thời lượng theo bước 0,1 giây, offset/speed SRT, tìm/thay literal, wrap Unicode, merge không vượt giới hạn/chồng lấn, WebVTT escape và validation BOM/CRLF.
- FFmpeg thật: đo pixel vùng viền cho contain/cover; đầu/cuối tối và giữa sáng khi fade; PCM RMS kiểm tra fade audio và duck nhạc, giữ thời lượng loop.
- Browser: rename dự án/media, undo/redo, split, thời lượng hàng loạt, lời/đánh số/thứ tự, tải TXT/CSV/ZIP/VTT, metadata, điền media, clone có file, lưu fit/fade/duck, xử lý SRT và layout mobile.

Lệnh mới: `npm run test:tools` trong `src/Studio.Web`. CI chạy cùng build, integration tests, media smoke, browser smoke và đóng gói artifact.


## Đợt 30 chức năng v0.8

- Sáu integration tests bổ sung đưa tổng .NET lên 53: chặn 12 nhóm cấu hình sai ở cả hai intake; save/export/import/clone Effects; PNG thumbnail/waveform thật, MP3/frame độc lập; nguồn thiếu audio/mốc sai/sai loại và cleanup; render tất cả hiệu chỉnh, fps/mono/48 kHz, recipe và audio export; silent job và music offset vượt nguồn.
- Node tổng 12 test: thêm strip HTML/ASS, gộp trùng/chồng lấn theo giới hạn, split giữ chữ và phân hoạch thời gian, clamp và report/TXT.
- Media smoke: decode pixel grayscale/brightness/contrast, viền magenta, rotate+flip/sharpen/vignette; ffprobe 24/60 fps và mono; đo RMS các đoạn im lặng/trễ/voice tăng tốc và loudnorm.
- Browser: cấu hình đủ 16 option, lưu/snapshot render, tạo/tải thumbnail và waveform, trích frame/audio vào thư viện, 6 công cụ SRT, job filter/sort, tải recipe/MP3 và layout desktop/mobile.

Các test không gửi request trả phí. Sinh video AI chưa được triển khai hoặc kiểm chứng trong đợt này.


## Đợt 30 chức năng v0.9

- 6 integration tests mới, tổng .NET 59: capture/version/scope/rename/delete/export/restore checkpoint; preset detaches nội dung và kiểm tra JSON/body chunked quá giới hạn; 20 mục/kind và upgrade idempotent giữ project/media; summary/preflight nguồn padded/start sai/file mất; audio hỏng và thiếu binding; hai render thật khác ratio, snapshot giữ lời/fps, duplicate Succeeded và chặn trạng thái khác.
- Node tổng 17 bài: sort SRT ổn định, overlap/minimum không mất lời hoặc tạo cue rỗng, whitespace/case tiếng Việt giữ tag, reading timing/shuffle immutable và compare checkpoints.
- Browser: mốc lưu/khôi phục/so sánh/đổi tên/xuất; preset lưu/áp/đổi tên/nhập/xuất/xóa xác nhận; summary/preflight; multiselect/batch/undo/export/timing/shuffle; 6 công cụ SRT; hai render dọc/ngang thật, repeat job, queue stats/search và mobile.
- Media smoke cũ tiếp tục chạy cho concat/loop/trim, caption/audio, gain/fade/duck/effects, cancellation, errors/retry và restart persistence.

Không gọi AI trả phí. Sinh video AI vẫn chưa được triển khai hoặc kiểm chứng trong v0.9.


## Đợt 30 chức năng v0.10

- 6 integration tests mới, tổng .NET 65: validate SceneOptions/CaptionStyle trên document/Studio; defaults cũ; save/JSON/clone/checkpoint/preset; skip disabled scene và caption; render cùng nguồn với override khác; metadata scope/limits/upsert/upgrade; hash/copy bytes độc lập/bulk name; usage document/checkpoint/job và chặn ID khác project.
- Smoke FFmpeg: đo pixel red/blue khi speed 2×; phân biệt brightness của hai occurrence cùng file; fade đầu/cuối cảnh; cover-focus/zoom; chữ đỏ ở trên/dưới và nền hộp xanh thật. Các kiểm tra cũ vẫn chạy.
- Browser: điều chỉnh đủ scene/style, render hai cảnh khi một cảnh tắt, kiểm tra cue bị tắt, lưu favorite/tags/note/lọc không dấu, usage, upload file trùng/hash, rename batch, copy qua project kèm metadata, tạo và thay liên kết cảnh, save/style persistence và mobile.
- 17 test Node vẫn kiểm tra utility/SRT/compare, có trường captions trong phép so sánh cấu hình mới.

Không gọi AI trả phí. Tạo video AI vẫn chưa có trong đợt v0.10.

## v0.11: audio, bàn giao và SRT

- 6 integration tests mới: validation trên cả document/render, persist/import/clone/checkpoint/preset và default cũ; filter graph render thật; 7 định dạng bàn giao/metadata và ZIP SHA-256; lỗi nguồn không audio/khoảng sai/giới hạn file; non-loop nhạc không cắt ngắn video; kiểm tra trim giọng và dọn file.
- Node tổng 22 tests: thêm sentence/punctuation bảo vệ tag/URL, emoji Unicode, range clamp/rebase, merge append/timeline, CSV chống công thức.
- Smoke dùng tín hiệu tổng hợp và đo audio thực: high/low-pass, bass/treble, compressor/denoise RMS, cân bằng kênh, fade giọng/nhạc, tốc độ và non-loop/padding, cắt đầu giọng chọn đúng tone. Kế thừa các kiểm tra pixel/metadata của những bản trước.
- Browser chỉnh đủ nhóm audio, kiểm tra recipe/lưu lại, tải WAV/FLAC/mute/GIF/poster/contact/bundle, ffprobe kênh/sample rate, giải ZIP đối chiếu manifest SHA-256; thao tác 7 công cụ SRT.

## v0.12: video AI

14 trường hợp integration mới (5 Fact và 9 nhánh Theory):

- Đúng endpoint/header/model/input/response_format REST Google; response đọc steps/model_output/video, không dùng shortcut SDK.
- File H.264 thực → ffprobe → asset → gắn rõ ràng → render thật. Prompt document không bị worker ghi đè.
- Cùng RequestId không replay; cùng fingerprint reuse trước quota; khác prompt khi hết quota bị chặn.
- Ảnh base64 đúng byte/MIME, scope project; phiên bản cũ/options sai/key thiếu không gọi provider hoặc giữ hàng đợi rác.
- JSON sai, MP4 không hợp lệ, container hỏng, response quá giới hạn, HTTP từ chối, HTTP 5xx, lỗi mạng/IO và chưa completed đều đi đúng Failed/Unknown, không tự gửi lại và không để asset/temp rác.
- Startup Running → Unknown, giữ Queued; hủy Queued và kiểm tra scope/not-found/terminal conflict.
- Browser: single/batch, duration/resolution/reference, lưu trước submit, apply + lưu document thật, download MP4 đúng byte, cancel Queued và mobile layout; provider/queue video được mô phỏng.

Tổng .NET 85, Node 22. Các lệnh xác minh: `dotnet test tests/Studio.Api.Tests -c Release`, `npm run build --prefix src/Studio.Web`, `npm run test:tools --prefix src/Studio.Web`, `python3 scripts/smoke.py`, `node scripts/browser-smoke.cjs` (có Chromium và SDK trong PATH). CI chạy toàn bộ và đóng gói ứng dụng/ảnh chụp.

Không có yêu cầu AI trả phí trong tests. Chưa kiểm tra Google/OpenAI bằng key thật: quyền model, hóa đơn, độ trễ và chất lượng tạo sinh còn cần tài khoản người dùng. Các phép đo DSP/codec/file chỉ chứng minh pipeline local và hợp đồng tích hợp. Xem [VIDEO-AI](VIDEO-AI.md) cho nguồn tham khảo chính thức.

## v0.13: workflow sản xuất và loop

Tổng **104 integration tests .NET**, **22 Node tests**, full FFmpeg smoke và Playwright browser.

- 11 tests Production: plan không enqueue; offline snapshot/render thật; giữ bản đang sửa; request idempotency; per-scene TTS với blank scene/timing/padding; MP3 concat và SRT timeline; ảnh AI shared cache; chia script → video → render và reuse script-derived IDs; scope/options/version/key/quota nguyên batch; Unknown/no replay/restart; cancel queued children/capacity; fail media cancels unsent siblings; resume từ completed speech không gọi provider; hủy trong lúc TTS không làm run sống lại; retry render tạo job mới và không gọi lại AI.
- 8 trường hợp Loop (2 Fact + 6 Theory): direct/reverse/pingpong/crossfade đo frame hai màu và blend/seam thực, duration và recipe; enum/mode/resolution/cycle/fade budgets; cleanup khi lỗi; cả 3 kiểu mới ở 24/60 fps giữ đúng frame rate. Đã bắt và sửa lỗi `xfade` nhận frame rate 1/0 sau trim/settb bằng bước fps cố định.
- Browser dùng workflow API thật, không route mock workflow: plan/start, sửa narration khi đang chạy, snapshot nguyên vẹn, native video metadata/preview, apply result, cancel, reverse/pingpong/crossfade render từ UI. Đồng thời chạy lại toàn bộ flows v0.7–v0.12 và mobile/no JS errors.
- Browser regression kiểm tra undo thay video → ảnh khôi phục media **và speed**; thao tác cấu trúc luôn có history entry riêng, input typing vẫn coalesce theo focus.
- Các bài có AI dùng HTTP handler mô phỏng, trả PNG/MP3/MP4 thật để decode/probe/render. Không gọi dịch vụ AI tính phí. Quyền model, chất lượng tạo sinh và giá thực tế chưa được kiểm tra.

Lệnh giữ như trước. Xem [Production](PRODUCTION.md), [Loops](LOOPS.md) cho cách dùng, giới hạn và các nguồn chính thức đã đối chiếu.

## v0.14

14 test cases StoryPlan + 4 Quality; tổng .NET 122, Node 22. Story tests dùng provider mô phỏng để kiểm tra strict schema/header/model/limits, original/review/apply, revisions/source immutability, cache/quota/idempotency, missing key/options/scope, errors/refusal/truncation/missing/extra/count/duration, Unknown/restart, queued cancel, import không tốn quota. QA tests render FFmpeg thật và kiểm tra report tự tạo trước Succeeded, recipe SHA-256/recheck/upsert, audio/mono/SRT overflow warning, output sai kích thước/duration, corrupt/missing, nonterminal và direction validation.

Browser dùng import/review/save/export/apply thật, giữ document trước apply, reorder/duplicate/remove scene không tráo form fields, metadata direction đi qua storyboard → render, automatic/manual QA và JSON download, mobile layout. Phần enqueue/cancel AI planner UI được route-mock; hợp đồng provider kiểm tra ở .NET. Toàn bộ browser regression v0.7–v0.13 và FFmpeg DSP/pixel smoke tiếp tục chạy. Không có key thật/yêu cầu AI tính phí.

Nguồn và mức giới hạn QA: [STORY-ARCHITECT](STORY-ARCHITECT.md).
