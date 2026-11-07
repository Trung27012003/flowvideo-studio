import { Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface DirectionBlueprint {
  shot: string;
  camera: string;
  lighting: string;
  location: string;
  characters: string;
  purpose: string;
}
export interface PlannedScene {
  editorId?: string;
  title: string;
  narration: string;
  prompt: string;
  duration: number;
  direction: DirectionBlueprint;
}
export interface StoryProposal {
  title: string;
  audience: string;
  message: string;
  hook: string;
  ending: string;
  scenes: PlannedScene[];
}
export interface StoryPlanOptions {
  source: string;
  sceneCount: number;
  duration: number;
  genre: string;
  structure: string;
  language: string;
  style: string;
  variant: number;
}
export interface StoryPlanView {
  id: string;
  status: string;
  message: string;
  documentVersion: number;
  revision: number;
  model: string;
  origin?: string;
  inputTokens: number | null;
  outputTokens: number | null;
  proposal: StoryProposal | null;
}
export interface StoryPlanEdit {
  id: string;
  revision: number;
  proposal: StoryProposal;
}
export function defaultDirection(): DirectionBlueprint {
  return {
    shot: 'medium',
    camera: 'static',
    lighting: '',
    location: '',
    characters: '',
    purpose: '',
  };
}
@Component({
  selector: 'app-story-architect',
  imports: [FormsModule],
  template: ` <section class="architect">
    <h2>Phân cảnh AI có cấu trúc</h2>
    <p>
      Lấy ý tưởng hoặc kịch bản ở ô bên dưới, lưu nguồn rồi tạo bản đề xuất. Xem/sửa và áp dụng rõ
      ràng trước khi tạo tư liệu. AI có thể tính phí; {{ used() }}/{{ limit() }} lượt đã nhận trong
      24 giờ. Token usage là dữ liệu sử dụng, chưa phải hóa đơn.
    </p>
    <fieldset [disabled]="disabled()">
      <div class="settings">
        <label
          >Nguồn phân cảnh<select aria-label="Nguồn phân cảnh" [(ngModel)]="options.source">
            <option value="idea">Ý tưởng</option>
            <option value="script">Kịch bản</option>
          </select></label
        >
        <label
          >Số cảnh AI<input
            type="number"
            min="1"
            max="10"
            step="1"
            [(ngModel)]="options.sceneCount"
        /></label>
        <label
          >Tổng giây phân cảnh<input
            type="number"
            min="1"
            max="600"
            step="1"
            [(ngModel)]="options.duration"
        /></label>
        <label
          >Thể loại phân cảnh<select aria-label="Thể loại phân cảnh" [(ngModel)]="options.genre">
            <option value="education">Giáo dục</option>
            <option value="story">Kể chuyện</option>
            <option value="ad">Quảng cáo</option>
            <option value="documentary">Tài liệu</option>
          </select></label
        >
        <label
          >Cấu trúc câu chuyện<select
            aria-label="Cấu trúc câu chuyện"
            [(ngModel)]="options.structure"
          >
            <option value="hook">Hook → Nội dung → Kết thúc</option>
            <option value="three-act">Ba hồi</option>
            <option value="aida">AIDA</option>
          </select></label
        >
        <label
          >Ngôn ngữ phân cảnh<select aria-label="Ngôn ngữ phân cảnh" [(ngModel)]="options.language">
            <option value="vi">Tiếng Việt</option>
            <option value="en">English</option>
          </select></label
        >
        <label
          >Phong cách phân cảnh<input
            maxlength="300"
            [(ngModel)]="options.style"
            placeholder="Ví dụ: chân thực, ánh sáng mềm"
        /></label>
        <label
          >Biến thể phân cảnh (0–100)<input
            type="number"
            min="0"
            max="100"
            [(ngModel)]="options.variant"
        /></label>
      </div>
      <div class="actions">
        <button class="primary" [disabled]="!enabled()" (click)="request()">
          Tạo bản phân cảnh</button
        ><button (click)="refresh.emit()">Cập nhật lịch sử phân cảnh</button>
        <label
          >Nhập phân cảnh JSON<input
            aria-label="Nhập phân cảnh JSON"
            type="file"
            accept=".json,application/json"
            (change)="importPlan.emit($event)"
        /></label>
      </div>
    </fieldset>
    @if (!enabled()) {
      <p>Cấu hình OPENAI_API_KEY ở backend để tạo mới; bản đề xuất đã có vẫn xem/sửa được.</p>
    }
    <div class="history">
      @for (row of rows(); track row.id) {
        <article>
          <b>{{ status(row.status) }}</b>
          <p>{{ row.proposal?.title || row.message }}</p>
          <small
            >{{ row.model }} · nguồn v{{ row.documentVersion }} · bản sửa {{ row.revision }} · token
            vào {{ row.inputTokens ?? 'chưa rõ' }} / ra {{ row.outputTokens ?? 'chưa rõ' }}</small
          >
          <div class="actions">
            @if (row.proposal) {
              <button [disabled]="disabled()" (click)="open.emit(row.id)">
                Xem/sửa bản phân cảnh
              </button>
            }
            @if (row.status === 'Queued') {
              <button [disabled]="disabled()" (click)="cancel.emit(row.id)">
                Hủy phân cảnh chưa gửi
              </button>
            }
            <a
              [href]="'/api/projects/' + projectId() + '/story-plans/' + row.id"
              [download]="'flowvideo-story-plan-' + row.id + '.json'"
              >Tải bản phân cảnh JSON</a
            >
          </div>
          @if (row.status !== 'Succeeded') {
            <p>{{ row.message }}</p>
          }
        </article>
      } @empty {
        <p>Chưa có bản phân cảnh.</p>
      }
    </div>
    @if (draft(); as p) {
      <fieldset class="review" [disabled]="disabled()">
        <h3>Duyệt và sửa bản đề xuất</h3>
        <p>
          {{ p.scenes.length }} cảnh · {{ total() }} giây. Áp dụng sẽ thay danh sách cảnh và script
          đã lưu; tư liệu cũ vẫn giữ trong thư viện. Cảnh mới chưa gắn media.
        </p>
        <div class="settings">
          @for (field of metaFields; track field.key) {
            <label
              >{{ field.label
              }}<textarea
                [attr.aria-label]="field.label"
                [maxlength]="field.key === 'title' ? 120 : 1000"
                [ngModel]="p[field.key]"
                (ngModelChange)="meta(field.key, $event)"
              ></textarea>
            </label>
          }
        </div>
        @for (scene of p.scenes; track scene.editorId; let i = $index) {
          <article class="scene-review">
            <header>
              <b>Cảnh {{ i + 1 }}</b>
              <div class="actions">
                <button
                  (click)="move(i, -1)"
                  [disabled]="i === 0"
                  aria-label="Đưa cảnh đề xuất lên"
                >
                  ↑</button
                ><button
                  (click)="move(i, 1)"
                  [disabled]="i === p.scenes.length - 1"
                  aria-label="Đưa cảnh đề xuất xuống"
                >
                  ↓</button
                ><button (click)="duplicate(i)" [disabled]="p.scenes.length >= 10">
                  Nhân bản cảnh đề xuất</button
                ><button (click)="remove(i)" [disabled]="p.scenes.length <= 1">
                  Xóa cảnh đề xuất
                </button>
              </div>
            </header>
            <div class="settings">
              <label
                >Tên cảnh đề xuất<input
                  aria-label="Tên cảnh đề xuất"
                  maxlength="120"
                  [ngModel]="scene.title"
                  (ngModelChange)="sceneField(i, 'title', $event)" /></label
              ><label
                >Giây cảnh đề xuất<input
                  aria-label="Giây cảnh đề xuất"
                  type="number"
                  min="0.1"
                  max="600"
                  step="0.1"
                  [ngModel]="scene.duration"
                  (ngModelChange)="sceneField(i, 'duration', $event)"
              /></label>
            </div>
            <label
              >Lời đọc đề xuất<textarea
                aria-label="Lời đọc đề xuất"
                maxlength="2000"
                [ngModel]="scene.narration"
                (ngModelChange)="sceneField(i, 'narration', $event)"
              ></textarea></label
            ><label
              >Prompt đề xuất<textarea
                aria-label="Prompt đề xuất"
                maxlength="4000"
                [ngModel]="scene.prompt"
                (ngModelChange)="sceneField(i, 'prompt', $event)"
              ></textarea>
            </label>
            <div class="settings">
              <label
                >Cỡ cảnh đề xuất<select
                  aria-label="Cỡ cảnh đề xuất"
                  [ngModel]="scene.direction.shot"
                  (ngModelChange)="direction(i, 'shot', $event)"
                >
                  @for (shot of shots; track shot.value) {
                    <option [value]="shot.value">{{ shot.label }}</option>
                  }
                </select></label
              ><label
                >Máy quay đề xuất<select
                  aria-label="Máy quay đề xuất"
                  [ngModel]="scene.direction.camera"
                  (ngModelChange)="direction(i, 'camera', $event)"
                >
                  @for (camera of cameras; track camera) {
                    <option [value]="camera">{{ camera }}</option>
                  }
                </select></label
              >
              @for (field of directionFields; track field.key) {
                <label
                  >{{ field.label
                  }}<input
                    [attr.aria-label]="field.label"
                    maxlength="300"
                    [ngModel]="scene.direction[field.key]"
                    (ngModelChange)="direction(i, field.key, $event)"
                /></label>
              }
            </div>
          </article>
        }
        <div class="actions">
          <button (click)="add()" [disabled]="p.scenes.length >= 10">Thêm cảnh đề xuất</button
          ><button (click)="submit(false)">Lưu bản sửa phân cảnh</button
          ><button class="primary" (click)="submit(true)">Lưu và áp dụng storyboard</button
          ><button (click)="close()">Đóng bản đề xuất</button>
        </div>
        <p>
          Góc máy/ánh sáng là hướng dẫn gửi vào prompt, không bảo đảm model thực hiện chính xác.
          Không tự tạo ảnh/video khi bấm áp dụng.
        </p>
        @if (original(); as o) {
          <details>
            <summary>Xem bản gốc</summary>
            <h4>{{ o.title }}</h4>
            @for (s of o.scenes; track $index) {
              <p>
                <b>{{ s.title }} · {{ s.duration }}s</b><br />{{ s.narration }}<br />{{ s.prompt }}
              </p>
            }
          </details>
        }
      </fieldset>
    }
  </section>`,
  styles: [
    `
      :host {
        display: block;
        margin-bottom: 24px;
      }
      .architect {
        padding: 22px;
        border: 1px solid #315267;
        border-radius: 16px;
        background: #0e1c2b;
      }
      h2,
      h3 {
        margin: 0 0 12px;
      }
      p,
      small {
        font-size: 12px;
        color: #a6b6c8;
        line-height: 1.8;
      }
      fieldset {
        border: 0;
        min-width: 0;
        padding: 0;
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
        margin: 12px 0;
      }
      input,
      select,
      textarea {
        width: 100%;
        min-width: 0;
      }
      textarea {
        min-height: 90px;
      }
      .actions,
      header {
        display: flex;
        gap: 10px;
        flex-wrap: wrap;
        align-items: center;
      }
      article {
        background: #152337;
        padding: 16px;
        border-radius: 12px;
        margin: 14px 0;
      }
      .history {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 300px), 1fr));
        gap: 12px;
      }
      .review {
        margin-top: 20px;
        border-top: 1px solid #334155;
        padding-top: 20px;
      }
      .scene-review {
        border: 1px solid #334155;
      }
      header {
        justify-content: space-between;
      }
      a {
        color: #36dcc0;
        font-size: 12px;
      }
      @media (max-width: 750px) {
        .settings {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class StoryArchitect {
  rows = input<StoryPlanView[]>([]);
  projectId = input('');
  used = input(0);
  limit = input(10);
  disabled = input(false);
  enabled = input(false);
  generate = output<StoryPlanOptions>();
  refresh = output<void>();
  importPlan = output<Event>();
  open = output<string>();
  cancel = output<string>();
  save = output<StoryPlanEdit>();
  apply = output<StoryPlanEdit>();
  options: StoryPlanOptions = {
    source: 'idea',
    sceneCount: 5,
    duration: 30,
    genre: 'education',
    structure: 'hook',
    language: 'vi',
    style: '',
    variant: 0,
  };
  selected = signal<StoryPlanView | null>(null);
  draft = signal<StoryProposal | null>(null);
  original = signal<StoryProposal | null>(null);
  metaFields: { key: 'title' | 'audience' | 'message' | 'hook' | 'ending'; label: string }[] = [
    { key: 'title', label: 'Tiêu đề đề xuất' },
    { key: 'audience', label: 'Khán giả đề xuất' },
    { key: 'message', label: 'Thông điệp đề xuất' },
    { key: 'hook', label: 'Hook đề xuất' },
    { key: 'ending', label: 'Kết thúc đề xuất' },
  ];
  shots = [
    { value: 'establishing', label: 'Thiết lập bối cảnh' },
    { value: 'wide', label: 'Toàn cảnh' },
    { value: 'medium', label: 'Trung cảnh' },
    { value: 'close', label: 'Cận cảnh' },
    { value: 'extreme-close', label: 'Đặc tả' },
  ];
  cameras = ['static', 'pan', 'tilt', 'dolly', 'zoom', 'handheld'];
  directionFields: { key: 'lighting' | 'location' | 'characters' | 'purpose'; label: string }[] = [
    { key: 'lighting', label: 'Ánh sáng đề xuất' },
    { key: 'location', label: 'Bối cảnh đề xuất' },
    { key: 'characters', label: 'Nhân vật đề xuất' },
    { key: 'purpose', label: 'Mục đích cảnh đề xuất' },
  ];
  show(row: StoryPlanView, original: StoryProposal) {
    this.selected.set(structuredClone(row));
    this.draft.set(this.editable(row.proposal));
    this.original.set(structuredClone(original));
  }
  accepted(row: StoryPlanView) {
    this.selected.set(structuredClone(row));
    this.draft.set(this.editable(row.proposal));
  }
  private editable(p: StoryProposal | null): StoryProposal | null {
    return p
      ? {
          ...structuredClone(p),
          scenes: p.scenes.map((s) => ({ ...structuredClone(s), editorId: crypto.randomUUID() })),
        }
      : null;
  }
  close() {
    this.selected.set(null);
    this.draft.set(null);
    this.original.set(null);
  }
  total() {
    return (
      Math.round(
        (this.draft()?.scenes.reduce((sum, s) => sum + (Number(s.duration) || 0), 0) || 0) * 10,
      ) / 10
    );
  }
  meta(key: string, value: string) {
    this.draft.update((p) => (p ? { ...p, [key]: value } : p));
  }
  sceneField(index: number, key: string, value: unknown) {
    this.draft.update((p) =>
      p ? { ...p, scenes: p.scenes.map((s, i) => (i === index ? { ...s, [key]: value } : s)) } : p,
    );
  }
  direction(index: number, key: string, value: string) {
    const d = this.draft()?.scenes[index].direction;
    if (d) this.sceneField(index, 'direction', { ...d, [key]: value });
  }
  add() {
    this.draft.update((p) =>
      p && p.scenes.length < 10
        ? {
            ...p,
            scenes: [
              ...p.scenes,
              {
                editorId: crypto.randomUUID(),
                title: 'Cảnh mới',
                narration: '',
                prompt: '',
                duration: 3,
                direction: defaultDirection(),
              },
            ],
          }
        : p,
    );
  }
  remove(index: number) {
    this.draft.update((p) =>
      p && p.scenes.length > 1 ? { ...p, scenes: p.scenes.filter((_, i) => i !== index) } : p,
    );
  }
  duplicate(index: number) {
    this.draft.update((p) =>
      p && p.scenes.length < 10
        ? {
            ...p,
            scenes: p.scenes.flatMap((s, i) =>
              i === index ? [s, { ...structuredClone(s), editorId: crypto.randomUUID() }] : [s],
            ),
          }
        : p,
    );
  }
  move(index: number, delta: number) {
    this.draft.update((p) => {
      if (!p || index + delta < 0 || index + delta >= p.scenes.length) return p;
      const scenes = [...p.scenes];
      [scenes[index], scenes[index + delta]] = [scenes[index + delta], scenes[index]];
      return { ...p, scenes };
    });
  }
  submit(apply: boolean) {
    const row = this.selected(),
      proposal = this.draft();
    if (row && proposal)
      (apply ? this.apply : this.save).emit({
        id: row.id,
        revision: row.revision,
        proposal: {
          ...structuredClone(proposal),
          scenes: proposal.scenes.map(({ editorId, ...scene }) => structuredClone(scene)),
        },
      });
  }
  status(s: string) {
    return (
      (
        {
          Queued: 'Đang chờ',
          Running: 'Đang phân cảnh',
          Succeeded: 'Sẵn sàng duyệt',
          Failed: 'Lỗi',
          Unknown: 'Chưa rõ kết quả',
          Canceled: 'Đã hủy',
        } as Record<string, string>
      )[s] || s
    );
  }
  request() {
    this.generate.emit({ ...this.options });
  }
}
