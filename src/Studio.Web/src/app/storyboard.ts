import { RenderOptions, defaultEffects } from './render-options';
import { scaleDurations } from './creator-utils';
import { Component, computed, input, model, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
export interface ImageRow {
  generation: {
    id: string;
    sceneId: string;
    status: string;
    message: string;
    model: string;
    size: string;
    quality: string;
    prompt: string;
  };
  asset: { id: string; name: string; kind: string } | null;
}
export interface SceneDraft {
  id: string;
  title: string;
  narration: string;
  prompt: string;
  assetId: string | null;
  duration: number;
  start: number;
}
@Component({
  selector: 'app-storyboard',
  imports: [FormsModule, RenderOptions],
  templateUrl: './storyboard.html',
  styleUrl: './storyboard.scss',
})
export class StoryboardEditor {
  scenes = model.required<SceneDraft[]>();
  ratio = model('9:16');
  voiceId = model('');
  musicId = model('');
  autoCaptions = model(true);
  resolution = model(720);
  voiceGain = model(1);
  musicGain = model(0.18);
  effects = model(defaultEffects());
  fit = model('contain');
  fadeSeconds = model(0);
  duckMusic = model(false);
  exportText = output<string>();
  undoStack = signal<SceneDraft[][]>([]);
  redoStack = signal<SceneDraft[][]>([]);
  actionError = signal('');
  working = signal(false);
  bulkDuration = 5;
  targetDuration = 30;
  findText = '';
  replaceText = '';
  words = computed(
    () =>
      this.scenes()
        .map((s) => s.narration)
        .join(' ')
        .trim()
        .split(/\s+/u)
        .filter(Boolean).length,
  );
  private editing = false;
  private editRecorded = false;
  beginEdit() {
    this.editing = true;
    this.editRecorded = false;
  }
  endEdit() {
    this.editing = false;
  }
  private commit(update: (scenes: SceneDraft[]) => SceneDraft[]) {
    const previous = this.scenes(),
      next = update(previous);
    if (JSON.stringify(previous) === JSON.stringify(next)) return;
    if (!this.editing || !this.editRecorded) {
      this.undoStack.update((stack) => [...stack, structuredClone(previous)].slice(-20));
      this.editRecorded = true;
    }
    this.redoStack.set([]);
    this.scenes.set(next);
    this.actionError.set('');
  }
  undo() {
    const stack = this.undoStack();
    if (!stack.length) return;
    this.redoStack.update((s) => [...s, structuredClone(this.scenes())].slice(-20));
    this.scenes.set(structuredClone(stack[stack.length - 1]));
    this.undoStack.set(stack.slice(0, -1));
  }
  redo() {
    const stack = this.redoStack();
    if (!stack.length) return;
    this.undoStack.update((s) => [...s, structuredClone(this.scenes())].slice(-20));
    this.scenes.set(structuredClone(stack[stack.length - 1]));
    this.redoStack.set(stack.slice(0, -1));
  }
  reverse() {
    this.commit((s) => [...s].reverse());
  }
  edge(index: number, end: boolean) {
    this.commit((s) => {
      const next = [...s],
        [scene] = next.splice(index, 1);
      if (scene) end ? next.push(scene) : next.unshift(scene);
      return next;
    });
  }
  bulk(action: string) {
    try {
      if (action === 'duration') {
        if (
          !Number.isFinite(this.bulkDuration) ||
          this.bulkDuration < 0.1 ||
          this.bulkDuration * this.scenes().length > 600
        )
          throw new Error('Thời lượng tối thiểu 0,1 giây; tổng tối đa 600 giây.');
        this.commit((s) => s.map((c) => ({ ...c, duration: this.bulkDuration })));
      } else if (action === 'scale') {
        const durations = scaleDurations(
          this.scenes().map((s) => s.duration),
          this.targetDuration,
        );
        this.commit((s) => s.map((c, i) => ({ ...c, duration: durations[i] })));
      } else if (action === 'number')
        this.commit((s) => s.map((c, i) => ({ ...c, title: 'Cảnh ' + (i + 1) })));
      else if (action === 'clear')
        this.commit((s) => s.map((c) => ({ ...c, assetId: null, start: 0 })));
      else if (action === 'fill') {
        const assets = this.media();
        if (!assets.length) throw new Error('Thêm ảnh hoặc video vào thư viện trước.');
        let i = 0;
        this.commit((s) =>
          s.map((c) =>
            c.assetId ? c : { ...c, assetId: assets[i++ % assets.length].id, start: 0 },
          ),
        );
      } else if (action === 'replace') {
        if (!this.findText) throw new Error('Nhập lời cần tìm.');
        const next = this.scenes().map((c) => ({
          ...c,
          narration: c.narration.split(this.findText).join(this.replaceText),
        }));
        if (next.some((c) => c.narration.length > 2000))
          throw new Error('Kết quả vượt 2.000 ký tự/cảnh.');
        this.commit(() => next);
      }
    } catch (e) {
      this.actionError.set(e instanceof Error ? e.message : 'Thao tác thất bại.');
    }
  }
  async split(id: string) {
    const scene = this.scenes().find((s) => s.id === id);
    if (
      !scene ||
      this.scenes().length >= 30 ||
      !Number.isFinite(scene.duration) ||
      scene.duration < 0.2
    ) {
      this.actionError.set('Cần ít nhất 0,2 giây và tối đa 30 cảnh.');
      return;
    }
    this.working.set(true);
    this.actionError.set('');
    try {
      const half = Math.round(scene.duration * 500) / 1000;
      if (this.video(scene.assetId)) {
        const response = await fetch('/api/assets/' + scene.assetId + '/metadata', {
          signal: AbortSignal.timeout(30000),
        });
        if (!response.ok) throw new Error('Không đọc được thời lượng nguồn để tách.');
        const metadata = await response.json();
        if (!Number.isFinite(metadata.duration) || scene.start + half >= metadata.duration)
          throw new Error(
            'Điểm tách nằm trong phần giữ khung cuối. Hãy chọn điểm vào hoặc thời lượng ngắn hơn.',
          );
      }
      this.commit((s) =>
        s.flatMap((c) =>
          c.id !== id
            ? [c]
            : [
                { ...c, duration: half },
                {
                  ...c,
                  id: crypto.randomUUID(),
                  title: c.title.slice(0, 110) + ' (phần 2)',
                  duration: Math.round((c.duration - half) * 1000) / 1000,
                  start: this.video(c.assetId) ? c.start + half : 0,
                },
              ],
        ),
      );
    } catch (e) {
      this.actionError.set(e instanceof Error ? e.message : 'Không tách được cảnh.');
    } finally {
      this.working.set(false);
    }
  }
  export = output<void>();
  assets = input<{ id: string; name: string; kind: string }[]>([]);
  disabled = input(false);
  version = input(0);
  imageEnabled = input(false);
  imageJobs = input<ImageRow[]>([]);
  imageUsed = input(0);
  imageLimit = input(10);
  generateImage = output<string>();
  save = output<void>();
  render = output<void>();
  media = computed(() => this.assets().filter((a) => a.kind === 'image' || a.kind === 'video'));
  audio = computed(() => this.assets().filter((a) => a.kind === 'audio'));
  total = computed(() => this.scenes().reduce((sum, s) => sum + (Number(s.duration) || 0), 0));
  add() {
    this.commit((s) => [
      ...s,
      {
        id: crypto.randomUUID(),
        title: 'Cảnh ' + (s.length + 1),
        narration: '',
        prompt: '',
        assetId: null,
        duration: 5,
        start: 0,
      },
    ]);
  }
  duplicate(id: string) {
    if (this.scenes().length >= 30) return;
    this.commit((list) => {
      const index = list.findIndex((s) => s.id === id);
      if (index < 0) return list;
      const clone = {
        ...list[index],
        id: crypto.randomUUID(),
        title: list[index].title.slice(0, 109) + ' (bản sao)',
      };
      return [...list.slice(0, index + 1), clone, ...list.slice(index + 1)];
    });
  }
  preset(name: 'shorts' | 'youtube' | 'draft') {
    if (name === 'shorts') {
      this.ratio.set('9:16');
      this.resolution.set(1080);
    } else if (name === 'youtube') {
      this.ratio.set('16:9');
      this.resolution.set(1080);
    } else this.resolution.set(720);
  }
  change(id: string, patch: Partial<SceneDraft>) {
    this.commit((list) =>
      list.map((s) =>
        s.id === id ? { ...s, ...patch, ...('assetId' in patch ? { start: 0 } : {}) } : s,
      ),
    );
  }
  move(index: number, delta: number) {
    this.commit((s) => {
      const next = [...s],
        target = index + delta;
      if (target >= 0 && target < next.length)
        [next[index], next[target]] = [next[target], next[index]];
      return next;
    });
  }
  remove(id: string) {
    this.commit((s) => s.filter((x) => x.id !== id));
  }
  imageActive(id: string) {
    return this.imageJobs().some(
      (r) =>
        r.generation.sceneId === id &&
        (r.generation.status === 'Queued' || r.generation.status === 'Running'),
    );
  }
  applyImage(row: ImageRow) {
    if (row.asset) this.change(row.generation.sceneId, { assetId: row.asset.id });
  }
  sceneExists(id: string) {
    return this.scenes().some((s) => s.id === id);
  }
  imageStatus(status: string) {
    return (
      (
        {
          Queued: 'Đang chờ',
          Running: 'Đang tạo',
          Succeeded: 'Đã tạo',
          Failed: 'Lỗi',
          Unknown: 'Chưa rõ kết quả',
        } as Record<string, string>
      )[status] || status
    );
  }
  image(id: string | null) {
    return this.assets().some((a) => a.id === id && a.kind === 'image');
  }
  video(id: string | null) {
    return this.assets().some((a) => a.id === id && a.kind === 'video');
  }
}
