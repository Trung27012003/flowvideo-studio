import { Component, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface Effects {
  brightness: number;
  contrast: number;
  saturation: number;
  flipHorizontal: boolean;
  flipVertical: boolean;
  rotation: number;
  vignette: boolean;
  sharpen: number;
  background: string;
  fps: number;
  quality: string;
  mono: boolean;
  musicStart: number;
  voiceDelay: number;
  voiceSpeed: number;
  normalizeVoice: boolean;
}
export function defaultEffects(): Effects {
  return {
    brightness: 0,
    contrast: 1,
    saturation: 1,
    flipHorizontal: false,
    flipVertical: false,
    rotation: 0,
    vignette: false,
    sharpen: 0,
    background: '#000000',
    fps: 30,
    quality: 'standard',
    mono: false,
    musicStart: 0,
    voiceDelay: 0,
    voiceSpeed: 1,
    normalizeVoice: false,
  };
}
@Component({
  selector: 'app-render-options',
  imports: [FormsModule],
  template: `<details class="effects">
    <summary>Hiệu chỉnh hình & âm thanh · {{ context() }}</summary>
    <div class="grid">
      @for (field of numbers; track field.key) {
        <label
          >{{ field.label }} · {{ context()
          }}<input
            type="number"
            [min]="field.min"
            [max]="field.max"
            [step]="field.step"
            [ngModel]="value(field.key)"
            (ngModelChange)="change(field.key, $event)"
            [disabled]="disabled()"
        /></label>
      }
      <label
        >Xoay hình · {{ context()
        }}<select
          [attr.aria-label]="'Xoay hình · ' + context()"
          [ngModel]="options().rotation"
          (ngModelChange)="change('rotation', $event)"
          [disabled]="disabled()"
        >
          @for (n of [0, 90, 180, 270]; track n) {
            <option [ngValue]="n">{{ n }}°</option>
          }
        </select></label
      >
      <label
        >FPS · {{ context()
        }}<select
          [attr.aria-label]="'FPS · ' + context()"
          [ngModel]="options().fps"
          (ngModelChange)="change('fps', $event)"
          [disabled]="disabled()"
        >
          @for (n of [24, 30, 60]; track n) {
            <option [ngValue]="n">{{ n }} fps</option>
          }
        </select></label
      >
      <label
        >Chất lượng · {{ context()
        }}<select
          [attr.aria-label]="'Chất lượng · ' + context()"
          [ngModel]="options().quality"
          (ngModelChange)="change('quality', $event)"
          [disabled]="disabled()"
        >
          <option value="compact">Gọn · ưu tiên dung lượng</option>
          <option value="standard">Chuẩn · cân bằng</option>
          <option value="high">Cao · ưu tiên hình ảnh</option>
        </select></label
      >
      <label
        >Màu viền · {{ context()
        }}<input
          type="color"
          [ngModel]="options().background"
          (ngModelChange)="change('background', $event)"
          [disabled]="disabled()"
      /></label>
      @for (field of checks; track field.key) {
        <label class="check"
          ><input
            type="checkbox"
            [ngModel]="value(field.key)"
            (ngModelChange)="change(field.key, $event)"
            [disabled]="disabled()"
          />{{ field.label }} · {{ context() }}</label
        >
      }
    </div>
    <button type="button" (click)="reset()" [disabled]="disabled()">
      Khôi phục hiệu chỉnh mặc định
    </button>
    <p>
      Áp dụng khi xuất; preview tư liệu vẫn là nguồn gốc. Màu viền dùng với contain. Tốc độ/trễ
      giọng không tự căn lại SRT; nhạc bắt đầu phải nằm trong nguồn. Chuẩn hóa giọng tự động; hãy
      nghe lại sau khi xuất.
    </p>
  </details>`,
  styles: [
    `
      .effects {
        border: 1px solid #334155;
        border-radius: 10px;
        padding: 14px;
        margin: 18px 0;
      }
      summary {
        cursor: pointer;
        font-size: 12px;
        font-weight: 600;
      }
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr));
        gap: 14px;
        margin: 18px 0;
      }
      label {
        display: flex;
        flex-direction: column;
        gap: 7px;
        font-size: 11px;
        color: #b3c3d8;
        min-width: 0;
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
      p {
        font-size: 11px;
        line-height: 1.8;
        color: #91a3ba;
        margin: 12px 0 0;
      }
      button {
        font-size: 11px;
      }
    `,
  ],
})
export class RenderOptions {
  options = model<Effects>(defaultEffects());
  context = input('Studio');
  disabled = input(false);
  numbers = [
    { key: 'brightness', label: 'Độ sáng', min: -0.5, max: 0.5, step: 0.05 },
    { key: 'contrast', label: 'Tương phản', min: 0.5, max: 2, step: 0.1 },
    { key: 'saturation', label: 'Độ bão hòa', min: 0, max: 2, step: 0.1 },
    { key: 'sharpen', label: 'Độ sắc nét', min: 0, max: 1.5, step: 0.1 },
    { key: 'musicStart', label: 'Điểm bắt đầu nhạc (s)', min: 0, max: 600, step: 0.1 },
    { key: 'voiceDelay', label: 'Trễ giọng (s)', min: 0, max: 60, step: 0.1 },
    { key: 'voiceSpeed', label: 'Tốc độ giọng', min: 0.75, max: 1.5, step: 0.05 },
  ];
  checks = [
    { key: 'flipHorizontal', label: 'Lật ngang' },
    { key: 'flipVertical', label: 'Lật dọc' },
    { key: 'vignette', label: 'Tối góc vignette' },
    { key: 'mono', label: 'Xuất âm thanh mono' },
    { key: 'normalizeVoice', label: 'Chuẩn hóa độ lớn giọng' },
  ];
  value(key: string): any {
    return this.options()[key as keyof Effects];
  }
  change(key: string, value: unknown) {
    this.options.update((s) => ({ ...s, [key]: value }));
  }
  reset() {
    this.options.set(defaultEffects());
  }
}
