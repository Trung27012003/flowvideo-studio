# Kiến trúc v0.1

Angular → HTTP API ASP.NET Core → EF Core SQLite; BackgroundService đọc job queued và gọi FFmpeg. Browser polling trạng thái mỗi 2,5 giây. File dùng ID sinh bởi server, không dùng tên upload làm đường dẫn. FFmpeg arguments được truyền bằng ArgumentList, không qua shell.

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

SQLite và hosted worker thay PostgreSQL/Hangfire để bản local ít dependency. Chưa cần Redis, Docker hay cloud storage. Worker chưa có pause/cancel từ UI. Polling thay SignalR trong bản đầu; có thể thay transport mà không đổi dữ liệu job. AI script dùng API thật, không retry tự động request tính phí.

## Nâng cấp

Tách renderer/provider interface khi thêm provider thứ hai. Tạo Scene, TimelineRevision, ProviderRequest, UsageRecord; lưu model, request ID, tham số và nguồn gốc media. Dùng capability matrix cho từng provider. Thêm authentication trước multi-user; thêm migrations, PostgreSQL và cơ chế claim job nguyên tử trước nhiều worker.
