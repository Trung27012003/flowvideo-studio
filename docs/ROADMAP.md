# Kế hoạch phát triển

## V0.1 — nền tảng đã có

- [x] Angular UI, project, media library, SQLite
- [x] FFmpeg concat, direct loop, audio, burn-in SRT
- [x] Durable queue, failure/retry, export/download
- [x] Optional AI script generation
- [x] CI build + real-media smoke test

## V0.2 — đã bổ sung

- [x] Cắt điểm vào/ra từng clip, áp dụng được cho loop
- [x] Hủy job queued/running, dừng FFmpeg và dọn file
- [x] Tránh encode lại khi không có sub; loop một chu kỳ nguồn; cache cảnh trùng trong job
- [x] Polling thích ứng, phản hồi cũ không ghi đè state, hủy fetch khi đóng component
- [x] Regression tests trim/cancel và browser smoke test trên CI

## V0.3 — giọng và phụ đề đã bổ sung

- [x] OpenAI TTS, chọn giọng/tốc độ, nghe thử, lưu MP3 và chọn trong Studio
- [x] Whisper nhận diện audio/video thành SRT có timestamp
- [x] Editor SRT, kiểm tra mốc/nội dung, lưu bản mới và tải xuống
- [x] HTTP contract tests dùng provider mô phỏng; render thật với audio/sub đã tạo
- [x] Browser test editor/download; không gọi API tính phí trong CI

## V0.4 — storyboard đã bổ sung

- [x] Lưu ý tưởng, kịch bản và cảnh theo dự án
- [x] Chia đoạn thành cảnh, gắn tư liệu, sắp xếp và chỉnh thời lượng/điểm vào
- [x] Phụ đề theo cảnh, giọng/nhạc chung, lưu và xếp hàng dựng
- [x] Kiểm tra phiên bản để tránh ghi đè khi nhiều tab cùng sửa
- [x] Cấu hình job không bị đổi bởi lần lưu storyboard tiếp theo
- [x] Nâng cấp database cũ và kiểm thử reload/đảo cảnh trên trình duyệt

## V0.5 — ảnh AI đã bổ sung

- [x] Tạo ảnh từ prompt của cảnh qua OpenAI, một PNG/request
- [x] Worker ảnh, trạng thái lưu bền vững và tiếp tục các yêu cầu queued
- [x] Request ID chống gửi trùng, reuse ảnh cùng prompt/cấu hình
- [x] Giới hạn lượt trong 24 giờ, Unknown khi kết quả không rõ, không tự retry
- [x] Lưu thư viện và gắn vào cảnh bằng thao tác rõ ràng
- [x] Provider contract tests và browser UI contract với upstream mô phỏng

## V0.6 — đợt 10 chức năng đã bổ sung

- [x] Tìm thư viện không dấu
- [x] Lọc thư viện theo loại
- [x] Xem trước media/SRT bằng dialog
- [x] Nhân bản cảnh
- [x] Xuất JSON storyboard
- [x] Nhập JSON thành dự án mới
- [x] Tải SRT của render hoàn tất
- [x] Preset Shorts/YouTube/720p
- [x] Mixer âm lượng giọng và nhạc
- [x] Render Full HD 1080p

## V0.7 — đợt 30 chức năng đã bổ sung

- [x] 8 chức năng dự án/thư viện: rename, clone media độc lập, metadata, sort, chọn hàng loạt, ZIP/CSV.
- [x] 13 chức năng storyboard: lịch sử cảnh, split, sắp xếp/thời lượng/lời/media hàng loạt, thống kê và TXT/shotlist.
- [x] 6 công cụ SRT/WebVTT: offset, tốc độ, tìm/thay, wrap, merge và xuất VTT.
- [x] 3 chức năng dựng: contain/cover, fade video/audio và sidechain duck nhạc.
- [x] Danh sách và giới hạn từng mục: [BATCH-V07](BATCH-V07.md).

## V0.8 — đợt 30 chức năng đã bổ sung

- [x] 16 hiệu chỉnh dựng hình và audio, lưu cùng storyboard/job.
- [x] Thumbnail, ảnh sóng âm, trích frame và tách audio.
- [x] 6 công cụ phụ đề: cleanup, dedupe, CPS, TXT, split và clamp.
- [x] 4 tiện ích hàng đợi/bản xuất: filter, sort, recipe và MP3.
- [x] Hướng dẫn từng mục: [BATCH-V08](BATCH-V08.md).

## V0.9 — đợt 30 chức năng đã bổ sung

- [x] Mốc lưu/khôi phục/so sánh và preset cá nhân có SQLite.
- [x] Nhập/xuất/đổi tên/xóa mục, summary và preflight metadata.
- [x] Multiselect/batch scenes, reading timing, shuffle và xuất nhóm.
- [x] SRT sort/overlap/minimum/whitespace/case và bảng timeline.
- [x] Hai aspect ratio từ cùng snapshot, repeat Succeeded, queue stats/search.
- [x] Danh sách đủ 30 mục: [BATCH-V09](BATCH-V09.md).

## Tiếp theo — chất lượng nền tảng (2–3 tuần)

- ffprobe kiểm tra file ngay sau upload; duration, thumbnail, waveform
- EF migrations, giới hạn dung lượng, dọn orphan/temp files
- Lưu script và preset; theo dõi render progress thực từ FFmpeg
- Mở rộng API validation tests, accessibility và browser E2E CI

## Tiếp theo — hoàn thiện audio (2–3 tuần)

- Hàng đợi AI bền vững, tạo lại từng câu và theo dõi chi phí
- Alignment theo từng từ, editor dạng bảng và xuất ASS
- Từ điển phát âm, auto-ducking và loudness normalization

## Tiếp theo — tự động hóa storyboard (3–4 tuần)

- AI chia cảnh có cấu trúc, provenance media và lịch sử bản lưu có thể khôi phục
- Voice timing quyết định duration thực tế
- Tự tạo giọng/tư liệu cho từng cảnh, trạng thái từng bước và resume
- Trần ngân sách; cache kết quả và invalidation khi sửa cảnh

## V0.7 — video AI và ảnh tham chiếu (3–4 tuần)

- Một provider video có API chính thức; image-to-image và ảnh tham chiếu
- Capability matrix; ảnh tham chiếu và hồ sơ nhân vật
- Hoàn thiện đối soát request và số tiền sử dụng theo provider
- Tạo lại từng cảnh, lựa chọn kết quả, usage/cost ledger

## V0.8 — editor, loop và clip thông minh (4–6 tuần)

- Timeline trim/split/reorder/undo, proxy và render preview
- Loop crossfade/ping-pong, preview điểm nối và audio loop riêng
- Transcript selection, highlight đề xuất bởi AI, crop thủ công trước auto-tracking

## V1 — phân phối

- Đóng gói Windows bằng WebView2 hoặc triển khai server có auth
- PostgreSQL + worker riêng khi cần multi-user
- Kết nối xuất bản bằng API chính thức sau khi có quyền phù hợp

Thời gian là dự trù cho một dev làm tập trung, cần điều chỉnh sau mỗi giai đoạn. MVP hiện tại chưa thực hiện toàn bộ trải nghiệm một nút bấm đã mô tả ở kế hoạch sản phẩm.
