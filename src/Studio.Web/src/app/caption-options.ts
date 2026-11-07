import { Component, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface CaptionStyle {
  font: string;
  size: number;
  color: string;
  outlineColor: string;
  outline: number;
  shadow: number;
  bold: boolean;
  alignment: number;
  margin: number;
  box: boolean;
  boxColor: string;
  boxOpacity: number;
}
export function defaultCaptionStyle(): CaptionStyle {
  return {
    font: 'sans',
    size: 18,
    color: '#ffffff',
    outlineColor: '#000000',
    outline: 1,
    shadow: 0,
    bold: false,
    alignment: 2,
    margin: 36,
    box: false,
    boxColor: '#000000',
    boxOpacity: 0.5,
  };
}
@Component({
  selector: 'app-caption-options',
  imports: [FormsModule],
  template: `<details>
    <summary>Kiểu phụ đề · {{ context() }}</summary>
    <div class="grid">
      <label
        >Phông phụ đề · {{ context()
        }}<select
          [attr.aria-label]="'Phông phụ đề · ' + context()"
          [ngModel]="style().font"
          (ngModelChange)="change('font', $event)"
          [disabled]="disabled()"
        >
          <option value="sans">Sans</option>
          <option value="serif">Serif</option>
          <option value="mono">Mono</option>
        </select></label
      >
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
      @for (field of colors; track field.key) {
        <label
          >{{ field.label }} · {{ context()
          }}<input
            type="color"
            [ngModel]="value(field.key)"
            (ngModelChange)="change(field.key, $event)"
            [disabled]="disabled()"
        /></label>
      }
      <label
        >Vị trí phụ đề · {{ context()
        }}<select
          [attr.aria-label]="'Vị trí phụ đề · ' + context()"
          [ngModel]="style().alignment"
          (ngModelChange)="change('alignment', $event)"
          [disabled]="disabled()"
        >
          @for (position of positions; track position.id) {
            <option [ngValue]="position.id">{{ position.name }}</option>
          }
        </select></label
      >
      <label class="check"
        ><input
          type="checkbox"
          [ngModel]="style().bold"
          (ngModelChange)="change('bold', $event)"
          [disabled]="disabled()"
        />Chữ đậm phụ đề · {{ context() }}</label
      >
      <label class="check"
        ><input
          type="checkbox"
          [ngModel]="style().box"
          (ngModelChange)="change('box', $event)"
          [disabled]="disabled()"
        />Nền hộp phụ đề · {{ context() }}</label
      >
    </div>
    <p>
      Áp dụng khi xuất, không sửa nội dung SRT. Khi bật hộp, màu nền thay màu viền; độ viền cũng là
      khoảng đệm hộp. Cỡ chữ và lề hiển thị tùy kích thước video.
    </p>
  </details>`,
  styles: [
    `
      details {
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
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 170px), 1fr));
        gap: 12px;
        margin: 16px 0;
      }
      label {
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 11px;
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
      }
    `,
  ],
})
export class CaptionOptions {
  style = model<CaptionStyle>(defaultCaptionStyle());
  context = input('Studio');
  disabled = input(false);
  numbers = [
    { key: 'size', label: 'Cỡ chữ phụ đề', min: 12, max: 48, step: 1 },
    { key: 'outline', label: 'Độ viền phụ đề', min: 0, max: 4, step: 0.5 },
    { key: 'shadow', label: 'Bóng chữ phụ đề', min: 0, max: 4, step: 0.5 },
    { key: 'margin', label: 'Lề dọc phụ đề', min: 0, max: 200, step: 1 },
    { key: 'boxOpacity', label: 'Độ đậm nền hộp', min: 0, max: 1, step: 0.1 },
  ];
  colors = [
    { key: 'color', label: 'Màu chữ phụ đề' },
    { key: 'outlineColor', label: 'Màu viền phụ đề' },
    { key: 'boxColor', label: 'Màu nền hộp' },
  ];
  positions = [
    { id: 7, name: 'Trên trái' },
    { id: 8, name: 'Trên giữa' },
    { id: 9, name: 'Trên phải' },
    { id: 4, name: 'Giữa trái' },
    { id: 5, name: 'Chính giữa' },
    { id: 6, name: 'Giữa phải' },
    { id: 1, name: 'Dưới trái' },
    { id: 2, name: 'Dưới giữa' },
    { id: 3, name: 'Dưới phải' },
  ];
  value(key: string): any {
    return this.style()[key as keyof CaptionStyle];
  }
  change(key: string, value: unknown) {
    this.style.update((s) => ({ ...s, [key]: value }));
  }
}
