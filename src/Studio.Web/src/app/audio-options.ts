import { Component, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface AudioTuning {
  voiceHighpass: number;
  voiceLowpass: number;
  denoiseVoice: boolean;
  compressVoice: boolean;
  voiceBass: number;
  voiceTreble: number;
  voicePan: number;
  voiceStart: number;
  voiceFadeIn: number;
  voiceFadeOut: number;
  musicSpeed: number;
  musicHighpass: number;
  musicLowpass: number;
  musicPan: number;
  musicLoop: boolean;
  musicFadeIn: number;
  musicFadeOut: number;
}
export function defaultAudioTuning(): AudioTuning {
  return {
    voiceHighpass: 0,
    voiceLowpass: 0,
    denoiseVoice: false,
    compressVoice: false,
    voiceBass: 0,
    voiceTreble: 0,
    voicePan: 0,
    voiceStart: 0,
    voiceFadeIn: 0,
    voiceFadeOut: 0,
    musicSpeed: 1,
    musicHighpass: 0,
    musicLowpass: 0,
    musicPan: 0,
    musicLoop: true,
    musicFadeIn: 0,
    musicFadeOut: 0,
  };
}
@Component({
  selector: 'app-audio-options',
  imports: [FormsModule],
  template: `<details>
    <summary>Xử lý âm thanh · {{ context() }}</summary>
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
    <button (click)="tuning.set(defaults())" [disabled]="disabled()">
      Khôi phục xử lý âm thanh
    </button>
    <p>
      Cắt giọng tính theo nguồn trước đổi tốc độ. Cân kênh giảm một bên, không chuyển âm giữa hai
      kênh. Tắt lặp nhạc sẽ giữ hình và thêm im lặng khi nhạc hết. Fade giọng áp trước trễ; fade
      nhạc theo phần nhạc nghe được. Luôn nghe lại sau khi xuất.
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
        font-weight: 600;
        font-size: 12px;
      }
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr));
        gap: 12px;
        margin: 16px 0;
      }
      label {
        display: flex;
        flex-direction: column;
        font-size: 11px;
        gap: 7px;
        min-width: 0;
      }
      input {
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
      button {
        font-size: 11px;
      }
    `,
  ],
})
export class AudioOptions {
  tuning = model(defaultAudioTuning());
  context = input('Studio');
  disabled = input(false);
  defaults = defaultAudioTuning;
  numbers = [
    { key: 'voiceHighpass', label: 'Lọc trầm giọng Hz (0 tắt)', min: 0, max: 500, step: 20 },
    { key: 'voiceLowpass', label: 'Lọc cao giọng Hz (0 tắt)', min: 0, max: 18000, step: 1000 },
    { key: 'voiceBass', label: 'Bass giọng dB', min: -12, max: 12, step: 1 },
    { key: 'voiceTreble', label: 'Treble giọng dB', min: -12, max: 12, step: 1 },
    { key: 'voicePan', label: 'Cân kênh giọng (-1 trái / 1 phải)', min: -1, max: 1, step: 0.1 },
    { key: 'voiceStart', label: 'Cắt đầu giọng (s)', min: 0, max: 600, step: 0.1 },
    { key: 'voiceFadeIn', label: 'Fade vào giọng (s)', min: 0, max: 5, step: 0.1 },
    { key: 'voiceFadeOut', label: 'Fade ra giọng (s)', min: 0, max: 5, step: 0.1 },
    { key: 'musicSpeed', label: 'Tốc độ nhạc', min: 0.5, max: 2, step: 0.1 },
    { key: 'musicHighpass', label: 'Lọc trầm nhạc Hz (0 tắt)', min: 0, max: 500, step: 20 },
    { key: 'musicLowpass', label: 'Lọc cao nhạc Hz (0 tắt)', min: 0, max: 18000, step: 1000 },
    { key: 'musicPan', label: 'Cân kênh nhạc (-1 trái / 1 phải)', min: -1, max: 1, step: 0.1 },
    { key: 'musicFadeIn', label: 'Fade vào nhạc (s)', min: 0, max: 5, step: 0.1 },
    { key: 'musicFadeOut', label: 'Fade ra nhạc (s)', min: 0, max: 5, step: 0.1 },
  ];
  checks = [
    { key: 'denoiseVoice', label: 'Giảm nhiễu giọng' },
    { key: 'compressVoice', label: 'Nén động giọng' },
    { key: 'musicLoop', label: 'Lặp nhạc nền' },
  ];
  value(key: string): any {
    return this.tuning()[key as keyof AudioTuning];
  }
  change(key: string, value: unknown) {
    this.tuning.update((s) => ({ ...s, [key]: value }));
  }
}
