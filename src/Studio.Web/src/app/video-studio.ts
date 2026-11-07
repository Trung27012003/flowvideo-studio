import { Component, computed, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SceneDraft } from './storyboard';
export interface VideoRequestOptions {
  sceneIds: string[];
  seconds: number;
  resolution: string;
  referenceId: string | null;
}
export interface VideoRow {
  generation: {
    id: string;
    sceneId: string;
    status: string;
    message: string;
    prompt: string;
    model: string;
    size: string;
    quality: string;
    seconds: number;
  };
  asset: { id: string; name: string; kind: string } | null;
}
@Component({
  selector: 'app-video-studio',
  imports: [FormsModule],
  template: `<details class="video-studio">
    <summary>Tạo video AI · Gemini Omni</summary>
    <p>
      Gửi prompt và ảnh tham chiếu (tùy chọn) tới Google. Có thể phát sinh phí. Giới hạn ứng dụng
      {{ limit() }} lượt/24 giờ, đã dùng {{ used() }}. Không tự gửi lại khi lỗi hoặc chưa rõ kết
      quả.
    </p>
    @if (!enabled()) {
      <p>Cấu hình GEMINI_API_KEY ở backend để bật. Các video đã tạo vẫn xem và dùng được.</p>
    }
    <fieldset [disabled]="disabled() || !enabled()">
      <div class="settings">
        <label
          >Thời lượng video AI (s)<input
            type="number"
            min="3"
            max="15"
            step="1"
            [(ngModel)]="seconds"
        /></label>
        <label
          >Độ phân giải video AI<select aria-label="Độ phân giải video AI" [(ngModel)]="resolution">
            <option value="360p">360p · bản nháp</option>
            <option value="720p">720p</option>
            <option value="1080p">1080p</option>
          </select></label
        >
        <label
          >Ảnh tham chiếu video AI<select
            aria-label="Ảnh tham chiếu video AI"
            [(ngModel)]="referenceId"
          >
            <option value="">Không dùng ảnh</option>
            @for (a of images(); track a.id) {
              <option [value]="a.id">{{ a.name }}</option>
            }
          </select></label
        >
      </div>
      <p>
        Tỉ lệ theo storyboard: {{ ratio() }}. Hỗ trợ 9:16 và 16:9. Thời lượng video AI độc lập với
        thời lượng cảnh khi ghép.
      </p>
      @for (s of scenes(); track s.id) {
        <div class="scene-action">
          <span>{{ s.title }} · {{ s.prompt ? s.prompt.slice(0, 100) : 'Chưa có prompt' }}</span
          ><button
            [disabled]="!s.prompt.trim() || active(s.id) || !valid()"
            (click)="create([s.id])"
          >
            Tạo video AI · {{ s.title }}
          </button>
        </div>
      }
      <button [disabled]="!eligible().length || !valid()" (click)="create(eligible())">
        Xếp hàng video cho {{ eligible().length }} cảnh bật
      </button>
    </fieldset>
    <section aria-label="Lịch sử video AI">
      <h3>Video AI của dự án</h3>
      @for (row of rows(); track row.generation.id) {
        <article>
          @if (row.asset) {
            <video
              controls
              preload="none"
              [src]="'/api/assets/' + row.asset.id + '/file'"
              [attr.aria-label]="'Xem video AI ' + row.generation.id"
            ></video>
          }
          <div>
            <b>{{ status(row.generation.status) }}</b>
            <p>{{ row.generation.prompt.slice(0, 140) }}</p>
            <p>{{ row.generation.message }}</p>
            <small
              >{{ row.generation.model }} · {{ row.generation.seconds }}s ·
              {{ row.generation.size }} · {{ row.generation.quality }}</small
            >
          </div>
          @if (row.asset) {
            <button
              [disabled]="disabled() || !exists(row.generation.sceneId)"
              (click)="apply.emit(row)"
            >
              Dùng video cho cảnh</button
            ><a [href]="'/api/assets/' + row.asset.id + '/file'" [download]="row.asset.name"
              >Tải video AI</a
            >
          }
          @if (row.generation.status === 'Queued') {
            <button [disabled]="disabled()" (click)="cancel.emit(row.generation.id)">
              Hủy video chưa gửi
            </button>
          }
        </article>
      } @empty {
        <p>Chưa có lượt tạo video AI.</p>
      }
      <p>
        Dùng video chỉ đổi bản nháp, giữ thời lượng cảnh. Lưu storyboard để giữ, hoặc Lưu & dựng để
        ghép với giọng đọc, nhạc và phụ đề.
      </p>
    </section>
  </details>`,
  styles: [
    `
      :host {
        display: block;
        margin: 20px 0;
      }
      details {
        border: 1px solid #334155;
        border-radius: 14px;
        padding: 18px;
      }
      summary {
        font-weight: 700;
        cursor: pointer;
      }
      p,
      small {
        color: #91a3ba;
        font-size: 12px;
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
        gap: 12px;
      }
      label {
        display: flex;
        flex-direction: column;
        gap: 7px;
        font-size: 12px;
      }
      input,
      select {
        width: 100%;
      }
      .scene-action,
      article {
        display: flex;
        gap: 12px;
        align-items: center;
        flex-wrap: wrap;
        margin: 12px 0;
        padding: 12px;
        background: #111d30;
        border-radius: 10px;
      }
      .scene-action span,
      article div {
        flex: 1;
        min-width: 160px;
      }
      video {
        width: 160px;
        max-height: 200px;
      }
      a {
        color: #22d3ee;
        font-size: 12px;
      }
      @media (max-width: 700px) {
        .settings {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class VideoStudio {
  scenes = input<SceneDraft[]>([]);
  assets = input<{ id: string; name: string; kind: string }[]>([]);
  rows = input<VideoRow[]>([]);
  enabled = input(false);
  disabled = input(false);
  used = input(0);
  limit = input(5);
  ratio = input('9:16');
  generate = output<VideoRequestOptions>();
  cancel = output<string>();
  apply = output<VideoRow>();
  seconds = 5;
  resolution = '720p';
  referenceId = '';
  images = computed(() => this.assets().filter((a) => a.kind === 'image'));
  eligible = computed(() =>
    this.scenes()
      .filter((s) => s.options?.enabled !== false && s.prompt.trim() && !this.active(s.id))
      .map((s) => s.id),
  );
  active(id: string) {
    return this.rows().some(
      (r) => r.generation.sceneId === id && ['Queued', 'Running'].includes(r.generation.status),
    );
  }
  exists(id: string) {
    return this.scenes().some((s) => s.id === id);
  }
  valid() {
    return (
      Number.isInteger(this.seconds) &&
      this.seconds >= 3 &&
      this.seconds <= 15 &&
      ['9:16', '16:9'].includes(this.ratio())
    );
  }
  create(sceneIds: string[]) {
    if (this.valid())
      this.generate.emit({
        sceneIds,
        seconds: this.seconds,
        resolution: this.resolution,
        referenceId: this.referenceId || null,
      });
  }
  status(value: string) {
    return (
      (
        {
          Queued: 'Đang chờ',
          Running: 'Đang tạo',
          Succeeded: 'Đã tạo',
          Failed: 'Lỗi',
          Unknown: 'Chưa rõ kết quả',
          Canceled: 'Đã hủy',
        } as Record<string, string>
      )[value] || value
    );
  }
}
