import { Component, OnDestroy, OnInit, signal, computed } from '@angular/core';
import { FormsModule } from '@angular/forms';
interface Project {
  id: string;
  name: string;
}
interface Asset {
  id: string;
  name: string;
  kind: string;
}
interface Job {
  id: string;
  status: string;
  message: string;
  createdAt: string;
}
@Component({
  selector: 'app-root',
  imports: [FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  projects = signal<Project[]>([]);
  projectId = signal('');
  assets = signal<Asset[]>([]);
  jobs = signal<Job[]>([]);
  selected = signal<string[]>([]);
  tab = signal('studio');
  busy = signal(false);
  error = signal('');
  notice = signal('');
  preview = signal('');
  audio = computed(() => this.assets().filter((a) => a.kind === 'audio'));
  subtitles = computed(() => this.assets().filter((a) => a.kind === 'subtitle'));
  media = computed(() => this.assets().filter((a) => a.kind === 'video' || a.kind === 'image'));
  aiEnabled = signal(false);
  projectName = '';
  mode = 'concat';
  duration = 30;
  ratio = '9:16';
  voiceId = '';
  musicId = '';
  subtitleId = '';
  idea = '';
  script = signal('');
  cuts = signal<Record<string, { start: number; end: number | null }>>({});
  syncError = signal(false);
  private destroyed = false;
  private requestVersion = 0;
  private projectVersion = 0;
  private controller = new AbortController();
  private timer?: ReturnType<typeof setInterval>;
  async ngOnInit() {
    await this.perform(async () => {
      const [projects, capabilities] = await Promise.all([
        this.api('/projects'),
        this.api('/capabilities'),
      ]);
      this.projects.set(projects);
      this.aiEnabled.set(capabilities.scriptAi);
      if (this.projects().length) await this.selectProject(this.projects()[0].id);
    });
    this.schedulePoll();
  }
  private schedulePoll() {
    clearTimeout(this.timer);
    if (this.destroyed) return;
    const active = this.jobs().some((j) => j.status === 'Queued' || j.status === 'Running');
    const delay = this.syncError() ? 30000 : active ? 2500 : 15000;
    this.timer = setTimeout(async () => {
      if (!document.hidden && this.projectId()) {
        try {
          await this.refreshJobs();
          this.syncError.set(false);
        } catch {
          if (!this.destroyed) this.syncError.set(true);
        }
      }
      this.schedulePoll();
    }, delay);
  }
  ngOnDestroy() {
    this.destroyed = true;
    clearTimeout(this.timer);
    this.controller.abort();
  }
  async api(path: string, init?: RequestInit): Promise<any> {
    const r = await fetch('/api' + path, {
      ...init,
      signal: AbortSignal.any([this.controller.signal, AbortSignal.timeout(120000)]),
    });
    if (!r.ok) {
      const body = await r.json().catch(() => ({}));
      throw new Error(body.error || body.detail || 'Yêu cầu thất bại (' + r.status + ').');
    }
    return r.json();
  }
  async perform(fn: () => Promise<void>) {
    if (this.busy() || this.destroyed) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      await fn();
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : 'Có lỗi xảy ra.');
    } finally {
      this.busy.set(false);
    }
  }
  async createProject() {
    await this.perform(async () => {
      const p = await this.api('/projects', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: this.projectName }),
      });
      this.projects.update((x) => [p, ...x]);
      this.projectName = '';
      await this.selectProject(p.id);
    });
  }
  async selectProject(id: string) {
    const version = ++this.projectVersion;
    this.projectId.set(id);
    this.assets.set([]);
    this.jobs.set([]);
    this.selected.set([]);
    this.cuts.set({});
    this.preview.set('');
    this.voiceId = '';
    this.musicId = '';
    this.subtitleId = '';
    await Promise.all([
      this.api('/projects/' + id + '/assets').then((assets) => {
        if (version === this.projectVersion && !this.destroyed) this.assets.set(assets);
      }),
      this.refreshJobs(),
    ]);
    this.schedulePoll();
  }
  async switchProject(id: string) {
    await this.perform(() => this.selectProject(id));
  }
  async refreshJobs() {
    const id = this.projectId();
    const version = ++this.requestVersion;
    const jobs = await this.api('/projects/' + id + '/jobs');
    if (id === this.projectId() && version === this.requestVersion && !this.destroyed)
      this.jobs.set(jobs);
  }
  setCut(id: string, field: 'start' | 'end', value: number | null) {
    this.cuts.update((cuts) => ({
      ...cuts,
      [id]: {
        ...(cuts[id] || { start: 0, end: null }),
        [field]: field === 'start' ? (value ?? 0) : value,
      },
    }));
  }
  isVideo(id: string) {
    return this.assets().some((a) => a.id === id && a.kind === 'video');
  }
  async upload(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files || []);
    await this.perform(async () => {
      for (const f of files) {
        const form = new FormData();
        form.append('file', f);
        const asset = await this.api('/projects/' + this.projectId() + '/assets', {
          method: 'POST',
          body: form,
        });
        this.assets.update((a) => [...a, asset]);
      }
      this.notice.set('Đã nhập ' + files.length + ' file.');
    });
    input.value = '';
  }
  toggle(id: string) {
    this.selected.update((a) => (a.includes(id) ? a.filter((x) => x !== id) : [...a, id]));
  }
  move(id: string, delta: number) {
    this.selected.update((a) => {
      const next = [...a];
      const i = next.indexOf(id);
      const j = i + delta;
      if (j >= 0 && j < next.length) [next[i], next[j]] = [next[j], next[i]];
      return next;
    });
  }
  name(id: string) {
    return this.assets().find((a) => a.id === id)?.name || id;
  }
  async render() {
    await this.perform(async () => {
      await this.api('/projects/' + this.projectId() + '/renders', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          assetIds: this.selected(),
          cuts: this.selected()
            .filter((id) => this.isVideo(id) && this.cuts()[id])
            .map((id) => ({ assetId: id, ...this.cuts()[id] })),
          mode: this.mode,
          duration: this.duration,
          ratio: this.ratio,
          voiceId: this.voiceId || null,
          musicId: this.musicId || null,
          subtitleId: this.subtitleId || null,
        }),
      });
      await this.refreshJobs();
      this.schedulePoll();
      this.notice.set('Đã thêm vào hàng đợi. Bạn có thể theo dõi ở bên dưới.');
    });
  }
  async retry(id: string) {
    await this.perform(async () => {
      await this.api('/jobs/' + id + '/retry', { method: 'POST' });
      await this.refreshJobs();
      this.schedulePoll();
    });
  }
  async cancel(id: string) {
    await this.perform(async () => {
      await this.api('/jobs/' + id + '/cancel', { method: 'POST' });
      await this.refreshJobs();
      this.schedulePoll();
    });
  }
  async generateScript() {
    await this.perform(async () => {
      const result = await this.api('/ai/script', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ idea: this.idea }),
      });
      this.script.set(result.script);
    });
  }
  status(s: string) {
    return (
      (
        {
          Queued: 'Đang chờ',
          Running: 'Đang xử lý',
          Succeeded: 'Hoàn tất',
          Failed: 'Lỗi',
          Cancelled: 'Đã hủy',
        } as Record<string, string>
      )[s] || s
    );
  }
}
