import { Component, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface SceneOptions {
  enabled: boolean;
  fit: string;
  brightness: number | null;
  rotation: number | null;
  speed: number;
  zoom: number;
  focusX: number;
  focusY: number;
  caption: boolean;
  fade: number;
  note: string;
}
export function defaultSceneOptions(): SceneOptions {
  return {
    enabled: true,
    fit: 'inherit',
    brightness: null,
    rotation: null,
    speed: 1,
    zoom: 1,
    focusX: 0.5,
    focusY: 0.5,
    caption: true,
    fade: 0,
    note: '',
  };
}
@Component({
  selector: 'app-scene-options',
  imports: [FormsModule],
  template: ` <details>
    <summary>Tùy chỉnh riêng cảnh</summary>
    <div class="grid">
      <label class="check"
        ><input
          type="checkbox"
          [ngModel]="options().enabled"
          (ngModelChange)="change('enabled', $event)"
          [disabled]="disabled()"
        />Dùng cảnh khi dựng</label
      >
      <label
        >Vừa khung riêng<select
          aria-label="Vừa khung riêng"
          [ngModel]="options().fit"
          (ngModelChange)="change('fit', $event)"
          [disabled]="disabled()"
        >
          <option value="inherit">Theo dự án</option>
          <option value="contain">Giữ trọn hình</option>
          <option value="cover">Lấp đầy khung</option>
        </select></label
      >
      <label class="check"
        ><input
          type="checkbox"
          [ngModel]="options().brightness !== null"
          (ngModelChange)="change('brightness', $event ? 0 : null)"
          [disabled]="disabled()"
        />Dùng độ sáng riêng</label
      >
      @if (options().brightness !== null) {
        <label
          >Độ sáng riêng<input
            type="number"
            min="-0.5"
            max="0.5"
            step="0.05"
            [ngModel]="options().brightness"
            (ngModelChange)="change('brightness', $event)"
            [disabled]="disabled()"
        /></label>
      }
      <label
        >Xoay riêng<select
          aria-label="Xoay riêng"
          [ngModel]="options().rotation"
          (ngModelChange)="change('rotation', $event)"
          [disabled]="disabled()"
        >
          <option [ngValue]="null">Theo dự án</option>
          @for (n of [0, 90, 180, 270]; track n) {
            <option [ngValue]="n">{{ n }}°</option>
          }
        </select></label
      >
      <label
        >Tốc độ hình cảnh<input
          type="number"
          min="0.5"
          max="2"
          step="0.1"
          [ngModel]="options().speed"
          (ngModelChange)="change('speed', $event)"
          [disabled]="disabled() || !video()"
      /></label>
      <label
        >Zoom riêng<input
          type="number"
          min="1"
          max="2"
          step="0.1"
          [ngModel]="options().zoom"
          (ngModelChange)="change('zoom', $event)"
          [disabled]="disabled()"
      /></label>
      <label
        >Tâm ngang cảnh (0–1)<input
          type="number"
          min="0"
          max="1"
          step="0.05"
          [ngModel]="options().focusX"
          (ngModelChange)="change('focusX', $event)"
          [disabled]="disabled()"
      /></label>
      <label
        >Tâm dọc cảnh (0–1)<input
          type="number"
          min="0"
          max="1"
          step="0.05"
          [ngModel]="options().focusY"
          (ngModelChange)="change('focusY', $event)"
          [disabled]="disabled()"
      /></label>
      <label class="check"
        ><input
          type="checkbox"
          [ngModel]="options().caption"
          (ngModelChange)="change('caption', $event)"
          [disabled]="disabled()"
        />Hiện phụ đề cảnh</label
      >
      <label
        >Fade riêng cảnh (s)<input
          type="number"
          min="0"
          max="1"
          step="0.1"
          [ngModel]="options().fade"
          (ngModelChange)="change('fade', $event)"
          [disabled]="disabled()"
      /></label>
    </div>
    <label
      >Ghi chú sản xuất cảnh<textarea
        maxlength="1000"
        [ngModel]="options().note"
        (ngModelChange)="change('note', $event)"
        [disabled]="disabled()"
        placeholder="Ghi chú không hiện trong video"
      ></textarea>
    </label>
    <p>
      Tốc độ chỉ đổi hình nguồn, giữ thời lượng cảnh đã đặt. Tâm chỉ tác dụng khi cover/zoom; 0 là
      trái/trên, 1 là phải/dưới. Cảnh tắt vẫn được lưu.
    </p>
  </details>`,
  styles: [
    `
      details {
        border-top: 1px solid #334155;
        padding: 12px 0;
        margin: 14px 0;
      }
      summary {
        cursor: pointer;
        font-size: 12px;
        font-weight: 600;
      }
      .grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 12px;
        margin: 14px 0;
      }
      label {
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 11px;
        min-width: 0;
        margin: 8px 0;
      }
      input,
      select,
      textarea {
        width: 100%;
      }
      .check {
        flex-direction: row;
        align-items: center;
      }
      .check input {
        width: auto;
      }
      textarea {
        min-height: 70px;
      }
      p {
        font-size: 11px;
        line-height: 1.8;
        color: #91a3ba;
      }
    `,
  ],
})
export class SceneOptionsEditor {
  options = model<SceneOptions>(defaultSceneOptions());
  video = input(false);
  disabled = input(false);
  change(key: string, value: unknown) {
    this.options.update((s) => ({ ...s, [key]: value }));
  }
}
