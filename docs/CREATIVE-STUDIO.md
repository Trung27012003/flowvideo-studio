# Creative Studio v0.15

Bản này thêm năm nhóm vào cùng ứng dụng Angular + .NET + FFmpeg hiện có. Không cần Python service, Redis, GPU hay đăng ký thêm thư viện trả phí. Chỉ những thao tác gọi nhà cung cấp AI mới cần key và có thể tính phí.

## Cách dùng

1. Chọn dự án, mở **Hồ sơ nhân vật/bối cảnh · Backup · Ngân sách AI** ngay dưới thanh dự án.
2. Tạo hồ sơ, chọn cảnh và bấm **Gắn bản chụp vào cảnh**. Mở Storyboard để xem bản chụp; lưu storyboard để giữ thay đổi.
3. Mở **Chữ, logo, CTA và chuyển cảnh** trong Studio hoặc Storyboard. Cấu hình của hai màn hình độc lập; storyboard lưu vào dự án, Studio gửi trực tiếp theo từng tác vụ.
4. Điền đơn giá và ngân sách nếu dùng AI, bấm **Lưu ngân sách**, rồi tính thử hoặc kiểm tra toàn batch trong mục Một nút. Để trống đơn giá có nghĩa là chưa biết, không có nghĩa miễn phí.
5. **Lưu storyboard trước khi backup.** Tải ZIP của bản đã lưu. Chọn Khôi phục backup để tạo một dự án mới độc lập, sau đó kiểm tra hoặc dựng tiếp.

## Hồ sơ nhân vật và bối cảnh

- Tối đa 50 hồ sơ/dự án; tạo, sửa, xóa, tải lại; chỉnh sửa có kiểm tra version để tránh ghi đè dữ liệu mới.
- Nhân vật: tên, mô tả, ngoại hình, trang phục, mô tả giọng, ghi chú sản xuất và ảnh tham chiếu tùy chọn.
- Bối cảnh: tên, mô tả, thời tiết, thời điểm, ánh sáng, đạo cụ, ghi chú và ảnh tham chiếu.
- Mỗi cảnh gắn tối đa hai nhân vật, một bối cảnh và trạng thái nhân vật riêng. Lưu nguyên nội dung + ID + version vào cảnh, không tra lại bản mới nhất khi render hoặc chạy AI. Sửa/xóa thư viện giữ nguyên cảnh đã gắn; gắn lại nếu muốn dùng phiên bản mới.
- Mô tả liên quan được thêm vào prompt ảnh/video, kể cả workflow. Ghi chú sản xuất không gửi vào prompt. Prompt tổng hợp giới hạn 4.000 ký tự.
- Ảnh phải thuộc cùng dự án. Ảnh trong hồ sơ dùng để quản lý và tham khảo; **chưa tự động gửi ảnh đó cho engine**, chưa giữ nhận diện khuôn mặt bằng embedding/LoRA. Mô tả giọng không tự thay giọng TTS; vẫn chọn giọng trong phần lồng tiếng.
- Xuất/nhập gói `flowvideo.profiles` schema 1 để tái sử dụng mô tả. Gói JSON bỏ ảnh và cấp ID mới khi nhập. Import nguyên gói kiểm tra giới hạn trước khi lưu.
- Nhân bản dự án sao chép hồ sơ và ánh xạ lại các ảnh liên quan. Import storyboard JSON giữ mô tả chụp nhưng bỏ liên kết ảnh/logo; ZIP backup giữ đầy đủ các file đó.

## Chữ, CTA, logo

`BrandingOptions` là cấu hình tùy chọn trong StoryDocument và RenderRequest; dự án cũ không cần đổi dữ liệu.

- Tối đa năm lớp chữ, mỗi lớp 300 ký tự, mốc bắt đầu/kết thúc 0–600 giây, chín vị trí, cỡ 12–96, màu chữ/nền và độ đậm nền. Thêm chữ/CTA có nội dung mẫu để sửa.
- Xuống dòng thủ công; chưa tự căn chữ vừa khung, keyframe hoặc chuyển động từng từ. Mốc thời gian tính từ đầu video. Lớp nằm ngoài thời lượng thực không xuất hiện, nên đặt mốc theo video bạn đang dựng.
- Một logo từ ảnh dự án, chín vị trí, rộng 5–50% khung, giới hạn chiều cao 1/4 khung và giữ tỉ lệ, độ rõ 0–1 cùng mốc xuất hiện riêng.
- Chữ được ghi vào file UTF-8 tạm, chạy `drawtext` với `expansion=none`; nội dung như dấu phần trăm, dấu nháy, dấu hai chấm không được nội suy thành lệnh filter. Logo chạy qua `overlay`, giữ alpha PNG.
- Tương thích với phụ đề, lồng tiếng, nhạc, ducking và fade. Lớp branding đặt sau phụ đề theo thứ tự render hiện tại; tránh chọn vị trí đè chữ sub.
- Xem kết quả bằng video render; chưa có preview thời gian thực của các lớp trên canvas.

## Chuyển cảnh giữa clip

Các kiểu: `cut`, `fade`, `fadeblack`, `fadewhite`, `wipeleft`, `slideright`, `smoothleft`, `circleopen`. Mặc định `cut` giữ hành vi cũ. Bật hiệu ứng cho 2–10 clip trong concat/storyboard; thời gian 0,05–1,5 giây, làm tròn theo frame. Mỗi clip phải dài hơn hiệu ứng và một frame.

Ứng dụng nối thêm một đoạn **khung hình cuối đóng băng** vào clip đi trước rồi dùng FFmpeg `xfade` với clip kế tiếp, tại mốc kết thúc cảnh ban đầu. Tổng thời lượng vẫn là tổng các cảnh; giọng và phụ đề không đổi mốc. Cách này có thể thấy hình đứng trong phần chuyển cảnh; chưa dùng handle từ video nguồn hoặc thời lượng chồng lấn làm ngắn tổng video. Một kiểu/thời gian áp dụng cho cả bản dựng, chưa chỉnh riêng từng ranh giới. Loop dùng bộ direct/reverse/pingpong/crossfade riêng của v0.13.

## Ngân sách AI

Đây là **kiểm soát dự toán theo đơn giá người dùng nhập**, không phải lấy hóa đơn hoặc báo giá tự động từ OpenAI/Google. Mặc định tắt chặn ngân sách và để trống giá. Đơn giá áp dụng chung theo loại thao tác; người dùng cần chọn mức phù hợp với model, kích thước và chất lượng đang dùng.

| Loại | Đơn vị dự toán |
|---|---|
| Ảnh | USD/lượt tạo mới |
| Video AI | USD/giây video được yêu cầu |
| Lồng tiếng | USD/1.000 ký tự .NET (`string.Length`) |
| Kịch bản/phân cảnh | USD/lượt |
| Nhận diện | USD/phút audio đầu vào |

Giới hạn trên toàn vòng đời dự án: trên 0 đến 10.000 USD; giá mỗi đơn vị 0–100 USD hoặc chưa biết. Làm tròn dự toán lên micro-USD theo từng loại. Lịch sử trước v0.15 chưa được truy tính; không tự đọc hóa đơn nhà cung cấp.

- Nhận yêu cầu ảnh/video/phân cảnh: kiểm tra cache/idempotency trước, rồi lưu tác vụ và khoản giữ dự toán trong **một SaveChanges/transaction**. Yêu cầu trùng/cache không giữ thêm tiền.
- Quy trình một nút dự toán và giữ toàn batch gồm media mới + TTS trước khi tạo hàng đợi. Không giữ thêm khoản cho từng child. Plan chỉ tính thử, không giữ tiền.
- TTS/nhận diện/kịch bản trực tiếp giữ dự toán trước HTTP tính phí. Key thiếu, dữ liệu sai hoặc vượt ngân sách không gửi provider và không tạo khoản giữ mới. API kịch bản trực tiếp giờ cần `projectId` khi có key.
- Semaphore dùng chung bảo vệ kiểm tra và ghi sổ với cấu hình/đối soát: hai yêu cầu đồng thời không cùng vượt phần ngân sách còn lại. Áp dụng cho một instance backend, như giới hạn triển khai SQLite hiện có.
- Khi bật chặn, giá chưa biết hoặc sổ có khoản chưa định giá sẽ chặn yêu cầu AI mới. Điền giá, lưu, rồi **Định giá khoản chưa rõ**. Giá mới không viết lại dự toán cũ đã biết.
- Lỗi, hủy, Unknown và restart giữ nguyên khoản dự toán: không thể suy ra provider đã hoàn phí. Người dùng có thể đối soát số tiền thực tế, kèm ghi chú; vẫn giữ dự toán gốc.
- Dashboard hiển thị 200 khoản gần nhất; tổng tính toàn bộ lịch sử. Xuất JSON toàn sổ tối đa 50.000 khoản. Nút tải lại lấy tình trạng hiện tại, không tự thăm dò hóa đơn.
- Không áp ngân sách AI cho render/backup/các thao tác local không gọi provider. Yêu cầu đã nhận từ trước khi bật giới hạn không bị thu hồi.

## Backup và khôi phục dự án

ZIP `flowvideo.project` schema 1 gồm storyboard đã lưu, toàn bộ media đã đăng ký, favorite/tag/note, thư viện hồ sơ, tối đa 20 checkpoint + 20 preset. Manifest chứa byte count và SHA-256 của từng media.

Giới hạn: 100 media, tổng media 240 MiB, manifest 2 MiB, ZIP upload 243 MiB, 50 hồ sơ. File xuất dùng no-compression để giảm CPU. API kiểm tra file nguồn trước khi tạo backup; dữ liệu danh mục được đọc trong transaction.

Khôi phục kiểm tra format/version, số lượng và đường dẫn entry, entry trùng, file thiếu/thừa, kích thước khai báo và **số byte thực giải nén**, SHA-256, scope và cấu hình document. Không gọi ExtractToDirectory: mỗi media được ghi vào tên ngẫu nhiên do server tạo. Lưu vào dự án mới với ID project/media/scene/profile được ánh xạ lại. Lỗi kiểm tra rollback database và dọn file; giữ nguyên dự án nguồn.

ZIP **không chứa** video xuất, lịch sử job, trạng thái AI/workflow, báo cáo QA, sổ ngân sách hoặc cấu hình key backend. Không tự chạy lại AI khi khôi phục. Đây là bản sao dự án có thể mang sang máy khác, chưa phải snapshot toàn SQLite/data directory hoặc công cụ backup theo lịch. Ngân sách của dự án mới mặc định tắt, sổ trống; sổ tài chính nguồn vẫn ở dự án nguồn.

## API chính

| Endpoint | Chức năng |
|---|---|
| GET/POST `/api/projects/{id}/profiles` | Danh sách/tạo hồ sơ |
| PUT/DELETE `/api/projects/{id}/profiles/{profileId}` | Sửa có version/xóa |
| POST `/api/projects/{id}/profiles/resolve` | Chụp hồ sơ cho cảnh |
| GET `.../profiles/export`, POST `.../profiles/import` | Gói hồ sơ JSON |
| GET `/api/projects/{id}/backup` | ZIP bản đã lưu |
| POST `/api/backups/restore` | Multipart `file`, tạo dự án mới |
| GET/PUT `/api/projects/{id}/budget` | Dashboard/cấu hình có version |
| POST `.../budget/quote` | Mảng `{ kind, units }`, chỉ dự toán |
| POST `.../budget/reprice` | Định giá khoản chưa rõ |
| PUT `.../budget/entries/{entryId}` | `{ actualUsd, note }` |
| GET `.../budget/export` | Xuất toàn sổ JSON |

Xem DTO trong `CreativeProfiles.cs`, `ProjectBackup.cs`, `AiBudget.cs`, `Branding.cs`, `ClipTransitions.cs`. Không thêm dependency runtime mới. Tham khảo: [FFmpeg drawtext](https://ffmpeg.org/ffmpeg-filters.html#drawtext), [overlay](https://ffmpeg.org/ffmpeg-filters.html#overlay), [xfade](https://ffmpeg.org/ffmpeg-filters.html#xfade), [System.IO.Compression](https://learn.microsoft.com/dotnet/api/system.io.compression.ziparchive).
