# Đợt 30 chức năng v0.11

Đã gộp vào bản v0.12: 15 công cụ audio, 8 công cụ bàn giao bản xuất và 7 công cụ SRT. Các tính năng này chạy local, không cần API key.

| # | Chức năng | Hành vi |
|---|---|---|
| 1 | High-pass giọng | Tắt hoặc 20–500 Hz. |
| 2 | Low-pass giọng | Tắt hoặc 1.000–18.000 Hz. |
| 3 | Giảm nhiễu giọng | Bộ lọc FFT `afftdn`, không phải tách giọng bằng AI. |
| 4 | Compressor giọng | Giảm dải động với cấu hình cố định. |
| 5 | Bass giọng | EQ ±12 dB, tâm 110 Hz. |
| 6 | Treble giọng | EQ ±12 dB, tâm 3.500 Hz. |
| 7 | Cân bằng trái/phải giọng | -1–1, giảm kênh đối diện; nguồn mono được chuyển stereo. |
| 8 | Cắt đầu giọng | Chọn điểm bắt đầu 0–600 giây, kiểm tra không vượt nguồn. |
| 9 | Fade giọng độc lập | Fade-in/out 0–5 giây, giới hạn mỗi đầu tối đa nửa đoạn. |
| 10 | Tốc độ nhạc | 0,5–2× bằng `atempo`, giữ cao độ. |
| 11 | High-pass nhạc | Tắt hoặc 20–500 Hz. |
| 12 | Low-pass nhạc | Tắt hoặc 1.000–18.000 Hz. |
| 13 | Cân bằng trái/phải nhạc | -1–1, độc lập với giọng. |
| 14 | Bật/tắt loop nhạc | Tắt sẽ đệm im lặng sau khi nhạc hết, giữ thời lượng video. |
| 15 | Fade nhạc độc lập | Fade-in/out 0–5 giây, trước khi trộn/duck nhạc. |
| 16 | Audio WAV | 48 kHz, stereo PCM 16-bit từ bản render. |
| 17 | Audio FLAC | 48 kHz stereo, nén lossless từ audio đã render. |
| 18 | MP4 tắt tiếng | Sao chép luồng video, bỏ audio. |
| 19 | GIF xem nhanh | Chọn mốc/độ dài; tối đa 10 giây, rộng 640 px, 10 fps. |
| 20 | Poster PNG | Trích một frame, giữ tỉ lệ trong khung 640 px. |
| 21 | Contact sheet | 6 frame phân bố trong video, lưới 3×2. |
| 22 | Thông số bản xuất | Duration, kích thước, codec, fps, kênh và sample rate. |
| 23 | Gói giao video ZIP | MP4, SRT nếu có, recipe JSON và manifest SHA-256. |
| 24 | Viết hoa đầu câu SRT | Heuristic tiếng Việt, giữ tag định dạng; cần xem lại tên riêng. |
| 25 | Sửa khoảng cách dấu câu | Xử lý khoảng trắng quanh dấu câu, bảo vệ URL/tag. |
| 26 | Tìm trong bảng SRT | Lọc lời, giữ chỉ số cue gốc; hiển thị tối đa 200 kết quả. |
| 27 | Lấy đoạn phụ đề | Cắt cue giao khoảng chọn, đưa đầu đoạn về mốc 0. |
| 28 | Ghép thêm file SRT | Giữ timeline hoặc nối cuối với khoảng nghỉ 0–10 giây. |
| 29 | Bỏ emoji SRT | Xử lý chuỗi ZWJ, màu da, cờ và keycap; không xóa chữ/số thông thường. |
| 30 | Xuất SRT CSV | UTF-8 BOM, thời điểm theo ms, escape công thức spreadsheet. |

## Sử dụng

Trong Studio hoặc Storyboard, mở **Xử lý âm thanh** để chỉnh trước khi dựng. Cấu hình audio được lưu cùng document, JSON, checkpoint, preset và snapshot render. Không sửa file nguồn. Bộ lọc có thể làm thay đổi âm sắc; nghe lại bản xuất để chọn thông số phù hợp với nguồn.

Studio → **Công cụ bản xuất** → chọn job thành công để tải định dạng khác. WAV/FLAC cần audio. FLAC bảo toàn audio của bản render, không khôi phục thông tin đã mất do MP3/AAC. GIF chỉ dành cho xem nhanh, không có palette tối ưu nâng cao. Poster/contact sheet dùng frame thực tế, không tự chọn ảnh đẹp bằng AI.

Công cụ bàn giao chạy tối đa 2 yêu cầu đồng thời, giới hạn 60 giây/yêu cầu, file trả về tối đa 150 MiB; MP4 mute và ZIP giới hạn video nguồn 100 MiB. Các file trung gian được dọn sau khi truyền xong hoặc thất bại; thư mục bàn giao còn lại từ lần dừng bất thường được dọn khi khởi động. FFmpeg/ffprobe nhận đối số qua `ArgumentList`.

Trong màn hình giọng/phụ đề, biến đổi SRT chỉ thay nội dung đang sửa: bấm lưu để thêm file vào thư viện. Ghép/range không thay file SRT đã lưu. Từ chối kết quả không còn cue hoặc vượt giới hạn, giữ bản cũ để người dùng chỉnh tiếp.

## Kiểm thử

6 integration tests audio/deliveries; 5 Node tests utility mới; smoke kiểm chứng tần số/RMS/cân bằng kênh/fade bằng âm thanh tổng hợp và FFmpeg; trình duyệt đi qua từng nhóm điều khiển, tải đủ 7 định dạng và kiểm tra nội dung ZIP/SHA-256. Xem [báo cáo kiểm chứng](VERIFICATION.md).
