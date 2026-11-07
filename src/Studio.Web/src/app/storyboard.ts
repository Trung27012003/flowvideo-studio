import { Component, computed, input, model, output } from '@angular/core';
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
  imports: [FormsModule],
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
    this.scenes.update((s) => [
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
    this.scenes.update((list) => {
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
    this.scenes.update((list) =>
      list.map((s) =>
        s.id === id ? { ...s, ...patch, ...('assetId' in patch ? { start: 0 } : {}) } : s,
      ),
    );
  }
  move(index: number, delta: number) {
    this.scenes.update((s) => {
      const next = [...s],
        target = index + delta;
      if (target >= 0 && target < next.length)
        [next[index], next[target]] = [next[target], next[index]];
      return next;
    });
  }
  remove(id: string) {
    this.scenes.update((s) => s.filter((x) => x.id !== id));
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
