# Tối ưu v0.2

## Các thay đổi

- Không encode video lần thứ hai khi không burn-in phụ đề; bước cuối copy stream video và chỉ xử lý audio nếu có.
- Loop chỉ chuẩn hóa một chu kỳ nguồn thay vì toàn bộ thời lượng đầu ra rồi encode thêm lần nữa.
- Cảnh trùng trong cùng job dùng lại file đã chuẩn hóa; dừng xử lý khi timeline đạt giới hạn. Cắt nguồn trước khi normalize.
- Chỉ truy vấn asset đang dùng; hàng đợi trả metadata 100 job gần nhất, không gửi toàn bộ cấu hình mỗi lần refresh.
- Polling không chồng request, giảm nhịp khi rảnh, tạm ngừng khi tab ẩn, backoff khi lỗi. Requests bị hủy khi component đóng.
- Giới hạn encoder hai thread, giữ log tail hữu hạn, dọn tiến trình và file tạm khi hủy/lỗi. Atomic claim/finish/cancel tránh ghi đè trạng thái.

## Phép đo local

So sánh bản `535582d` với worker v0.2 trên cùng môi trường Linux, .NET 10 và FFmpeg. Clip `testsrc2` 640×360 dài 1 giây, tạo loop 30 giây, đầu ra 720×1280, 30 fps, không audio/subtitle. Mỗi bản hai lần chạy, tính từ POST tạo job đến trạng thái Succeeded; bao gồm thời gian đợi worker và polling đo 100 ms.

| Bản   |   Lần 1 |   Lần 2 | Trung vị |
| ----- | ------: | ------: | -------: |
| Trước | 8,138 s | 8,601 s |  8,370 s |
| Sau   | 1,831 s | 1,703 s |  1,767 s |

Trung vị giảm khoảng 79%, tương đương 4,7 lần trong bài đo này. Đây là phép đo hẹp với hai mẫu mỗi bản, không đại diện mọi video/phần cứng. Khi chèn phụ đề, bước cuối vẫn cần encode nên mức cải thiện sẽ khác. CI kiểm tra tính đúng đắn, không đặt ngưỡng hiệu năng theo số đo này.

## Kiểm thử bổ sung

Trim được kiểm tra bằng nguồn có cảnh đỏ rồi xanh: cắt 1–2 giây phải xuất cảnh xanh, đúng thời lượng và tỉ lệ dọc. Kiểm tra hủy queued/running, hủy lặp lại, từ chối hủy job hoàn tất, không có export dở và job sau tiếp tục chạy. Nguồn lỗi ở ngoài ngân sách thời lượng không bị xử lý. Browser smoke kiểm tra nhập điểm cắt, preview video thật, nút hủy và mobile.
