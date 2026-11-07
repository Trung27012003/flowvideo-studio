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
  private timer?: ReturnType<typeof setInterval>;
  async ngOnInit() {
    await this.perform(async () => {
      this.projects.set(await this.api('/projects'));
      const c = await this.api('/capabilities');
      this.aiEnabled.set(c.scriptAi);
      if (this.projects().length) await this.selectProject(this.projects()[0].id);
    });
    this.timer = setInterval(() => {
      if (this.projectId()) void this.refreshJobs().catch(() => {});
    }, 2500);
  }
  ngOnDestroy() {
    clearInterval(this.timer);
  }
  async api(path: string, init?: RequestInit): Promise<any> {
    const r = await fetch('/api' + path, init);
    if (!r.ok) {
      const body = await r.json().catch(() => ({}));
      throw new Error(body.error || body.detail || 'Yêu cầu thất bại (' + r.status + ').');
    }
    return r.json();
  }
  async perform(fn: () => Promise<void>) {
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
    this.projectId.set(id);
    this.selected.set([]);
    this.preview.set('');
    this.voiceId = '';
    this.musicId = '';
    this.subtitleId = '';
    this.assets.set(await this.api('/projects/' + id + '/assets'));
    await this.refreshJobs();
  }
  async switchProject(id: string) {
    await this.perform(() => this.selectProject(id));
  }
  async refreshJobs() {
    const id = this.projectId();
    const jobs = await this.api('/projects/' + id + '/jobs');
    if (id === this.projectId()) this.jobs.set(jobs);
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
          mode: this.mode,
          duration: this.duration,
          ratio: this.ratio,
          voiceId: this.voiceId || null,
          musicId: this.musicId || null,
          subtitleId: this.subtitleId || null,
        }),
      });
      await this.refreshJobs();
      this.notice.set('Đã thêm vào hàng đợi. Bạn có thể theo dõi ở bên dưới.');
    });
  }
  async retry(id: string) {
    await this.perform(async () => {
      await this.api('/jobs/' + id + '/retry', { method: 'POST' });
      await this.refreshJobs();
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
        } as Record<string, string>
      )[s] || s
    );
  }
}
