# FlowVideo Studio

[![Build and media smoke test](https://github.com/Trung27012003/flowvideo-studio/actions/workflows/ci.yml/badge.svg)](https://github.com/Trung27012003/flowvideo-studio/actions/workflows/ci.yml)

Studio video cá nhân bằng **Angular 21 + ASP.NET Core / .NET 10 + FFmpeg**. Giao diện tiếng Việt, hàng đợi render lưu bền vững và kho media local.

![Giao diện FlowVideo Studio](docs/studio-preview.png)

## Trạng thái v0.12

**Đã triển khai:** tạo/chọn dự án; nhập ảnh, video, audio, SRT; chọn và sắp xếp cảnh; cắt điểm vào/ra từng clip; ghép ảnh/video; loop trực tiếp một nguồn; thêm giọng đọc từ file và nhạc nền; burn-in SRT; hàng đợi, hủy job đang chờ/đang chạy, thử lại job lỗi; preview/tải MP4; tạo kịch bản, giọng đọc AI và phụ đề từ lời nói khi cấu hình API key; chỉnh sửa/lưu SRT không cần AI; lưu kịch bản và storyboard theo dự án; dựng theo thời lượng từng cảnh và tạo phụ đề từ lời cảnh; tạo ảnh AI theo prompt từng cảnh với hàng đợi, lịch sử và giới hạn lượt; tạo video AI từ prompt/ảnh tham chiếu qua Gemini, xếp hàng nhiều cảnh và gắn kết quả vào storyboard.

**Chưa triển khai:** sửa ảnh theo ảnh tham chiếu, editor timeline nhiều track, loop crossfade/ping-pong, đăng mạng xã hội, tự động tạo toàn bộ tư liệu/giọng đọc từ ý tưởng. Các chức năng này được mô tả trong [roadmap](docs/ROADMAP.md), không giả lập kết quả AI.

## Chạy phát triển

Yêu cầu: .NET 10 SDK, Node.js 24, FFmpeg/ffprobe trong PATH, Python 3 nếu chạy smoke test. Cài font hỗ trợ tiếng Việt, ví dụ DejaVu Sans.

```bash
cd src/Studio.Web
npm ci
cd ../..
bash scripts/dev.sh
```

Mở **http://127.0.0.1:4200**. API chạy tại `http://127.0.0.1:5080`. Angular proxy chuyển `/api` tới backend. Trên Windows, chạy hai lệnh trong hai terminal: `dotnet run --project src/Studio.Api --urls http://127.0.0.1:5080` và `npm start` trong `src/Studio.Web`.

Tạo dự án → nhập media → chọn cảnh theo thứ tự → chỉnh điểm vào/ra từng clip nếu cần → chọn audio/SRT nếu cần → **Tạo & chạy video**. Hàng đợi tải 100 job gần nhất; tự cập nhật mỗi 2,5 giây khi có tác vụ đang xử lý, 15 giây khi rảnh và tạm ngừng khi tab bị ẩn. Khi hoàn tất, xem hoặc tải MP4.

## AI tùy chọn

Đặt biến môi trường `OPENAI_API_KEY` trong terminal chạy backend; `OPENAI_MODEL` mặc định `gpt-4o-mini`. Không đặt key trong Angular hay commit vào Git. File `.env.example` chỉ là mẫu; ứng dụng **không tự đọc `.env`**. Tạo kịch bản, giọng đọc và nhận diện phụ đề đều có thể phát sinh phí. `OPENAI_TTS_MODEL` mặc định `gpt-4o-mini-tts`; STT dùng `whisper-1` để lấy mốc thời gian. Cần model được tài khoản cho phép sử dụng.

Bấm **Lưu kịch bản** để giữ nội dung theo dự án. Có thể chia các đoạn thành cảnh, chọn tư liệu và dựng bằng storyboard; Ảnh có thể tạo riêng bằng nút AI cho từng cảnh; chưa tự tạo toàn bộ tư liệu cho cả kịch bản.

## Video AI v0.12 và 30 công cụ mới v0.11

**Video AI:** prompt → MP4 qua Google Gemini Interactions API; ảnh tham chiếu, 3–15 giây, 360p/720p/1080p, dọc/ngang, tạo một cảnh hoặc xếp hàng nhiều cảnh, lưu lịch sử, reuse/quota/idempotency, hủy Queued, xem/tải/gắn vào storyboard. Cần `GEMINI_API_KEY`; mặc định `gemini-omni-1.2-flash`, giới hạn 5 lượt/24 giờ. Xem [hướng dẫn, nguồn chính thức và giới hạn kiểm chứng](docs/VIDEO-AI.md). Chưa kiểm tra bằng API key thật; CI không phát sinh phí AI.

**30 công cụ local:** 15 hiệu chỉnh giọng/nhạc (lọc tần số, giảm nhiễu/compressor/EQ, balance, trim/fade, speed và loop nhạc); 8 công cụ bàn giao (WAV/FLAC/mute/GIF/poster/contact sheet/metadata/ZIP có manifest SHA-256); 7 công cụ SRT (sentence/punctuation/search/range/merge/emoji/CSV). Danh sách và cách dùng: [BATCH-V11.md](docs/BATCH-V11.md).

## Đợt 30 chức năng v0.10

Thêm **10 tùy chỉnh riêng từng cảnh** (bật/tắt, vừa khung, sáng/xoay, speed/zoom/tâm, cue/fade và ghi chú), **10 tùy chỉnh kiểu phụ đề** và **10 công cụ thư viện** (copy qua dự án, hash file trùng, nơi dùng, tên hàng loạt, tạo/thay cảnh, yêu thích/nhãn/ghi chú/lọc).

Danh sách đủ 30 mục, hướng dẫn và giới hạn: [BATCH-V10.md](docs/BATCH-V10.md). Mặc định JSON cũ tương thích; metadata bổ sung bảng AssetPreferences. Không cần API key, không thay file nguồn.

## Đợt 30 chức năng v0.9

Thêm **mốc khôi phục và preset cá nhân lưu SQLite**: lưu/mở/so sánh/đổi tên/xóa/xuất mốc, lưu/áp/đổi tên/xóa và mang preset qua JSON. Có tổng quan dữ liệu và kiểm tra trước khi dựng, chọn nhiều cảnh, nhân bản/xóa/di chuyển/xuất nhóm, trộn thứ tự và căn thời lượng theo lời. Thêm 6 công cụ timeline SRT, dựng hai tỉ lệ dọc/ngang một lượt, dựng lại snapshot job hoàn tất, thống kê và tìm tác vụ.

Danh sách đủ **30 mục**, hướng dẫn và giới hạn: [BATCH-V09.md](docs/BATCH-V09.md). Upgrade bổ sung bảng `SavedWorkspaceItems`, không viết lại dữ liệu cũ; tối đa 20 mốc và 20 preset/dự án. Không cần API key.

## Đợt 30 chức năng v0.8

Thêm **16 tùy chỉnh khi dựng**: sáng/tương phản/bão hòa, lật ngang/dọc, xoay, vignette, sharpen, màu viền, fps, chất lượng, mono, điểm nhạc, trễ/tốc độ và chuẩn hóa giọng. **4 công cụ media** tạo thumbnail/sóng âm, lưu khung hình và tách audio vào thư viện. **6 công cụ SRT** xử lý định dạng/lời trùng, phân tích tốc độ đọc, xuất TXT, tách câu và cắt theo thời lượng. **4 tiện ích job** lọc, sắp xếp, tải recipe JSON và audio MP3 của bản xuất.

Danh sách đủ 30 mục, hướng dẫn và giới hạn: [BATCH-V08.md](docs/BATCH-V08.md). Không cần API key, không thay media gốc hoặc schema database. Các hiệu chỉnh được áp dụng khi render; preview nguồn giữ nguyên.

## Đợt 30 chức năng v0.7

Thêm đổi tên/nhân bản dự án kèm media độc lập; đổi tên và xem thông số media; sắp xếp/chọn hàng loạt/tải ZIP/xuất CSV thư viện; hoàn tác/làm lại và tách đôi cảnh; đảo/đưa cảnh về đầu/cuối; chỉnh thời lượng, đánh số, tìm/thay lời và gán media hàng loạt; đếm từ, xuất lời TXT/shotlist CSV; 6 công cụ SRT/WebVTT; contain/cover, fade và tự hạ nhạc dưới giọng.

Danh sách đủ **30 mục**, hướng dẫn và giới hạn: [BATCH-V07.md](docs/BATCH-V07.md). Tất cả chạy không cần API key. Không thêm bảng DB; cấu hình cũ giữ mặc định tương thích.

## Đợt 10 chức năng v0.6

1. Tìm tên tài nguyên, hỗ trợ gõ tiếng Việt không dấu.
2. Lọc thư viện theo ảnh/video/audio/phụ đề.
3. Xem trước ảnh, video, audio và SRT ngay trong ứng dụng.
4. Nhân bản cảnh với ID mới, giữ tư liệu và nội dung.
5. Xuất storyboard JSON sau khi lưu.
6. Nhập JSON thành dự án mới, giữ nguyên dự án cũ.
7. Tải SRT đã dùng khi render hoàn tất.
8. Preset Shorts, YouTube và bản nháp 720p.
9. Chỉnh âm lượng giọng/nhạc 0–200%, có limiter chống quá đỉnh.
10. Xuất 1080p ở Studio và Storyboard, giữ tùy chọn 720p.

Tất cả chức năng của đợt này dùng được không cần API key. JSON không kèm media: nhập sẽ bỏ liên kết tư liệu/audio và tạo ID cảnh mới; chọn lại media trước khi dựng. Xem [hướng dẫn v0.6](docs/BATCH-V06.md).

## Ảnh AI v0.5

Trong Storyboard, nhập prompt cho cảnh → **Tạo ảnh AI cho cảnh**. Ứng dụng lưu storyboard và xếp hàng; ảnh thành công xuất hiện trong thư viện và danh sách ảnh AI. Bấm **Dùng ảnh cho cảnh**, rồi lưu hoặc dựng storyboard.

Cần `OPENAI_API_KEY` và quyền dùng `OPENAI_IMAGE_MODEL` (mặc định `gpt-image-1`). Mặc định tạo một PNG, quality low, kích thước dọc/ngang gần nhất do model hỗ trợ. Yêu cầu có thể tính phí. Giới hạn mặc định 10 yêu cầu đã nhận trong 24 giờ cho toàn ứng dụng; `IMAGE_DAILY_LIMIT` đặt trong khoảng 1–100. Đây là giới hạn số lượt, không phải ngân sách tiền.

Mã yêu cầu được ghi trước khi gọi dịch vụ; không tự retry. Prompt/model/kích thước giống một kết quả thành công của cùng cảnh sẽ dùng lại ảnh. Timeout/mất kết nối hoặc restart khi đang chạy được đánh dấu Unknown; dịch vụ có thể đã tính phí. Xem [hướng dẫn ảnh AI](docs/IMAGES.md).

## Storyboard v0.4

Chọn dự án → Kịch bản AI → nhập/tạo kịch bản → **Lưu kịch bản**. **Chia đoạn thành cảnh** thêm mỗi đoạn cách dòng trống thành một cảnh; có thể thêm cảnh thủ công trong Storyboard.

Trong Storyboard, chọn ảnh/video, nhập thời lượng và điểm vào, sửa lời/ghi chú, sắp xếp cảnh, chọn giọng/nhạc rồi bấm **Lưu & dựng storyboard**. Mỗi job giữ cấu hình tại thời điểm tạo. Nội dung đã lưu mở lại theo dự án sau reload; nội dung chưa lưu có thể mất. Trình duyệt nhớ dự án được mở gần nhất khi localStorage khả dụng.

Video ngắn hơn cảnh sẽ giữ khung hình cuối. Phụ đề từ lời cảnh hiển thị trong toàn cảnh, chưa căn theo giọng nói. Storyboard không cần key khi dùng tư liệu có sẵn. Tối đa 30 cảnh và tổng 600 giây. Xem [hướng dẫn storyboard](docs/STORYBOARD.md).

## Giọng đọc và phụ đề v0.3

Mở **Giọng đọc & phụ đề** sau khi chọn dự án:

- Nhập lời đọc, chọn giọng và tốc độ → **Tạo giọng đọc AI**. Audio MP3 được lưu vào thư viện, có thể nghe thử và được chọn sẵn làm giọng trong Studio.
- Chọn audio/video có lời nói → chọn ngôn ngữ → **Nhận diện & tạo SRT**. Phụ đề được lưu, mở trong editor và chọn sẵn trong Studio.
- Mở SRT có sẵn hoặc nhập nội dung trực tiếp → sửa lời/mốc thời gian → **Lưu bản phụ đề**. Mỗi lần lưu tạo một asset mới, giữ nguyên các bản trước. Có thể tải bản đã lưu.

Editor SRT không cần key. AI cần tài khoản có quyền dùng model; đã kiểm thử hợp đồng API bằng provider mô phỏng, chưa kiểm chứng chất lượng AI thực tế bằng key thật. Giới hạn TTS 4.000 ký tự, STT 10 phút/file; xử lý theo request, chưa có hàng đợi AI bền vững. Xem [hướng dẫn audio](docs/AUDIO.md).

## Quy tắc render hiện tại

- Ghép: ảnh 5 giây/cảnh; video giữ thời lượng nguồn trong giới hạn; đầu ra dừng ở thời lượng media hoặc giới hạn người dùng, tùy giá trị nhỏ hơn.
- Cắt clip: nhập điểm vào/ra theo giây trong danh sách cảnh; để trống điểm ra để lấy đến cuối nguồn. Loop lặp phần đã cắt. Điểm vượt thời lượng nguồn sẽ khiến job báo lỗi.
- Loop: chọn đúng một ảnh/video, lặp trực tiếp đến thời lượng đích; không bảo đảm điểm nối liền mạch.
- Đầu ra: H.264/AAC MP4, chọn 24/30/60 fps (mặc định 30); 720×1280/1280×720 hoặc 1080×1920/1920×1080; chọn contain (letterbox) hoặc cover (cắt giữa).
- Audio gốc của clip bị bỏ. Giọng đọc có thể là file audio đã nhập hoặc MP3 tạo bằng TTS. Giọng mặc định 100%, nhạc mặc định 18%, chỉnh được 0–200%; limiter 0,95, tùy chọn hạ nhạc bằng sidechain khi có giọng. Có fade đầu/cuối toàn video/audio 0–2 giây.
- SRT phải là UTF-8. Phụ đề được burn-in và có thể tải SRT sau render mới từ v0.6; sửa nội dung/mốc thời gian ở màn hình Giọng đọc & phụ đề.
- Tối đa 250 MB/file, 30 cảnh/job, 600 giây; một worker render tuần tự.
- Job Queued còn lại được chạy khi mở lại; job Running bị gián đoạn được đánh dấu Failed để thử lại thủ công.

## Dữ liệu và bảo mật

SQLite và media nằm trong `data/` tương đối với backend; có thể đổi bằng `STUDIO_DATA` (nên dùng đường dẫn tuyệt đối). Không commit dữ liệu, file nguồn hay credentials. Sao lưu toàn bộ thư mục khi backend đã dừng.

**Bản này dành cho một người dùng trên localhost.** Chưa có đăng nhập, phân quyền hay cách ly xử lý file không tin cậy. Không mở API lên Internet/LAN trước khi bổ sung authentication, giới hạn tài nguyên và sandbox media. API key chỉ ở backend; thông tin lỗi chi tiết xem trong log backend.

## Build và kiểm thử

```bash
dotnet build src/Studio.Api -c Release
dotnet test tests/Studio.Api.Tests -c Release
npm run test:tools --prefix src/Studio.Web
cd src/Studio.Web && npm ci && npm run build
cd ../..
python3 scripts/smoke.py
```

Có thể kiểm tra giao diện bằng `npx playwright install chromium` rồi `npm run test:e2e` trong `src/Studio.Web` sau khi build cả frontend/backend. Linux có thể cần `npx playwright install --with-deps chromium`. Dùng biến `PLAYWRIGHT_CHROMIUM_EXECUTABLE` nếu muốn chỉ định Chromium đã cài.

Smoke test chạy backend với database tạm, tạo media bằng FFmpeg, kiểm tra ghép, loop, audio + SRT, validation, tải MP4 và dữ liệu sau restart. Cần API port 5097 trống; browser test dùng port 5099.

## Chạy bản build chung một server

```bash
dotnet publish src/Studio.Api -c Release -o publish
mkdir -p publish/wwwroot
cp -r src/Studio.Web/dist/web/browser/. publish/wwwroot/
cd publish
dotnet Studio.Api.dll --urls http://127.0.0.1:5080
```

Trên Windows, copy nội dung thư mục `dist/web/browser` vào `publish/wwwroot` bằng Explorer/PowerShell. CI tạo artifact cùng cấu trúc; máy chạy artifact vẫn cần .NET 10 runtime, FFmpeg và font.

Xem [kết quả tối ưu](docs/PERFORMANCE.md), [kiến trúc](docs/ARCHITECTURE.md) và [kế hoạch](docs/ROADMAP.md). Repository trên GitHub lưu mã nguồn và CI; không phải dịch vụ web được hosting công khai.
