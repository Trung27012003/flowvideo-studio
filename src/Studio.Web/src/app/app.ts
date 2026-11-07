import { transformSrt, webVtt } from './creator-utils';
import {
  Component,
  OnDestroy,
  OnInit,
  signal,
  computed,
  ViewChild,
  ElementRef,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { StoryboardEditor, SceneDraft, ImageRow } from './storyboard';
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
  hasSubtitles: boolean;
}
@Component({
  selector: 'app-root',
  imports: [FormsModule, StoryboardEditor],
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
  imageEnabled = signal(false);
  imageJobs = signal<ImageRow[]>([]);
  imageUsed = signal(0);
  imageLimit = signal(10);
  private imageRequestVersion = 0;
  speechEnabled = signal(false);
  transcriptionEnabled = signal(false);
  voices = signal<string[]>([]);
  speechText = '';
  speechName = 'Lời đọc AI';
  speechVoice = 'alloy';
  speechSpeed = 1;
  speechPreview = signal('');
  sourceId = '';
  language = 'vi';
  subtitleName = 'Phụ đề';
  subtitleContent = '';
  subtitleToEdit = '';
  savedSubtitle = signal<Asset | null>(null);
  transcriptionSources = computed(() =>
    this.assets().filter((a) => a.kind === 'audio' || a.kind === 'video'),
  );
  libraryQuery = signal('');
  libraryKind = signal('all');
  librarySort = signal('name-asc');
  librarySelected = signal<string[]>([]);
  metadata = signal<Record<string, unknown> | null>(null);
  renameProjectText = '';
  renameAssetText = '';
  offsetSeconds = 0;
  subtitleSpeed = 1;
  subtitleFind = '';
  subtitleReplace = '';
  wrapLength = 42;
  mergeGap = 0.2;
  boardFit = signal('contain');
  boardFade = signal(0);
  boardDuck = signal(false);
  fit = 'contain';
  fadeSeconds = 0;
  duckMusic = false;

  private normalized(text: string) {
    return text
      .toLowerCase()
      .normalize('NFD')
      .replace(/\p{Diacritic}/gu, '')
      .replace(/đ/g, 'd');
  }
  filteredAssets = computed(() => {
    const query = this.normalized(this.libraryQuery().trim());
    return this.assets()
      .filter(
        (a) =>
          (this.libraryKind() === 'all' || a.kind === this.libraryKind()) &&
          this.normalized(a.name).includes(query),
      )
      .sort((a, b) => {
        const key = this.librarySort().startsWith('kind') ? 'kind' : 'name';
        const order =
          a[key].localeCompare(b[key], 'vi', { numeric: true }) || a.id.localeCompare(b.id);
        return this.librarySort().endsWith('desc') ? -order : order;
      });
  });
  assetPreview = signal<Asset | null>(null);
  previewText = signal('');
  previewError = signal('');
  @ViewChild('mediaDialog', { static: true }) mediaDialog!: ElementRef<HTMLDialogElement>;
  projectName = '';
  mode = 'concat';
  duration = 30;
  ratio = '9:16';
  voiceId = '';
  musicId = '';
  subtitleId = '';
  idea = '';
  script = signal('');
  scenes = signal<SceneDraft[]>([]);
  documentVersion = signal(0);
  documentLoaded = signal(false);
  boardRatio = signal('9:16');
  boardVoice = signal('');
  boardMusic = signal('');
  boardCaptions = signal(true);
  boardResolution = signal(720);
  boardVoiceGain = signal(1);
  boardMusicGain = signal(0.18);
  resolution = 720;
  voiceVolume = 100;
  musicVolume = 18;
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
      this.imageEnabled.set(capabilities.imageAi);
      this.speechEnabled.set(capabilities.tts);
      this.transcriptionEnabled.set(capabilities.transcription);
      this.voices.set(capabilities.voices || []);
      let preferred = '';
      try {
        preferred = localStorage.getItem('flowvideo.project') || '';
      } catch {}
      const project = this.projects().find((p) => p.id === preferred) || this.projects()[0];
      if (project) await this.selectProject(project.id);
    });
    this.schedulePoll();
  }
  private schedulePoll() {
    clearTimeout(this.timer);
    if (this.destroyed) return;
    const active =
      this.jobs().some((j) => j.status === 'Queued' || j.status === 'Running') ||
      this.imageJobs().some(
        (r) => r.generation.status === 'Queued' || r.generation.status === 'Running',
      );
    const delay = this.syncError() ? 30000 : active ? 2500 : 15000;
    this.timer = setTimeout(async () => {
      if (!document.hidden && this.projectId()) {
        try {
          await Promise.all([this.refreshJobs(), this.refreshImages()]);
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
    this.renameProjectText = this.projects().find((p) => p.id === id)?.name || '';
    this.librarySelected.set([]);
    this.boardFit.set('contain');
    this.boardFade.set(0);
    this.boardDuck.set(false);
    this.fit = 'contain';
    this.fadeSeconds = 0;
    this.duckMusic = false;
    this.libraryQuery.set('');
    this.libraryKind.set('all');
    if (this.mediaDialog?.nativeElement.open) this.mediaDialog.nativeElement.close();
    this.boardResolution.set(720);
    this.boardVoiceGain.set(1);
    this.boardMusicGain.set(0.18);
    this.resolution = 720;
    this.voiceVolume = 100;
    this.musicVolume = 18;
    this.idea = '';
    this.script.set('');
    this.scenes.set([]);
    this.documentVersion.set(0);
    this.documentLoaded.set(false);
    this.boardRatio.set('9:16');
    this.boardVoice.set('');
    this.boardMusic.set('');
    this.boardCaptions.set(true);
    this.assets.set([]);
    this.jobs.set([]);
    this.imageJobs.set([]);
    this.imageUsed.set(0);
    this.selected.set([]);
    this.cuts.set({});
    this.speechPreview.set('');
    this.sourceId = '';
    this.subtitleToEdit = '';
    this.subtitleContent = '';
    this.savedSubtitle.set(null);
    this.preview.set('');
    this.voiceId = '';
    this.musicId = '';
    this.subtitleId = '';
    await Promise.all([
      this.api('/projects/' + id + '/assets').then((assets) => {
        if (version === this.projectVersion && !this.destroyed) this.assets.set(assets);
      }),
      this.refreshJobs(),
      this.refreshImages(),
      this.api('/projects/' + id + '/document').then((result) => {
        if (version !== this.projectVersion || this.destroyed) return;
        const doc = result.document;
        this.idea = doc.idea;
        this.script.set(doc.script);
        this.scenes.set(doc.scenes || []);
        this.boardRatio.set(doc.ratio);
        this.boardVoice.set(doc.voiceId || '');
        this.boardMusic.set(doc.musicId || '');
        this.boardCaptions.set(doc.autoCaptions);
        this.boardFit.set(doc.fit ?? 'contain');
        this.boardFade.set(doc.fadeSeconds ?? 0);
        this.boardDuck.set(doc.duckMusic ?? false);
        this.boardResolution.set(doc.resolution ?? 720);
        this.boardVoiceGain.set(doc.voiceGain ?? 1);
        this.boardMusicGain.set(doc.musicGain ?? 0.18);
        this.documentVersion.set(result.version);
        this.documentLoaded.set(true);
      }),
    ]);
    try {
      localStorage.setItem('flowvideo.project', id);
    } catch {}
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
  async refreshImages() {
    const id = this.projectId();
    const version = ++this.imageRequestVersion;
    const result = await this.api('/projects/' + id + '/image-generations');
    if (id !== this.projectId() || version !== this.imageRequestVersion || this.destroyed) return;
    this.imageJobs.set(result.items);
    this.imageUsed.set(result.used);
    this.imageLimit.set(result.dailyLimit);
    for (const row of result.items as ImageRow[]) if (row.asset) this.addAsset(row.asset);
  }
  async generateSceneImage(sceneId: string) {
    await this.perform(async () => {
      const version = await this.storeDocument();
      const result = await this.api('/projects/' + this.projectId() + '/image-generations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ requestId: crypto.randomUUID(), sceneId, version }),
      });
      await this.refreshImages();
      this.schedulePoll();
      this.notice.set(
        result.reused
          ? 'Yêu cầu đã có kết quả hoặc đang xử lý; không tạo thêm lượt. Chọn ảnh trong danh sách bên dưới.'
          : 'Đã lưu storyboard và xếp hàng tạo ảnh. Bạn có thể tiếp tục chỉnh cảnh trong khi chờ.',
      );
    });
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
          fit: this.fit,
          fadeSeconds: this.fadeSeconds,
          duckMusic: this.duckMusic,
          resolution: this.resolution,
          voiceGain: this.voiceVolume / 100,
          musicGain: this.musicVolume / 100,
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
  private addAsset(asset: Asset) {
    this.assets.update((assets) =>
      assets.some((a) => a.id === asset.id) ? assets : [...assets, asset],
    );
  }
  async generateSpeech() {
    await this.perform(async () => {
      const asset = await this.api('/projects/' + this.projectId() + '/speech', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          text: this.speechText,
          voice: this.speechVoice,
          speed: this.speechSpeed,
          name: this.speechName,
        }),
      });
      this.addAsset(asset);
      this.voiceId = asset.id;
      this.sourceId = asset.id;
      this.speechPreview.set('/api/assets/' + asset.id + '/file');
      this.notice.set(
        'Đã lưu giọng đọc AI vào thư viện và chọn làm giọng lồng tiếng trong Studio.',
      );
    });
  }
  private async subtitleFile(id: string): Promise<string> {
    const response = await fetch('/api/assets/' + id + '/file', {
      signal: AbortSignal.any([this.controller.signal, AbortSignal.timeout(30000)]),
    });
    if (!response.ok) throw new Error('Không đọc được file phụ đề.');
    return response.text();
  }
  async transcribe() {
    await this.perform(async () => {
      const result = await this.api('/projects/' + this.projectId() + '/transcriptions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ assetId: this.sourceId, language: this.language }),
      });
      this.addAsset(result.asset);
      this.subtitleId = result.asset.id;
      this.subtitleToEdit = result.asset.id;
      this.savedSubtitle.set(result.asset);
      this.subtitleContent = await this.subtitleFile(result.asset.id);
      this.notice.set(
        'Đã tạo và lưu phụ đề AI. Hãy kiểm tra nội dung và mốc thời gian trước khi render.',
      );
    });
  }
  async loadSubtitle() {
    await this.perform(async () => {
      this.subtitleContent = await this.subtitleFile(this.subtitleToEdit);
      this.savedSubtitle.set(this.assets().find((a) => a.id === this.subtitleToEdit) || null);
    });
  }
  async saveSubtitle() {
    await this.perform(async () => {
      const result = await this.api('/projects/' + this.projectId() + '/subtitles', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: this.subtitleName, content: this.subtitleContent }),
      });
      this.addAsset(result.asset);
      this.subtitleId = result.asset.id;
      this.subtitleToEdit = result.asset.id;
      this.savedSubtitle.set(result.asset);
      this.subtitleContent = await this.subtitleFile(result.asset.id);
      this.notice.set('Đã lưu bản phụ đề mới và chọn trong Studio. Các bản trước vẫn được giữ.');
    });
  }
  private async storeDocument() {
    if (!this.documentLoaded())
      throw new Error('Chưa tải xong dự án. Hãy tải lại trang trước khi lưu.');
    const result = await this.api('/projects/' + this.projectId() + '/document', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        version: this.documentVersion(),
        document: {
          idea: this.idea,
          script: this.script(),
          scenes: this.scenes(),
          ratio: this.boardRatio(),
          voiceId: this.boardVoice() || null,
          musicId: this.boardMusic() || null,
          autoCaptions: this.boardCaptions(),
          fit: this.boardFit(),
          fadeSeconds: this.boardFade(),
          duckMusic: this.boardDuck(),
          resolution: this.boardResolution(),
          voiceGain: this.boardVoiceGain(),
          musicGain: this.boardMusicGain(),
        },
      }),
    });
    this.documentVersion.set(result.version);
    return result.version;
  }
  async saveDocument() {
    await this.perform(async () => {
      await this.storeDocument();
      this.notice.set('Đã lưu kịch bản và storyboard vào dự án.');
    });
  }
  splitScript() {
    const paragraphs = this.script()
      .trim()
      .split(/\n\s*\n/)
      .map((s) => s.trim())
      .filter(Boolean);
    if (paragraphs.length > 30 || paragraphs.some((p) => p.length > 2000)) {
      this.error.set('Tối đa 30 đoạn, mỗi đoạn 2.000 ký tự. Hãy chia hoặc rút gọn trước.');
      return;
    }
    if (this.scenes().length + paragraphs.length > 30) {
      this.error.set('Thêm các đoạn này sẽ vượt quá 30 cảnh. Hãy bớt cảnh trước.');
      return;
    }
    this.scenes.update((s) => [
      ...s,
      ...paragraphs.map((text, i) => ({
        id: crypto.randomUUID(),
        title: 'Cảnh ' + (s.length + i + 1),
        narration: text,
        prompt: '',
        assetId: null,
        duration: 5,
        start: 0,
      })),
    ]);
    this.tab.set('board');
    this.notice.set(
      'Đã thêm mỗi đoạn thành một cảnh. Chọn tư liệu, sửa lời và thời lượng rồi lưu.',
    );
  }
  async renderStoryboard() {
    await this.perform(async () => {
      const version = await this.storeDocument();
      await this.api('/projects/' + this.projectId() + '/storyboard/render', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ version }),
      });
      await this.refreshJobs();
      this.schedulePoll();
      this.tab.set('studio');
      this.notice.set(
        'Đã lưu và đưa storyboard vào hàng đợi. Chỉnh sửa tiếp theo không thay đổi tác vụ này.',
      );
    });
  }
  async openAsset(asset: Asset) {
    this.assetPreview.set(asset);
    this.metadata.set(null);
    this.renameAssetText = asset.name;
    this.previewError.set('');
    this.previewText.set('Đang tải…');
    if (!this.mediaDialog.nativeElement.open) this.mediaDialog.nativeElement.showModal();
    if (asset.kind === 'subtitle') {
      try {
        const response = await fetch('/api/assets/' + asset.id + '/file', {
          signal: AbortSignal.any([this.controller.signal, AbortSignal.timeout(30000)]),
        });
        if (!response.ok) throw new Error('Không đọc được tài nguyên.');
        if (Number(response.headers.get('Content-Length')) > 131072) {
          await response.body?.cancel();
          throw new Error('SRT lớn hơn 128 KiB. Tải file để xem đầy đủ.');
        }
        const text = await response.text();
        if (this.assetPreview()?.id === asset.id) this.previewText.set(text);
      } catch (e) {
        if (this.assetPreview()?.id === asset.id)
          this.previewError.set(e instanceof Error ? e.message : 'Không đọc được file.');
      }
    }
  }
  private downloadJson(value: unknown, name: string) {
    const url = URL.createObjectURL(
      new Blob([JSON.stringify(value, null, 2)], { type: 'application/json' }),
    );
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  async exportStoryboard() {
    await this.perform(async () => {
      await this.storeDocument();
      const data = await this.api('/projects/' + this.projectId() + '/storyboard/export');
      this.downloadJson(data, 'flowvideo-storyboard.json');
      this.notice.set('Đã lưu và xuất storyboard JSON. File không kèm media.');
    });
  }
  async importStoryboard(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    await this.perform(async () => {
      if (file.size > 2097152) throw new Error('File JSON tối đa 2 MiB.');
      const content = (await file.text()).replace(/^\uFEFF/, '');
      try {
        JSON.parse(content);
      } catch {
        throw new Error('File JSON không hợp lệ.');
      }
      if (this.projectId() && this.documentLoaded()) await this.storeDocument();
      const result = await this.api('/storyboards/import', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: content,
      });
      this.projects.update((list) => [result.project, ...list]);
      await this.selectProject(result.project.id);
      this.tab.set('board');
      this.notice.set(
        'Đã nhập thành dự án mới. Media không đi kèm; hãy chọn lại tư liệu và audio.',
      );
    });
    input.value = '';
  }
  async renameProject() {
    await this.perform(async () => {
      const project = await this.api('/projects/' + this.projectId(), {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: this.renameProjectText }),
      });
      this.projects.update((p) => p.map((x) => (x.id === project.id ? project : x)));
      this.notice.set('Đã đổi tên dự án.');
    });
  }
  async duplicateProject() {
    await this.perform(async () => {
      await this.storeDocument();
      const result = await this.api('/projects/' + this.projectId() + '/duplicate', {
        method: 'POST',
      });
      this.projects.update((p) => [result.project, ...p]);
      await this.selectProject(result.project.id);
      this.notice.set('Đã nhân bản dự án và ' + result.copiedAssets + ' media độc lập.');
    });
  }
  async renameAsset() {
    const asset = this.assetPreview();
    if (!asset) return;
    await this.perform(async () => {
      const updated = await this.api('/assets/' + asset.id, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: this.renameAssetText }),
      });
      this.assets.update((a) => a.map((x) => (x.id === updated.id ? updated : x)));
      this.assetPreview.set(updated);
      this.renameAssetText = updated.name;
    });
  }
  async inspectAsset() {
    const asset = this.assetPreview();
    if (!asset) return;
    await this.perform(async () => {
      const value = await this.api('/assets/' + asset.id + '/metadata');
      if (this.assetPreview()?.id === asset.id) this.metadata.set(value);
    });
  }
  metadataEntries() {
    return Object.entries(this.metadata() || {}).filter(([, v]) => v !== null);
  }
  toggleLibrary(id: string) {
    this.librarySelected.update((s) =>
      s.includes(id) ? s.filter((x) => x !== id) : s.length < 20 ? [...s, id] : s,
    );
  }
  selectVisible() {
    this.librarySelected.set(
      this.filteredAssets()
        .slice(0, 20)
        .map((a) => a.id),
    );
  }
  private downloadBlob(blob: Blob, name: string) {
    const url = URL.createObjectURL(blob),
      link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  async downloadWorkspace(kind: string) {
    await this.perform(async () => {
      if (kind === 'narration' || kind === 'shotlist') await this.storeDocument();
      const suffix =
        kind === 'archive'
          ? 'assets/archive'
          : kind === 'manifest'
            ? 'assets/manifest'
            : 'storyboard/' + kind;
      const response = await fetch('/api/projects/' + this.projectId() + '/' + suffix, {
        method: kind === 'archive' ? 'POST' : 'GET',
        headers: kind === 'archive' ? { 'Content-Type': 'application/json' } : {},
        body: kind === 'archive' ? JSON.stringify({ assetIds: this.librarySelected() }) : undefined,
        signal: AbortSignal.any([this.controller.signal, AbortSignal.timeout(120000)]),
      });
      if (!response.ok) {
        const result = await response.json().catch(() => ({}));
        throw new Error(
          result.error || result.detail || 'Không xuất được file (' + response.status + ').',
        );
      }
      const extension = kind === 'archive' ? 'zip' : kind === 'narration' ? 'txt' : 'csv';
      this.downloadBlob(await response.blob(), 'flowvideo-' + kind + '.' + extension);
      this.notice.set('Đã xuất ' + kind + '.');
    });
  }
  subtitleTool(action: string) {
    this.error.set('');
    this.notice.set('');
    try {
      if (action === 'vtt')
        this.downloadBlob(
          new Blob([webVtt(this.subtitleContent)], { type: 'text/vtt;charset=utf-8' }),
          'flowvideo-subtitles.vtt',
        );
      else {
        const amount =
          action === 'offset'
            ? this.offsetSeconds
            : action === 'speed'
              ? this.subtitleSpeed
              : action === 'wrap'
                ? this.wrapLength
                : this.mergeGap;
        this.subtitleContent = transformSrt(
          this.subtitleContent,
          action,
          amount,
          this.subtitleFind,
          this.subtitleReplace,
        );
        this.notice.set('Đã sửa bản nháp SRT. Bấm Lưu bản phụ đề để giữ bản mới.');
      }
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : 'Không xử lý được SRT.');
    }
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
