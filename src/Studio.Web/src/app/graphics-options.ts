import { Component, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface TextOverlay {
  text: string;
  start: number;
  end: number;
  position: string;
  fontSize: number;
  color: string;
  boxColor: string;
  boxOpacity: number;
}
export interface Branding {
  texts: TextOverlay[];
  logo: {
    assetId: string;
    start: number;
    end: number;
    position: string;
    width: number;
    opacity: number;
  } | null;
}
export interface Transitions {
  style: string;
  duration: number;
}
export const defaultBranding = (): Branding => ({ texts: [], logo: null });
export const defaultTransitions = (): Transitions => ({ style: 'cut', duration: 0.3 });
@Component({
  selector: 'app-graphics-options',
  imports: [FormsModule],
  template: ` <details>
    <summary>Chữ, logo, CTA và chuyển cảnh</summary>
    <p>
      Tối đa 5 lớp chữ. Thời gian tính từ đầu video; xuống dòng thủ công nếu chữ dài. Logo dùng ảnh
      của dự án.
    </p>
    <fieldset [disabled]="disabled()">
      <button type="button" (click)="add('Tiêu đề video')" [disabled]="options().texts.length >= 5">
        Thêm chữ
      </button>
      <button
        type="button"
        (click)="add('Theo dõi để xem thêm', true)"
        [disabled]="options().texts.length >= 5"
      >
        Thêm CTA
      </button>
      @for (text of options().texts; track $index; let i = $index) {
        <div class="layer">
          <label
            >Nội dung chữ<textarea
              aria-label="Nội dung chữ"
              maxlength="300"
              [ngModel]="text.text"
              (ngModelChange)="patch(i, { text: $event })"
            ></textarea>
          </label>
          <label
            >Bắt đầu chữ (s)<input
              aria-label="Bắt đầu chữ"
              type="number"
              min="0"
              max="599"
              step="0.1"
              [ngModel]="text.start"
              (ngModelChange)="patch(i, { start: $event })"
          /></label>
          <label
            >Kết thúc chữ (s)<input
              aria-label="Kết thúc chữ"
              type="number"
              min="0.1"
              max="600"
              step="0.1"
              [ngModel]="text.end"
              (ngModelChange)="patch(i, { end: $event })"
          /></label>
          <label
            >Vị trí chữ<select
              [ngModel]="text.position"
              (ngModelChange)="patch(i, { position: $event })"
            >
              @for (p of positions; track p) {
                <option [value]="p">{{ p }}</option>
              }
            </select></label
          >
          <label
            >Cỡ chữ<input
              type="number"
              min="12"
              max="96"
              [ngModel]="text.fontSize"
              (ngModelChange)="patch(i, { fontSize: $event })"
          /></label>
          <label
            >Màu chữ<input
              type="color"
              [ngModel]="text.color"
              (ngModelChange)="patch(i, { color: $event })"
          /></label>
          <label
            >Màu nền chữ<input
              type="color"
              [ngModel]="text.boxColor"
              (ngModelChange)="patch(i, { boxColor: $event })"
          /></label>
          <label
            >Độ đậm nền<input
              type="number"
              min="0"
              max="1"
              step="0.1"
              [ngModel]="text.boxOpacity"
              (ngModelChange)="patch(i, { boxOpacity: $event })"
          /></label>
          <button type="button" (click)="remove(i)">Bỏ lớp chữ</button>
        </div>
      }
      <label
        >Ảnh logo<select
          aria-label="Ảnh logo"
          [ngModel]="options().logo?.assetId || ''"
          (ngModelChange)="chooseLogo($event)"
        >
          <option value="">Không dùng</option>
          @for (a of assets(); track a.id) {
            @if (a.kind === 'image') {
              <option [value]="a.id">{{ a.name }}</option>
            }
          }
        </select></label
      >
      @if (options().logo; as logo) {
        <div class="layer">
          <label
            >Vị trí logo<select
              [ngModel]="logo.position"
              (ngModelChange)="patchLogo({ position: $event })"
            >
              @for (p of positions; track p) {
                <option [value]="p">{{ p }}</option>
              }
            </select></label
          >
          <label
            >Chiều rộng logo (tỉ lệ khung)<input
              type="number"
              min="0.05"
              max="0.5"
              step="0.05"
              [ngModel]="logo.width"
              (ngModelChange)="patchLogo({ width: $event })"
          /></label>
          <label
            >Độ rõ logo<input
              type="number"
              min="0"
              max="1"
              step="0.1"
              [ngModel]="logo.opacity"
              (ngModelChange)="patchLogo({ opacity: $event })"
          /></label>
          <label
            >Bắt đầu logo (s)<input
              type="number"
              min="0"
              max="599"
              [ngModel]="logo.start"
              (ngModelChange)="patchLogo({ start: $event })"
          /></label>
          <label
            >Kết thúc logo (s)<input
              type="number"
              min="0.1"
              max="600"
              [ngModel]="logo.end"
              (ngModelChange)="patchLogo({ end: $event })"
          /></label>
        </div>
      }
      <label
        >Chuyển cảnh<select
          aria-label="Chuyển cảnh"
          [disabled]="mode() !== 'concat'"
          [ngModel]="transitions().style"
          (ngModelChange)="transitions.set({ ...transitions(), style: $event })"
        >
          @for (s of styles; track s) {
            <option [value]="s">{{ s }}</option>
          }
        </select></label
      >
      <label
        >Thời gian chuyển cảnh (s)<input
          aria-label="Thời gian chuyển cảnh"
          type="number"
          min="0.05"
          max="1.5"
          step="0.05"
          [disabled]="mode() !== 'concat'"
          [ngModel]="transitions().duration"
          (ngModelChange)="transitions.set({ ...transitions(), duration: $event })"
      /></label>
      <p>
        Chuyển cảnh dùng 2–10 clip, mỗi clip dài hơn hiệu ứng. Giữ nguyên tổng thời lượng bằng cách
        nối đuôi khung hình cuối; phụ đề và lời đọc giữ mốc cũ. Chế độ loop dùng tùy chọn loop
        riêng.
      </p>
    </fieldset>
  </details>`,
  styles: [
    `
      :host {
        min-width: 0;
        overflow-wrap: anywhere;
        display: block;
        margin: 1rem 0;
      }
      fieldset {
        min-width: 0;
        border: 0;
        padding: 0;
        display: flex;
        gap: 0.7rem;
        flex-wrap: wrap;
      }
      label {
        max-width: 100%;
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 0.3rem;
      }
      textarea {
        min-width: 0;
        width: 240px;
      }
      .layer {
        border: 1px solid #334155;
        padding: 0.8rem;
        display: flex;
        gap: 0.7rem;
        flex-wrap: wrap;
        width: 100%;
      }
      p {
        color: #94a3b8;
      }
      input,
      select,
      textarea {
        max-width: 100%;
      }
      @media (max-width: 600px) {
        label {
          width: 100%;
          margin-left: 0;
          margin-right: 0;
        }
        input:not([type='checkbox']),
        select,
        textarea {
          width: 100%;
        }
      }
      summary {
        cursor: pointer;
        font-weight: 600;
      }
    `,
  ],
})
export class GraphicsOptions {
  options = model<Branding>(defaultBranding());
  transitions = model<Transitions>(defaultTransitions());
  assets = input<{ id: string; kind: string; name: string }[]>([]);
  disabled = input(false);
  mode = input('concat');
  positions = [
    'top-left',
    'top-center',
    'top-right',
    'middle-left',
    'center',
    'middle-right',
    'bottom-left',
    'bottom-center',
    'bottom-right',
  ];
  styles = [
    'cut',
    'fade',
    'fadeblack',
    'fadewhite',
    'wipeleft',
    'slideright',
    'smoothleft',
    'circleopen',
  ];
  add(text: string, cta = false) {
    if (this.options().texts.length >= 5) return;
    this.options.update((b) => ({
      ...b,
      texts: [
        ...b.texts,
        {
          text,
          start: 0,
          end: 3,
          position: cta ? 'bottom-center' : 'top-center',
          fontSize: 48,
          color: '#ffffff',
          boxColor: '#000000',
          boxOpacity: 0.6,
        },
      ],
    }));
  }
  patch(i: number, patch: Partial<TextOverlay>) {
    this.options.update((b) => ({
      ...b,
      texts: b.texts.map((t, n) => (n === i ? { ...t, ...patch } : t)),
    }));
  }
  remove(i: number) {
    this.options.update((b) => ({ ...b, texts: b.texts.filter((_, n) => n !== i) }));
  }
  chooseLogo(assetId: string) {
    this.options.update((b) => ({
      ...b,
      logo: assetId
        ? { assetId, start: 0, end: 600, position: 'top-right', width: 0.15, opacity: 0.9 }
        : null,
    }));
  }
  patchLogo(patch: Partial<NonNullable<Branding['logo']>>) {
    this.options.update((b) => ({ ...b, logo: b.logo ? { ...b.logo, ...patch } : null }));
  }
}
