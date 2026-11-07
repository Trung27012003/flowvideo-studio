# Kiến trúc v0.3

Angular → HTTP API ASP.NET Core → EF Core SQLite; BackgroundService đọc job queued và gọi FFmpeg. Browser polling theo tải: 2,5 giây khi active, 15 giây khi idle, 30 giây khi lỗi; không gửi khi tab ẩn. Mỗi vòng đợi request hoàn tất trước khi hẹn lần sau; version guard tránh phản hồi cũ ghi đè dự án mới. File dùng ID sinh bởi server, không dùng tên upload làm đường dẫn. FFmpeg arguments được truyền bằng ArgumentList, không qua shell.

## Dữ liệu

Project → Asset và RenderJob. RenderRequest được serialize trong job, lưu danh sách asset có thứ tự, mode, duration, ratio và các audio/subtitle ID. API kiểm tra asset cùng dự án và đúng loại. Assets và exports lưu ngoài database.

## Pipeline

1. Xác thực request rồi lưu Queued.
2. Một worker đổi job thành Running.
3. Chuẩn hóa mỗi cảnh về resolution/frame rate/pixel format chung, bỏ audio gốc.
4. Nối clip, thêm audio và SRT, giới hạn thời lượng, xuất MP4.
5. Đánh dấu Succeeded hoặc Failed và dọn file tạm.

Lần chạy đầu dùng EnsureCreated. Khi thay đổi schema cần chuyển sang EF migrations trước khi phân phối bản nâng cấp. Không chạy nhiều instance backend trên cùng data directory; chưa có distributed lock. File upload chưa hợp lệ có thể được nhận rồi thất bại lúc render; bổ sung ffprobe validation là bước kế tiếp.

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
