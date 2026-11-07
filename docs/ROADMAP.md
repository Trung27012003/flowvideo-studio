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

## V0.10 — đợt 30 chức năng đã bổ sung

- [x] 10 tùy chỉnh riêng cảnh: enabled, fit, brightness, rotation, speed, zoom/focus, caption, fade và note.
- [x] 10 tùy chỉnh phụ đề: font, size, color/outline, shadow, bold, position/margin và box.
- [x] 10 công cụ thư viện: copy/hash/usage/bulk rename/tạo-thay cảnh và metadata yêu thích/nhãn/ghi chú/lọc.
- [x] Kiểm tra pixel và luồng UI/JSON thực tế; [danh sách đủ 30 mục](BATCH-V10.md).

## Tiếp theo — chất lượng nền tảng (2–3 tuần)

- ffprobe kiểm tra file ngay sau upload; duration, thumbnail, waveform
- EF migrations, giới hạn dung lượng, dọn orphan/temp files
- Lưu script và preset; theo dõi render progress thực từ FFmpeg
- Mở rộng API validation tests, accessibility và browser E2E CI

## Tiếp theo — hoàn thiện audio (2–3 tuần)

- Hàng đợi AI bền vững, tạo lại từng câu và theo dõi chi phí
- Alignment theo từng từ, editor dạng bảng và xuất ASS
- Từ điển phát âm và tự chọn cấu hình theo nguồn; ducking/loudness/filter/EQ đã có

## Tiếp theo — tự động hóa storyboard (3–4 tuần)

- AI chia cảnh có cấu trúc và provenance media; checkpoints khôi phục đã có
- Voice timing quyết định duration thực tế
- Tự tạo giọng/tư liệu cho từng cảnh, trạng thái từng bước và resume
- Trần ngân sách; cache kết quả và invalidation khi sửa cảnh

## Tiếp theo — sửa video và nhiều ảnh tham chiếu

- Provider video chính thức đã có ở v0.12; tiếp theo là sửa/extend video và nhiều ảnh tham chiếu
- Capability matrix; ảnh tham chiếu và hồ sơ nhân vật
- Hoàn thiện đối soát request và số tiền sử dụng theo provider
- Tạo lại từng cảnh, lựa chọn kết quả, usage/cost ledger

## Tiếp theo — editor, loop và clip thông minh

- Timeline trim/split/reorder/undo, proxy và render preview
- Loop crossfade/ping-pong, preview điểm nối và audio loop riêng
- Transcript selection, highlight đề xuất bởi AI, crop thủ công trước auto-tracking

## V1 — phân phối

- Đóng gói Windows bằng WebView2 hoặc triển khai server có auth
- PostgreSQL + worker riêng khi cần multi-user
- Kết nối xuất bản bằng API chính thức sau khi có quyền phù hợp

Thời gian là dự trù cho một dev làm tập trung, cần điều chỉnh sau mỗi giai đoạn. MVP hiện tại chưa thực hiện toàn bộ trải nghiệm một nút bấm đã mô tả ở kế hoạch sản phẩm.

## V0.11 — 30 công cụ audio/bàn giao/SRT đã có

- [x] 15 chỉnh giọng/nhạc lưu cùng document/job/preset.
- [x] 8 công cụ bản xuất: WAV, FLAC, mute, GIF, poster, contact sheet, thông số, ZIP/SHA-256.
- [x] 7 công cụ SRT mới. Chi tiết: [BATCH-V11](BATCH-V11.md).

## V0.12 — video AI đã tích hợp

- [x] Gemini Interactions REST, prompt hoặc một ảnh tham chiếu.
- [x] Duration/resolution/aspect ratio, single/batch queue, cache/quota/idempotency/cancel Queued.
- [x] Lịch sử bền vững, Unknown không retry, kiểm tra MP4, lưu library, preview/download/apply rõ ràng.
- [x] Contract tests mô phỏng và render/browser thật; nguồn tham khảo và giới hạn: [VIDEO-AI](VIDEO-AI.md).
- [ ] Kiểm tra tài khoản/model/chất lượng/giá bằng API key thật của người dùng.
- [x] Durable workflow từ storyboard/kịch bản → tư liệu + giọng → dựng đã có ở v0.13.
- [ ] Ý tưởng → AI phân cảnh có cấu trúc → toàn bộ video.
- [x] Loop reverse/ping-pong/crossfade đã có ở v0.13.
- [ ] Timeline nhiều track; nhiều ảnh tham chiếu và video editing/extension.

## V0.13 — sản xuất tự động và loop đã có

- [x] Plan/check toàn batch, snapshot isolation, script-paragraph scenes, missing-media generation, shared cache/quota.
- [x] Per-scene TTS, voice timing/padding/blank scenes, assembled voice, auto render, progress/events/history.
- [x] Idempotency, cancel owned queued jobs, Unknown không replay AI, local resume, retry render, snapshot/apply.
- [x] Reverse/ping-pong/crossfade với bounds và kiểm chứng frame thật.
- [x] [Production workflow](PRODUCTION.md), [Loops](LOOPS.md), integration/browser kiểm chứng.
