# Kiểm chứng bản v0.6

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

Tổng bộ integration tests hiện có 38 trường hợp. Chưa kiểm chứng chất lượng ảnh/quyền model/độ trễ thực tế bằng tài khoản OpenAI của người dùng.

## Đợt 10 chức năng

- JSON export/import: giữ script/prompt/options, đổi project/scene IDs, bỏ mọi media/audio reference và start; dữ liệu gốc giữ nguyên. JSON sai cấu trúc, profile không hợp lệ và file quá 2 MiB không tạo project.
- Tương thích default của JSON document/job cũ; từ chối resolution ngoài 720/1080 và gain ngoài 0–2.
- Render 1920×1080 thật, xuất SRT hai cảnh đúng timeline và không hiện SRT ở job không có phụ đề.
- Đo RMS PCM sau render voice-only, music-only và mix: gain 25% tạo mức tín hiệu khoảng 1/4 so với 100%.
- Browser: tìm không dấu, lọc, preview video/audio/image/SRT, Escape/focus modal, clone ID riêng, preset, lưu gain, download JSON/import thành project mới, SRT download, kiểm tra responsive.
