import { Component, input, output, signal, effect } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface ProfileContent {
  name: string;
  description: string;
  appearance: string;
  outfit: string;
  voice: string;
  weather: string;
  timeOfDay: string;
  lighting: string;
  props: string;
  notes: string;
  referenceId: string | null;
}
export interface ProfileSnapshot {
  id: string;
  version: number;
  kind: string;
  content: ProfileContent;
}
export interface Continuity {
  characters: ProfileSnapshot[];
  location: ProfileSnapshot | null;
  characterState: string;
}
interface Profile extends ProfileSnapshot {
  projectId: string;
}
const blank = (): ProfileContent => ({
  name: '',
  description: '',
  appearance: '',
  outfit: '',
  voice: '',
  weather: '',
  timeOfDay: '',
  lighting: '',
  props: '',
  notes: '',
  referenceId: null,
});
@Component({
  selector: 'app-creative-workspace',
  imports: [FormsModule],
  template: ` <details class="panel">
    <summary>Hồ sơ nhân vật/bối cảnh · Backup · Ngân sách AI</summary>
    @if (error()) {
      <p role="alert">{{ error() }}</p>
    }
    @if (notice()) {
      <p role="status">{{ notice() }}</p>
    }
    <fieldset [disabled]="busy()">
      <button (click)="downloadBackup()" [disabled]="!projectId()">
        Backup dự án đã lưu (ZIP)
      </button>
      <label
        >Khôi phục backup<input
          type="file"
          accept=".zip"
          aria-label="Khôi phục backup"
          (change)="restore($event)"
      /></label>
      <p>
        ZIP gồm media, storyboard đã lưu, hồ sơ, metadata, mốc/preset (tối đa 100 media / 240 MiB).
        Khôi phục tạo dự án mới; không chạy AI. Không kèm lịch sử tác vụ, video xuất, ngân sách hoặc
        API key.
      </p>
      @if (projectId()) {
        <details>
          <summary>Thư viện hồ sơ ({{ profiles().length }}/50)</summary>
          <button (click)="reload()">Tải lại hồ sơ/ngân sách</button>
          <div class="rows">
            @for (p of profiles(); track p.id) {
              <div>
                <b>{{ p.content.name }}</b> · {{ p.kind }} · v{{ p.version }}
                <button (click)="edit(p)">Sửa hồ sơ</button
                ><button (click)="deleteProfile(p)">Xóa hồ sơ</button>
              </div>
            }
          </div>
          <label
            >Loại hồ sơ<select aria-label="Loại hồ sơ" [(ngModel)]="kind" [disabled]="!!editing">
              <option value="character">Nhân vật</option>
              <option value="location">Bối cảnh</option>
            </select></label
          >
          <label
            >Tên hồ sơ<input aria-label="Tên hồ sơ" maxlength="120" [(ngModel)]="content.name"
          /></label>
          <label
            >Mô tả hồ sơ<textarea
              aria-label="Mô tả hồ sơ"
              maxlength="1000"
              [(ngModel)]="content.description"
            ></textarea>
          </label>
          @for (field of fields(); track field.key) {
            <label
              >{{ field.label
              }}<input
                maxlength="300"
                [ngModel]="content[field.key]"
                (ngModelChange)="setField(field.key, $event)"
            /></label>
          }
          <label
            >Ảnh tham chiếu hồ sơ<select [(ngModel)]="content.referenceId">
              <option [ngValue]="null">Không có</option>
              @for (a of assets(); track a.id) {
                @if (a.kind === 'image') {
                  <option [value]="a.id">{{ a.name }}</option>
                }
              }
            </select></label
          >
          <button (click)="saveProfile()" [disabled]="!content.name.trim()">
            {{ editing ? 'Cập nhật hồ sơ' : 'Tạo hồ sơ' }}</button
          ><button (click)="clear()">Hồ sơ mới</button>
          <button (click)="exportProfiles()">Xuất gói hồ sơ JSON</button
          ><label
            >Nhập gói hồ sơ<input
              type="file"
              accept=".json"
              aria-label="Nhập gói hồ sơ"
              (change)="importProfiles($event)"
          /></label>
          <p>
            Gói JSON không mang ảnh. Hồ sơ được chụp vào từng cảnh khi gắn; sửa/xóa thư viện không
            thay bản chụp. Mô tả được thêm vào prompt, không bảo đảm AI giữ khuôn mặt hoặc giọng;
            ảnh hồ sơ không tự gửi sang engine.
          </p>
          <label
            >Cảnh nhận hồ sơ<select aria-label="Cảnh nhận hồ sơ" [(ngModel)]="sceneId">
              <option value="">Chọn cảnh</option>
              @for (s of scenes(); track s.id) {
                <option [value]="s.id">{{ s.title }}</option>
              }
            </select></label
          >
          <label
            >Nhân vật 1<select aria-label="Nhân vật 1" [(ngModel)]="character1">
              <option value="">Không</option>
              @for (p of profiles(); track p.id) {
                @if (p.kind === 'character') {
                  <option [value]="p.id">{{ p.content.name }} v{{ p.version }}</option>
                }
              }
            </select></label
          >
          <label
            >Nhân vật 2<select aria-label="Nhân vật 2" [(ngModel)]="character2">
              <option value="">Không</option>
              @for (p of profiles(); track p.id) {
                @if (p.kind === 'character') {
                  <option [value]="p.id">{{ p.content.name }} v{{ p.version }}</option>
                }
              }
            </select></label
          >
          <label
            >Bối cảnh<select aria-label="Bối cảnh hồ sơ" [(ngModel)]="location">
              <option value="">Không</option>
              @for (p of profiles(); track p.id) {
                @if (p.kind === 'location') {
                  <option [value]="p.id">{{ p.content.name }} v{{ p.version }}</option>
                }
              }
            </select></label
          >
          <label>Trạng thái nhân vật<input maxlength="300" [(ngModel)]="characterState" /></label
          ><button (click)="attach()" [disabled]="!sceneId">Gắn bản chụp vào cảnh</button
          ><button (click)="detached()" [disabled]="!sceneId">Bỏ hồ sơ khỏi cảnh</button>
        </details>
        @if (budget(); as b) {
          <details>
            <summary>Ngân sách AI theo đơn giá tự nhập</summary>
            <p>
              Đã giữ dự toán/đối soát (lần tải gần nhất): {{ b.summary.reservedUsd }} USD /
              {{ b.summary.limitUsd }} USD. Khoản chưa biết giá: {{ b.summary.unknownEntries }}. Đây
              không phải hóa đơn nhà cung cấp; giới hạn tính trên toàn vòng đời dự án. Đơn giá áp
              dụng chung cho từng loại gọi AI, hãy chọn giá đủ cho model bạn dùng.
            </p>
            <label
              ><input type="checkbox" [(ngModel)]="config.enabled" />Chặn AI vượt ngân sách dự
              toán</label
            >
            <label
              >Giới hạn USD<input
                aria-label="Giới hạn USD"
                type="number"
                min="0.000001"
                max="10000"
                step="0.01"
                [(ngModel)]="config.limitUsd"
            /></label>
            @for (rate of rates; track rate.key) {
              <label
                >{{ rate.label
                }}<input
                  [attr.aria-label]="rate.label"
                  type="number"
                  min="0"
                  max="100"
                  step="0.001"
                  [ngModel]="config[rate.key]"
                  (ngModelChange)="config[rate.key] = $event"
              /></label>
            }
            <button (click)="saveBudget()">Lưu ngân sách</button
            ><button (click)="reprice()">Định giá khoản chưa rõ</button
            ><button (click)="exportBudget()">Xuất sổ dự toán JSON</button>
            <p>
              Hiển thị tối đa 200 khoản gần nhất, tổng và file xuất tính toàn bộ sổ. Để trống nghĩa
              là chưa biết giá. Số 0 nghĩa là bạn xác nhận miễn phí. Cache không giữ thêm dự toán.
              Tác vụ lỗi/hủy vẫn giữ dự toán; đối soát thủ công nếu được hoàn phí.
            </p>
            <label
              >Khoản đối soát<select aria-label="Khoản đối soát" [(ngModel)]="entryId">
                <option value="">Chọn khoản</option>
                @for (e of b.entries; track e.id) {
                  <option [value]="e.id">
                    {{ e.kind }} · {{ e.units }} đơn vị · {{ e.createdAt }}
                  </option>
                }
              </select></label
            >
            <label
              >Chi phí thực tế USD<input
                type="number"
                min="0"
                max="10000"
                step="0.001"
                [(ngModel)]="actualUsd" /></label
            ><label>Ghi chú đối soát<input maxlength="300" [(ngModel)]="reconcileNote" /></label
            ><button (click)="reconcile()" [disabled]="!entryId || !reconcileNote.trim()">
              Ghi nhận đối soát
            </button>
            <label
              >Dự toán loại<select [(ngModel)]="quoteKind">
                @for (r of rates; track r.kind) {
                  <option [value]="r.kind">{{ r.label }}</option>
                }
              </select></label
            ><label
              >Số đơn vị<input type="number" min="0" max="100000" [(ngModel)]="quoteUnits" /></label
            ><button (click)="quote()">Tính thử chi phí</button>
            @if (quotation()) {
              <p role="status">{{ quotation() }}</p>
            }
            <div class="rows">
              @for (e of b.entries; track e.id) {
                <div>
                  {{ e.kind }} · {{ e.units }} đơn vị ·
                  {{ e.actualMicros !== null ? 'Đối soát' : 'Dự toán' }}:
                  {{
                    (e.actualMicros ?? e.estimatedMicros) === null
                      ? 'Chưa biết'
                      : (e.actualMicros ?? e.estimatedMicros) / 1000000 + ' USD'
                  }}
                  · {{ e.note }}
                </div>
              }
            </div>
          </details>
        }
      }
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
      .panel {
        padding: 1rem;
        border: 1px solid #334155;
        border-radius: 12px;
      }
      fieldset {
        align-items: flex-start;
        min-width: 0;
        border: 0;
        padding: 1rem 0;
        display: flex;
        flex-wrap: wrap;
        gap: 0.8rem;
      }
      details {
        width: 100%;
        padding: 0.6rem 0;
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
      label {
        max-width: 100%;
        min-width: 0;
        display: inline-flex;
        flex-direction: column;
        gap: 0.3rem;
        margin: 0.5rem;
        vertical-align: top;
      }
      p {
        color: #94a3b8;
      }
      .rows {
        max-height: 240px;
        overflow: auto;
      }
      .rows > div {
        padding: 0.5rem;
        border-bottom: 1px solid #334155;
      }
    `,
  ],
})
export class CreativeWorkspace {
  projectId = input('');
  assets = input<{ id: string; kind: string; name: string }[]>([]);
  scenes = input<{ id: string; title: string }[]>([]);
  restored = output<string>();
  continuity = output<{ sceneId: string; value: Continuity | null }>();
  profiles = signal<Profile[]>([]);
  budget = signal<any>(null);
  config: any = {};
  error = signal('');
  notice = signal('');
  busy = signal(false);
  quotation = signal('');
  kind = 'character';
  content = blank();
  editing: Profile | null = null;
  sceneId = '';
  character1 = '';
  character2 = '';
  location = '';
  characterState = '';
  entryId = '';
  actualUsd = 0;
  reconcileNote = '';
  quoteKind = 'image';
  quoteUnits = 1;
  rates = [
    { key: 'imageUsdPerCall', kind: 'image', label: 'USD / ảnh' },
    { key: 'videoUsdPerSecond', kind: 'video', label: 'USD / giây video AI' },
    { key: 'speechUsdPer1000Chars', kind: 'speech', label: 'USD / 1.000 ký tự TTS' },
    { key: 'storyUsdPerCall', kind: 'story', label: 'USD / lượt kịch bản' },
    { key: 'transcriptionUsdPerMinute', kind: 'transcription', label: 'USD / phút nhận diện' },
  ];
  constructor() {
    effect(() => {
      const id = this.projectId();
      this.profiles.set([]);
      this.budget.set(null);
      this.clear();
      this.sceneId = '';
      this.character1 = '';
      this.character2 = '';
      this.location = '';
      this.characterState = '';
      this.entryId = '';
      this.error.set('');
      this.notice.set('');
      this.quotation.set('');
      if (id) void this.reload();
    });
  }
  fields(): { key: keyof ProfileContent; label: string }[] {
    return (
      this.kind === 'character'
        ? [
            ['appearance', 'Ngoại hình'],
            ['outfit', 'Trang phục'],
            ['voice', 'Giọng mô tả'],
          ]
        : [
            ['weather', 'Thời tiết'],
            ['timeOfDay', 'Thời điểm'],
            ['lighting', 'Ánh sáng'],
            ['props', 'Đạo cụ'],
          ]
    )
      .concat([['notes', 'Ghi chú sản xuất (không gửi AI)']])
      .map(([key, label]) => ({ key: key as keyof ProfileContent, label }));
  }
  setField(key: keyof ProfileContent, value: string) {
    this.content = { ...this.content, [key]: value };
  }
  private async api(path: string, body?: unknown, method = 'POST') {
    const res = await fetch(
      '/api' + path,
      body === undefined
        ? { signal: AbortSignal.timeout(30000) }
        : {
            method,
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
            signal: AbortSignal.timeout(30000),
          },
    );
    if (!res.ok) {
      let data: any = {};
      try {
        data = await res.json();
      } catch {}
      throw new Error(data.error || data.detail || 'Yêu cầu thất bại: ' + res.status);
    }
    return res.status === 204 ? null : res.json();
  }
  private async run(action: () => Promise<void>) {
    if (this.busy()) return;
    const id = this.projectId();
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      await action();
    } catch (e) {
      if (id === this.projectId()) this.error.set(e instanceof Error ? e.message : 'Lỗi thao tác');
    } finally {
      this.busy.set(false);
    }
  }
  async reload() {
    const id = this.projectId();
    try {
      const [profiles, budget] = await Promise.all([
        this.api('/projects/' + id + '/profiles'),
        this.api('/projects/' + id + '/budget'),
      ]);
      if (id === this.projectId()) {
        this.profiles.set(profiles);
        this.budget.set(budget);
        this.config = { ...budget.config };
      }
    } catch (e) {
      if (id === this.projectId()) this.error.set(String(e));
    }
  }
  clear() {
    this.editing = null;
    this.kind = 'character';
    this.content = blank();
  }
  edit(p: Profile) {
    this.editing = p;
    this.kind = p.kind;
    this.content = { ...p.content };
  }
  async saveProfile() {
    await this.run(async () => {
      const id = this.projectId();
      await this.api(
        '/projects/' + id + '/profiles' + (this.editing ? '/' + this.editing.id : ''),
        { kind: this.kind, content: this.content, version: this.editing?.version ?? 0 },
        this.editing ? 'PUT' : 'POST',
      );
      if (id !== this.projectId()) return;
      this.clear();
      await this.reload();
      this.notice.set('Đã lưu hồ sơ; bản chụp cũ trong cảnh được giữ.');
    });
  }
  async deleteProfile(p: Profile) {
    if (!confirm('Xóa ' + p.content.name + ' khỏi thư viện? Bản chụp trong cảnh vẫn được giữ.'))
      return;
    await this.run(async () => {
      await this.api('/projects/' + this.projectId() + '/profiles/' + p.id, {}, 'DELETE');
      await this.reload();
    });
  }
  async attach() {
    await this.run(async () => {
      const id = this.projectId(),
        sceneId = this.sceneId;
      const value = await this.api('/projects/' + id + '/profiles/resolve', {
        characterIds: [this.character1, this.character2].filter(Boolean),
        locationId: this.location || null,
        characterState: this.characterState,
      });
      if (id !== this.projectId()) return;
      this.continuity.emit({ sceneId, value });
      this.notice.set('Đã gắn bản chụp vào bản nháp. Lưu storyboard để giữ.');
    });
  }
  detached() {
    this.continuity.emit({ sceneId: this.sceneId, value: null });
  }
  private download(data: unknown, name: string) {
    this.blob(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }), name);
  }
  private blob(blob: Blob, name: string) {
    const url = URL.createObjectURL(blob),
      a = document.createElement('a');
    a.href = url;
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  async exportProfiles() {
    await this.run(async () =>
      this.download(
        await this.api('/projects/' + this.projectId() + '/profiles/export'),
        'flowvideo-profiles.json',
      ),
    );
  }
  async importProfiles(event: Event) {
    const input = event.target as HTMLInputElement,
      file = input.files?.[0];
    input.value = '';
    if (!file) return;
    await this.run(async () => {
      const id = this.projectId();
      if (file.size > 2 * 1024 * 1024) throw new Error('Gói hồ sơ tối đa 2 MiB.');
      await this.api('/projects/' + id + '/profiles/import', JSON.parse(await file.text()));
      await this.reload();
    });
  }
  async downloadBackup() {
    await this.run(async () => {
      const res = await fetch('/api/projects/' + this.projectId() + '/backup', {
        signal: AbortSignal.timeout(180000),
      });
      if (!res.ok) throw new Error((await res.json()).error || 'Không tạo được backup.');
      this.blob(await res.blob(), 'flowvideo-project.zip');
      this.notice.set('Đã tải backup của bản đã lưu.');
    });
  }
  async restore(event: Event) {
    const input = event.target as HTMLInputElement,
      file = input.files?.[0];
    input.value = '';
    if (!file) return;
    await this.run(async () => {
      if (file.size > 243 * 1024 * 1024) throw new Error('ZIP tối đa 243 MiB.');
      const form = new FormData();
      form.append('file', file);
      const res = await fetch('/api/backups/restore', {
        method: 'POST',
        body: form,
        signal: AbortSignal.timeout(180000),
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error || 'Không khôi phục được.');
      this.restored.emit(data.project.id);
    });
  }
  async saveBudget() {
    await this.run(async () => {
      await this.api(
        '/projects/' + this.projectId() + '/budget',
        { version: this.budget().version, config: this.config },
        'PUT',
      );
      await this.reload();
      this.notice.set('Đã lưu ngân sách dự toán.');
    });
  }
  async reprice() {
    await this.run(async () => {
      await this.api('/projects/' + this.projectId() + '/budget/reprice', {});
      await this.reload();
    });
  }
  async exportBudget() {
    await this.run(async () =>
      this.download(
        await this.api('/projects/' + this.projectId() + '/budget/export'),
        'flowvideo-budget.json',
      ),
    );
  }
  async reconcile() {
    await this.run(async () => {
      await this.api(
        '/projects/' + this.projectId() + '/budget/entries/' + this.entryId,
        { actualUsd: this.actualUsd, note: this.reconcileNote },
        'PUT',
      );
      await this.reload();
    });
  }
  async quote() {
    await this.run(async () => {
      const q = await this.api('/projects/' + this.projectId() + '/budget/quote', [
        { kind: this.quoteKind, units: this.quoteUnits },
      ]);
      this.quotation.set(
        'Dự toán: ' +
          (q.estimatedUsd === null ? 'Chưa biết' : q.estimatedUsd + ' USD') +
          ' · ' +
          (q.enabled
            ? q.allowed
              ? 'Đủ ngân sách'
              : 'Bị chặn hoặc thiếu đơn giá'
            : 'Chưa bật chặn ngân sách'),
      );
    });
  }
}
