import { Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface ProductionOptions {
  media: string;
  speech: boolean;
  voice: string;
  speed: number;
  autoTiming: boolean;
  padding: number;
  videoSeconds: number;
  videoResolution: string;
  fromScript: boolean;
}
export interface ProductionView {
  id: string;
  status: string;
  message: string;
  documentVersion: number;
  renderId: string | null;
  hasDocument: boolean;
  steps: { sceneId: string; provider: string; speechStatus: string }[];
  events: { at: string; message: string }[];
}
@Component({
  selector: 'app-production-studio',
  imports: [FormsModule],
  template: ` <details class="production">
    <summary>Một nút · Tư liệu → Giọng → Video</summary>
    <p>
      Chạy theo bản chụp storyboard đã lưu. Tự tạo tư liệu còn thiếu, đọc lời từng cảnh, ghép giọng
      và dựng. Bản đang sửa được giữ riêng. Tối đa 10 cảnh bật/600 giây; lời không có giọng sẽ giữ
      phụ đề theo thời lượng cảnh.
    </p>
    <fieldset [disabled]="disabled()">
      <div class="settings">
        <label
          >Nguồn cảnh quy trình<select
            aria-label="Nguồn cảnh quy trình"
            [(ngModel)]="options.fromScript"
          >
            <option [ngValue]="false">Storyboard đang sửa</option>
            <option [ngValue]="true">Mỗi đoạn kịch bản thành một cảnh mới</option>
          </select></label
        >
        <label
          >Tư liệu thiếu trong quy trình<select
            aria-label="Tư liệu thiếu trong quy trình"
            [(ngModel)]="options.media"
          >
            <option value="existing">Chỉ dùng tư liệu đã gắn</option>
            <option value="images" [disabled]="!imageEnabled()">Tạo ảnh AI</option>
            <option value="videos" [disabled]="!videoEnabled()">Tạo video AI</option>
          </select></label
        >
        <label class="check"
          ><input type="checkbox" [(ngModel)]="options.speech" [disabled]="!speechEnabled()" />Tự
          đọc lời từng cảnh</label
        >
        <label
          >Giọng trong quy trình<select
            aria-label="Giọng trong quy trình"
            [(ngModel)]="options.voice"
          >
            @for (v of voices(); track v) {
              <option [value]="v">{{ v }}</option>
            }
          </select></label
        >
        <label
          >Tốc độ đọc quy trình<input
            type="number"
            min="0.5"
            max="1.5"
            step="0.1"
            [(ngModel)]="options.speed"
        /></label>
        <label class="check"
          ><input type="checkbox" [(ngModel)]="options.autoTiming" />Căn thời lượng cảnh theo
          giọng</label
        >
        <label
          >Khoảng nghỉ mỗi cảnh (s)<input
            type="number"
            min="0"
            max="2"
            step="0.1"
            [(ngModel)]="options.padding"
        /></label>
        @if (options.media === 'videos') {
          <label
            >Độ dài clip AI quy trình (s)<input
              type="number"
              min="3"
              max="15"
              step="1"
              [(ngModel)]="options.videoSeconds" /></label
          ><label
            >Độ phân giải AI quy trình<select
              aria-label="Độ phân giải AI quy trình"
              [(ngModel)]="options.videoResolution"
            >
              <option value="360p">360p</option>
              <option value="720p">720p</option>
              <option value="1080p">1080p</option>
            </select></label
          >
        }
      </div>
      <p>
        Ảnh/giọng cần OPENAI_API_KEY, video cần GEMINI_API_KEY. AI có thể tính phí. Hạn mức được giữ
        cho cả quy trình trước khi gửi; đây là số lượt, không phải ngân sách tiền. Lỗi AI không tự
        gửi lại. Giọng tạo mới sẽ thay voice cũ trong bản chụp, đặt tốc độ/trễ/cắt đầu giọng toàn
        phim về mặc định để giữ khớp cảnh.
      </p>
      <div class="actions">
        <button (click)="submit(false)">Kiểm tra và đếm lượt AI</button
        ><button class="primary" (click)="submit(true)">Tạo tư liệu, giọng và dựng</button>
      </div>
    </fieldset>
    @if (estimate(); as e) {
      <p class="estimate" role="status">
        {{ e.scenes }} cảnh · tạo mới {{ e.newImages }} ảnh, {{ e.newVideos }} video,
        {{ e.newSpeech }} giọng · dùng lại {{ e.reusedMedia }} kết quả AI và
        {{ e.existingMedia }} tư liệu đã gắn. Chưa gửi AI.
      </p>
    }
    <h3>Lịch sử quy trình</h3>
    @for (run of rows(); track run.id) {
      <article>
        <header>
          <b>{{ status(run.status) }}</b
          ><small>Storyboard v{{ run.documentVersion }}</small>
        </header>
        <p>{{ run.message }}</p>
        <div class="steps">
          @for (step of run.steps; track step.sceneId) {
            <small
              >{{ provider(step.provider) }} · giọng {{ speechStatus(step.speechStatus) }}</small
            >
          }
        </div>
        <div class="actions">
          @if (active(run.status)) {
            <button [disabled]="disabled()" (click)="action.emit({ id: run.id, action: 'cancel' })">
              Hủy quy trình
            </button>
          }
          @if (run.renderId && run.status === 'Succeeded') {
            <button (click)="preview.emit(run.renderId)">Xem video quy trình</button
            ><a [href]="'/api/jobs/' + run.renderId + '/download'" download>Tải video quy trình</a>
          }
          @if (run.hasDocument) {
            <button [disabled]="disabled()" (click)="apply.emit(run.id)">
              Mở kết quả vào bản nháp
            </button>
          }
          @if (run.renderId && (run.status === 'Failed' || run.status === 'Canceled')) {
            <button
              [disabled]="disabled()"
              (click)="action.emit({ id: run.id, action: 'retry-render' })"
            >
              Dựng lại, không gọi AI
            </button>
          }
          <a
            [href]="'/api/projects/' + projectId() + '/workflows/' + run.id + '/snapshot'"
            [download]="'flowvideo-production-' + run.id + '.json'"
            >Tải bản chụp quy trình</a
          >
        </div>
        <details>
          <summary>Nhật ký các bước</summary>
          @for (event of run.events; track $index) {
            <p>{{ formatTime(event.at) }} · {{ event.message }}</p>
          }
        </details>
      </article>
    } @empty {
      <p>
        Chưa có quy trình. Chế độ chỉ dùng tư liệu đã gắn, không tạo giọng chạy được mà không cần
        key.
      </p>
    }
  </details>`,
  styles: [
    `
      :host {
        display: block;
        margin: 20px 0;
      }
      details.production {
        border: 1px solid #285e56;
        background: #0d1c25;
        border-radius: 14px;
        padding: 18px;
      }
      summary {
        cursor: pointer;
        font-weight: 700;
      }
      p,
      small {
        font-size: 12px;
        color: #a0b4c4;
        line-height: 1.8;
      }
      fieldset {
        border: 0;
        padding: 0;
        min-width: 0;
      }
      .settings {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 14px;
        margin: 14px 0;
      }
      label {
        display: flex;
        flex-direction: column;
        gap: 8px;
        font-size: 12px;
      }
      input,
      select {
        width: 100%;
      }
      .check {
        flex-direction: row;
        align-items: center;
      }
      .check input {
        width: auto;
      }
      .actions,
      header,
      .steps {
        display: flex;
        gap: 12px;
        flex-wrap: wrap;
        align-items: center;
      }
      .steps small {
        border: 1px solid #334155;
        border-radius: 8px;
        padding: 5px;
      }
      article {
        background: #111d30;
        border-radius: 12px;
        padding: 16px;
        margin: 16px 0;
      }
      article details {
        margin-top: 12px;
      }
      .estimate {
        padding: 12px;
        border: 1px solid #285e56;
      }
      a {
        color: #36dcc0;
        font-size: 12px;
      }
      h3 {
        margin-top: 20px;
      }
      @media (max-width: 700px) {
        .settings {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class ProductionStudio {
  rows = input<ProductionView[]>([]);
  projectId = input('');
  estimate = input<any>(null);
  disabled = input(false);
  imageEnabled = input(false);
  videoEnabled = input(false);
  speechEnabled = input(false);
  voices = input<string[]>([]);
  plan = output<ProductionOptions>();
  start = output<ProductionOptions>();
  apply = output<string>();
  preview = output<string>();
  action = output<{ id: string; action: string }>();
  options: ProductionOptions = {
    media: 'existing',
    speech: false,
    voice: 'alloy',
    speed: 1,
    autoTiming: true,
    padding: 0.15,
    videoSeconds: 5,
    videoResolution: '720p',
    fromScript: false,
  };
  submit(run: boolean) {
    (run ? this.start : this.plan).emit({ ...this.options });
  }
  formatTime(value: string) {
    return new Date(value).toLocaleString('vi-VN');
  }
  active(s: string) {
    return ['WaitingAssets', 'Speaking', 'Preparing', 'Rendering'].includes(s);
  }
  status(s: string) {
    return (
      (
        {
          WaitingAssets: 'Chờ tư liệu',
          Speaking: 'Đang tạo giọng',
          Preparing: 'Căn cảnh và ghép giọng',
          Rendering: 'Đang dựng',
          Succeeded: 'Hoàn tất',
          Failed: 'Lỗi',
          Unknown: 'Chưa rõ kết quả AI',
          Canceled: 'Đã hủy',
        } as Record<string, string>
      )[s] || s
    );
  }
  provider(s: string) {
    return (
      (
        { existing: 'Tư liệu có sẵn', images: 'Ảnh AI', videos: 'Video AI' } as Record<
          string,
          string
        >
      )[s] || s
    );
  }
  speechStatus(s: string) {
    return (
      (
        {
          Skipped: 'không tạo',
          Queued: 'chờ',
          Running: 'đang tạo',
          Succeeded: 'đã lưu',
          Unknown: 'chưa rõ',
          Failed: 'lỗi',
        } as Record<string, string>
      )[s] || s
    );
  }
}
