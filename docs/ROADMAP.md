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

## V0.3 — chất lượng nền tảng (2–3 tuần)

- ffprobe kiểm tra file ngay sau upload; duration, thumbnail, waveform
- EF migrations, giới hạn dung lượng, dọn orphan/temp files
- Lưu script và preset; theo dõi render progress thực từ FFmpeg
- Mở rộng API validation tests, accessibility và browser E2E CI

## V0.4 — giọng và phụ đề (2–3 tuần)

- Một provider TTS và STT, nghe thử và tạo lại từng câu
- Timestamp từ provider/alignment; editor subtitle và xuất SRT/ASS
- Từ điển phát âm, auto-ducking và loudness normalization

## V0.5 — storyboard và một nút bấm (3–4 tuần)

- Script có cấu trúc, Scene, Asset provenance và TimelineRevision
- Voice timing quyết định duration thực tế
- Tự tạo video từ media có sẵn, trạng thái từng bước và resume
- Trần ngân sách; cache kết quả và invalidation khi sửa cảnh

## V0.6 — AI image/video (3–4 tuần)

- Một provider image và một provider video có API chính thức
- Capability matrix; ảnh tham chiếu và hồ sơ nhân vật
- Persist provider request ID, kiểm tra trạng thái trước retry tính phí
- Tạo lại từng cảnh, lựa chọn kết quả, usage/cost ledger

## V0.7 — editor, loop và clip thông minh (4–6 tuần)

- Timeline trim/split/reorder/undo, proxy và render preview
- Loop crossfade/ping-pong, preview điểm nối và audio loop riêng
- Transcript selection, highlight đề xuất bởi AI, crop thủ công trước auto-tracking

## V1 — phân phối

- Đóng gói Windows bằng WebView2 hoặc triển khai server có auth
- PostgreSQL + worker riêng khi cần multi-user
- Kết nối xuất bản bằng API chính thức sau khi có quyền phù hợp

Thời gian là dự trù cho một dev làm tập trung, cần điều chỉnh sau mỗi giai đoạn. MVP hiện tại chưa thực hiện toàn bộ trải nghiệm một nút bấm đã mô tả ở kế hoạch sản phẩm.
