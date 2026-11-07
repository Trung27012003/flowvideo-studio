# Kiểm chứng bản v0.2

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
