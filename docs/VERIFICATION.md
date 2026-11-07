# Kiểm chứng bản MVP

Đã chạy trong môi trường Linux với .NET SDK 10.0.403, Node.js 24 và FFmpeg:

- Backend build thành công, 0 cảnh báo, 0 lỗi sau cập nhật EF Core/SQLite.
- Angular production build thành công.
- Smoke test FFmpeg thật: concat hai clip, loop 3 giây cùng audio/SRT, ảnh thành video; kiểm tra duration, resolution và audio stream qua ffprobe.
- Kiểm tra từ chối asset sai loại, request không hợp lệ và asset khác dự án.
- Không cấu hình AI key: endpoint script trả 503 đúng dự kiến; không gọi API tính phí trong kiểm thử.
- File media lỗi chuyển job sang Failed; retry được chấp nhận.
- Dự án và job tồn tại sau restart backend.
- Playwright + Chromium: tạo dự án, upload, chọn media, render, preview, thư viện và trạng thái AI chưa cấu hình. Không có JavaScript page errors.
- Kiểm tra viewport 390 px không tràn ngang.
- npm audit với production dependencies: 0 vulnerabilities tại lúc kiểm tra.

Chưa kiểm chứng gọi OpenAI thực tế, Windows/macOS, tải lớn, nhiều người dùng hoặc CI trên GitHub. Repository creation đang bị quyền GitHub integration từ chối (403); workflow chỉ chạy khi mã nguồn được push lên repository có quyền ghi.
