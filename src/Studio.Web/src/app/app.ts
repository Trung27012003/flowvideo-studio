import { GraphicsOptions, defaultBranding, defaultTransitions } from './graphics-options';
import { CreativeWorkspace, Continuity } from './creative-workspace';
import { StoryArchitect, StoryPlanView, StoryPlanOptions, StoryPlanEdit } from './story-architect';
import { ProductionStudio, ProductionOptions, ProductionView } from './production-studio';
import { VideoStudio, VideoRow, VideoRequestOptions } from './video-studio';
import { AudioOptions, defaultAudioTuning } from './audio-options';
import { CaptionOptions, defaultCaptionStyle } from './caption-options';
import { defaultSceneOptions } from './scene-options';
import { RenderOptions, defaultEffects } from './render-options';
import {
  transformSrt,
  webVtt,
  subtitleAnalysis,
  subtitlePlainText,
  parseSrt,
  stamp,
  compareDocuments,
  subtitleRange,
  mergeSrt,
  subtitleCsv,
  Cue,
} from './creator-utils';
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
  hasAudio: boolean;
}
@Component({
  selector: 'app-root',
  imports: [
    FormsModule,
    GraphicsOptions,
    CreativeWorkspace,
    StoryboardEditor,
    RenderOptions,
    CaptionOptions,
    AudioOptions,
    VideoStudio,
    ProductionStudio,
    StoryArchitect,
  ],
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
  @ViewChild(StoryArchitect) architect?: StoryArchitect;
  storyPlans = signal<StoryPlanView[]>([]);
  storyUsed = signal(0);
  storyLimit = signal(10);
  private storyRequestVersion = 0;
  qaReport = signal<any>(null);
  productionRuns = signal<ProductionView[]>([]);
  productionEstimate = signal<any>(null);
  private productionRequestVersion = 0;
  private productionSignature = '';
  videoEnabled = signal(false);
  videoJobs = signal<VideoRow[]>([]);
  videoUsed = signal(0);
  videoLimit = signal(5);
  private videoRequestVersion = 0;
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
  assetPreferences = signal<any[]>([]);
  favoritesOnly = signal(false);
  libraryTag = signal('');
  preferenceTags = '';
  preferenceNote = '';
  preferenceFavorite = false;
  libraryReport = signal('');
  copyTarget = '';
  bulkPrefix = 'Clip-';
  bulkSuffix = '';
  bulkStart = 1;
  audioTuning = signal(defaultAudioTuning());
  boardAudioTuning = signal(defaultAudioTuning());
  deliveryTools = [
    { kind: 'wav', label: 'Tải audio WAV' },
    { kind: 'flac', label: 'Tải audio FLAC' },
    { kind: 'mute', label: 'Tải MP4 tắt tiếng' },
    { kind: 'gif', label: 'Tải GIF xem nhanh' },
    { kind: 'poster', label: 'Tải poster PNG' },
    { kind: 'contact', label: 'Tải bảng ảnh video' },
    { kind: 'bundle', label: 'Tải gói giao video ZIP' },
  ];
  deliveryJob = '';
  deliveryAt = 0;
  deliveryDuration = 3;
  deliveryInfo = signal('');
  subtitleTableQuery = signal('');
  filteredSubtitleRows = computed(() =>
    this.subtitleRows()
      .map((cue, index) => ({ cue, index }))
      .filter((row) =>
        this.normalized(row.cue.text).includes(this.normalized(this.subtitleTableQuery().trim())),
      ),
  );
  subtitleRangeStart = 0;
  subtitleRangeEnd = 30;
  subtitleMergeMode = 'append';
  subtitleMergeGap = 0.2;
  captionStyle = signal(defaultCaptionStyle());
  boardSubtitleStyle = signal(defaultCaptionStyle());
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
  @ViewChild(StoryboardEditor) storyboardEditor?: StoryboardEditor;
  branding = signal(defaultBranding());
  transitions = signal(defaultTransitions());
  boardBranding = signal(defaultBranding());
  boardTransitions = signal(defaultTransitions());
  attachContinuity(event: { sceneId: string; value: Continuity | null }) {
    if (this.storyboardEditor)
      this.storyboardEditor.change(event.sceneId, { continuity: event.value });
    else
      this.scenes.update((s) =>
        s.map((scene) =>
          scene.id === event.sceneId ? { ...scene, continuity: event.value } : scene,
        ),
      );
  }
  async restoredProject(id: string) {
    await this.perform(async () => {
      this.projects.set(await this.api('/projects'));
      await this.selectProject(id);
      this.notice.set('Đã khôi phục thành dự án mới, chưa gọi AI.');
    });
  }
  effects = signal(defaultEffects());
  boardEffects = signal(defaultEffects());
  savedItems = signal<any[]>([]);
  itemName = 'Mốc mới';
  boardSession = signal(0);
  comparison = signal('');
  workflowReport = signal('');
  subtitleRows = signal<Cue[]>([]);
  subtitleMinimum = 1;
  subtitleCase = 'upper';
  jobQuery = signal('');
  queueStats = computed(() =>
    ['Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled'].map((status) => ({
      status,
      count: this.jobs().filter((j) => j.status === status).length,
    })),
  );
  jobFilter = signal('all');
  jobSort = signal('newest');
  filteredJobs = computed(() =>
    this.jobs()
      .filter(
        (j) =>
          (this.jobFilter() === 'all' || j.status === this.jobFilter()) &&
          (j.id + ' ' + j.message + ' ' + this.status(j.status))
            .toLocaleLowerCase('vi')
            .includes(this.jobQuery().trim().toLocaleLowerCase('vi')),
      )
      .slice()
      .sort((a, b) =>
        this.jobSort() === 'newest'
          ? b.createdAt.localeCompare(a.createdAt)
          : a.createdAt.localeCompare(b.createdAt),
      ),
  );
  frameAt = 0;
  derivedPreview = signal('');
  private derivedUrl = '';
  subtitleSplit = 80;
  subtitleClamp = 30;
  subtitleReport = signal('');
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
          (!this.favoritesOnly() || this.preference(a.id).favorite) &&
          (!this.libraryTag().trim() ||
            this.preference(a.id).tags.some((tag: string) =>
              this.normalized(tag).includes(this.normalized(this.libraryTag().trim())),
            )) &&
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
  loopStyle = 'direct';
  loopCrossfade = 0.3;
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
      this.videoEnabled.set(capabilities.aiVideo);
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
      this.storyPlans().some((p) => ['Queued', 'Running'].includes(p.status)) ||
      this.productionRuns().some((r) =>
        ['WaitingAssets', 'Speaking', 'Preparing', 'Rendering'].includes(r.status),
      ) ||
      this.videoJobs().some((r) => ['Queued', 'Running'].includes(r.generation.status)) ||
      this.jobs().some((j) => j.status === 'Queued' || j.status === 'Running') ||
      this.imageJobs().some(
        (r) => r.generation.status === 'Queued' || r.generation.status === 'Running',
      );
    const delay = this.syncError() ? 30000 : active ? 2500 : 15000;
    this.timer = setTimeout(async () => {
      if (!document.hidden && this.projectId()) {
        try {
          await Promise.all([
            this.refreshJobs(),
            this.refreshImages(),
            this.refreshVideos(),
            this.refreshProduction(),
            this.refreshStoryPlans(),
          ]);
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
    this.clearDerived();
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
    return r.status === 204 ? null : r.json();
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
    this.audioTuning.set(defaultAudioTuning());
    this.boardAudioTuning.set(defaultAudioTuning());
    this.deliveryJob = '';
    this.deliveryInfo.set('');
    this.subtitleTableQuery.set('');
    this.assetPreferences.set([]);
    this.favoritesOnly.set(false);
    this.libraryTag.set('');
    this.libraryReport.set('');
    this.copyTarget = '';
    this.captionStyle.set(defaultCaptionStyle());
    this.boardSubtitleStyle.set(defaultCaptionStyle());
    this.savedItems.set([]);
    this.comparison.set('');
    this.workflowReport.set('');
    this.subtitleRows.set([]);
    this.jobQuery.set('');
    this.jobFilter.set('all');
    this.boardSession.update((v) => v + 1);
    this.renameProjectText = this.projects().find((p) => p.id === id)?.name || '';
    this.librarySelected.set([]);
    this.boardFit.set('contain');
    this.boardFade.set(0);
    this.boardDuck.set(false);
    this.branding.set(defaultBranding());
    this.transitions.set(defaultTransitions());
    this.boardBranding.set(defaultBranding());
    this.boardTransitions.set(defaultTransitions());
    this.effects.set(defaultEffects());
    this.boardEffects.set(defaultEffects());
    this.clearDerived();
    this.subtitleReport.set('');
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
    this.videoJobs.set([]);
    this.productionRuns.set([]);
    this.storyPlans.set([]);
    this.storyUsed.set(0);
    this.qaReport.set(null);
    this.architect?.close();
    this.productionEstimate.set(null);
    this.productionSignature = '';
    this.videoUsed.set(0);
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
      this.refreshVideos(),
      this.refreshProduction(),
      this.refreshStoryPlans(),
      this.refreshSavedItems(),
      this.refreshPreferences(),
      this.api('/projects/' + id + '/document').then((result) => {
        if (version !== this.projectVersion || this.destroyed) return;
        const doc = result.document;
        this.applyDraft(doc);
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
  async refreshStoryPlans() {
    const id = this.projectId(),
      version = ++this.storyRequestVersion;
    const result = await this.api('/projects/' + id + '/story-plans');
    if (id !== this.projectId() || version !== this.storyRequestVersion || this.destroyed) return;
    this.storyPlans.set(result.items);
    this.storyUsed.set(result.used);
    this.storyLimit.set(result.dailyLimit);
  }
  async createStoryPlan(options: StoryPlanOptions) {
    await this.perform(async () => {
      const version = await this.storeDocument();
      const result = await this.api('/projects/' + this.projectId() + '/story-plans', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...options, requestId: crypto.randomUUID(), version }),
      });
      await this.refreshStoryPlans();
      this.schedulePoll();
      this.notice.set(
        result.reused
          ? 'Đã dùng lại bản phân cảnh. Chọn xem/sửa để duyệt.'
          : 'Đã xếp hàng phân cảnh. Storyboard đang sửa chưa bị thay đổi.',
      );
    });
  }
  async importStoryPlan(event: Event) {
    const input = event.target as HTMLInputElement,
      file = input.files?.[0];
    if (!file) return;
    try {
      await this.perform(async () => {
        if (file.size > 262144) throw new Error('File phân cảnh tối đa 256 KiB.');
        const data = JSON.parse(await file.text());
        const proposal = data.proposal ?? data.plan?.proposal;
        if (data.format !== 'flowvideo.story-plan' || data.schemaVersion !== 1 || !proposal)
          throw new Error('File phân cảnh không đúng định dạng.');
        const result = await this.api('/projects/' + this.projectId() + '/story-plans/import', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            format: data.format,
            schemaVersion: data.schemaVersion,
            proposal,
          }),
        });
        await this.refreshStoryPlans();
        const original = await this.api(
          '/projects/' + this.projectId() + '/story-plans/' + result.plan.id,
        );
        this.architect?.show(result.plan, original.original);
        this.notice.set('Đã nhập bản phân cảnh để duyệt; chưa gọi AI hoặc thay storyboard.');
      });
    } finally {
      input.value = '';
    }
  }
  async openStoryPlan(id: string) {
    await this.perform(async () => {
      const data = await this.api('/projects/' + this.projectId() + '/story-plans/' + id);
      this.architect?.show(data.plan, data.original);
    });
  }
  async editStoryPlan(edit: StoryPlanEdit, apply: boolean) {
    await this.perform(async () => {
      const base = '/projects/' + this.projectId() + '/story-plans/' + edit.id;
      const result = await this.api(base, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ revision: edit.revision, proposal: edit.proposal }),
      });
      this.architect?.accepted(result.plan);
      await this.refreshStoryPlans();
      if (apply) {
        const version = await this.storeDocument();
        const applied = await this.api(base + '/apply', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ revision: result.plan.revision, version }),
        });
        this.applyDraft(applied.document);
        this.documentVersion.set(applied.version);
        this.boardSession.update((v) => v + 1);
        this.tab.set('board');
        this.notice.set(
          'Đã áp dụng bản phân cảnh đã duyệt. Chọn tư liệu hoặc dùng quy trình một nút để sản xuất.',
        );
      } else this.notice.set('Đã lưu bản sửa phân cảnh; bản AI gốc được giữ riêng.');
    });
  }
  async cancelStoryPlan(id: string) {
    await this.perform(async () => {
      await this.api('/projects/' + this.projectId() + '/story-plans/' + id + '/cancel', {
        method: 'POST',
      });
      await this.refreshStoryPlans();
      this.notice.set('Đã hủy phân cảnh trước khi gửi AI.');
    });
  }
  async refreshStoryHistory() {
    await this.perform(() => this.refreshStoryPlans());
  }
  async inspectQuality(id: string) {
    await this.perform(async () => {
      const report = await this.api('/jobs/' + id + '/qa', { method: 'POST' });
      this.qaReport.set(report);
      this.notice.set(
        report.result === 'Failed'
          ? 'Bản xuất có lỗi QA kỹ thuật.'
          : 'Đã kiểm tra thông số bản xuất.',
      );
    });
  }
  exportQuality() {
    const report = this.qaReport();
    if (report) this.downloadJson(report, 'flowvideo-qa-' + report.jobId + '.json');
  }
  async refreshProduction() {
    const id = this.projectId(),
      version = ++this.productionRequestVersion;
    const result = await this.api('/projects/' + id + '/workflows');
    if (id !== this.projectId() || version !== this.productionRequestVersion || this.destroyed)
      return;
    this.productionRuns.set(result.items);
    const signature = result.items.map((r: any) => r.id + r.updatedAt).join('|');
    if (signature !== this.productionSignature) {
      const assets = await this.api('/projects/' + id + '/assets');
      if (id !== this.projectId() || version !== this.productionRequestVersion || this.destroyed)
        return;
      this.assets.set(assets);
      this.productionSignature = signature;
    }
  }
  async production(options: ProductionOptions, start: boolean) {
    await this.perform(async () => {
      const version = await this.storeDocument();
      const result = await this.api(
        '/projects/' + this.projectId() + '/workflows/' + (start ? 'start' : 'plan'),
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ ...options, requestId: crypto.randomUUID(), version }),
        },
      );
      if (start) {
        this.productionEstimate.set(null);
        await this.refreshProduction();
        this.schedulePoll();
        this.notice.set(
          'Đã chụp storyboard và bắt đầu quy trình. Bạn có thể tiếp tục chỉnh bản nháp.',
        );
      } else {
        this.productionEstimate.set(result);
        this.notice.set('Đã kiểm tra số lượt và hạn mức. Chưa gửi yêu cầu AI.');
      }
    });
  }
  async productionAction(value: { id: string; action: string }) {
    await this.perform(async () => {
      await this.api(
        '/projects/' + this.projectId() + '/workflows/' + value.id + '/' + value.action,
        { method: 'POST' },
      );
      await Promise.all([this.refreshProduction(), this.refreshJobs()]);
      this.schedulePoll();
      this.notice.set(
        value.action === 'cancel'
          ? 'Đã hủy quy trình. AI đã gửi có thể vẫn hoàn tất và tính phí.'
          : 'Đã xếp hàng dựng lại, không gọi thêm AI.',
      );
    });
  }
  async applyProduction(id: string) {
    await this.perform(async () => {
      const data = await this.api(
        '/projects/' + this.projectId() + '/workflows/' + id + '/snapshot',
      );
      if (!data.result) throw new Error('Quy trình chưa có kết quả storyboard.');
      this.applyDraft(data.result);
      this.boardSession.update((v) => v + 1);
      this.productionEstimate.set(null);
      this.notice.set('Đã mở kết quả quy trình vào bản nháp. Bấm Lưu storyboard để giữ.');
    });
  }
  previewProduction(id: string) {
    this.preview.set('/api/jobs/' + id + '/download');
    this.tab.set('studio');
  }
  async refreshVideos() {
    const id = this.projectId(),
      version = ++this.videoRequestVersion;
    const result = await this.api('/projects/' + id + '/video-generations');
    if (id !== this.projectId() || version !== this.videoRequestVersion || this.destroyed) return;
    this.videoJobs.set(result.items);
    this.videoUsed.set(result.used);
    this.videoLimit.set(result.dailyLimit);
    for (const row of result.items as VideoRow[]) if (row.asset) this.addAsset(row.asset);
  }
  async generateVideos(options: VideoRequestOptions) {
    await this.perform(async () => {
      const version = await this.storeDocument();
      let count = 0;
      try {
        for (const sceneId of options.sceneIds) {
          await this.api('/projects/' + this.projectId() + '/video-generations', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              requestId: crypto.randomUUID(),
              sceneId,
              version,
              seconds: options.seconds,
              resolution: options.resolution,
              referenceId: options.referenceId,
            }),
          });
          count++;
        }
        this.notice.set(
          'Đã nhận ' +
            count +
            ' yêu cầu video. Kết quả đã có được dùng lại; chọn Dùng video cho cảnh khi sẵn sàng.',
        );
      } catch (e) {
        throw new Error(
          'Đã nhận ' +
            count +
            '/' +
            options.sceneIds.length +
            ' yêu cầu. ' +
            (e instanceof Error ? e.message : 'Không tạo được video.'),
        );
      } finally {
        await this.refreshVideos();
        this.schedulePoll();
      }
    });
  }
  async cancelVideo(id: string) {
    await this.perform(async () => {
      await this.api('/projects/' + this.projectId() + '/video-generations/' + id + '/cancel', {
        method: 'POST',
      });
      await this.refreshVideos();
      this.notice.set('Đã hủy video trước khi gửi tới dịch vụ.');
    });
  }
  applyVideo(row: VideoRow) {
    if (!row.asset) return;
    this.scenes.update((s) =>
      s.map((c) =>
        c.id === row.generation.sceneId ? { ...c, assetId: row.asset!.id, start: 0 } : c,
      ),
    );
    this.notice.set('Đã dùng video trong bản nháp. Bấm Lưu storyboard để giữ.');
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
          loop:
            this.mode === 'loop' ? { style: this.loopStyle, crossfade: this.loopCrossfade } : null,
          duration: this.duration,
          ratio: this.ratio,
          fit: this.fit,
          fadeSeconds: this.fadeSeconds,
          duckMusic: this.duckMusic,
          branding: this.branding(),
          transitions: this.mode === 'concat' ? this.transitions() : null,
          effects: this.effects(),
          captions: this.captionStyle(),
          audio: this.audioTuning(),
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
  private draft() {
    return {
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
      branding: this.boardBranding(),
      transitions: this.boardTransitions(),
      effects: this.boardEffects(),
      captions: this.boardSubtitleStyle(),
      audio: this.boardAudioTuning(),
      resolution: this.boardResolution(),
      voiceGain: this.boardVoiceGain(),
      musicGain: this.boardMusicGain(),
    };
  }
  private applyDraft(doc: any) {
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
    this.boardBranding.set({
      ...defaultBranding(),
      ...doc.branding,
      texts: doc.branding?.texts ?? [],
    });
    this.boardTransitions.set({ ...defaultTransitions(), ...doc.transitions });
    this.boardEffects.set({ ...defaultEffects(), ...doc.effects });
    this.boardSubtitleStyle.set({ ...defaultCaptionStyle(), ...doc.captions });
    this.boardAudioTuning.set({ ...defaultAudioTuning(), ...doc.audio });
    this.boardResolution.set(doc.resolution ?? 720);
    this.boardVoiceGain.set(doc.voiceGain ?? 1);
    this.boardMusicGain.set(doc.musicGain ?? 0.18);
  }
  private applySettings(doc: any) {
    const draft = this.draft();
    this.applyDraft({
      ...draft,
      ratio: doc.ratio,
      autoCaptions: doc.autoCaptions,
      resolution: doc.resolution,
      voiceGain: doc.voiceGain,
      musicGain: doc.musicGain,
      fit: doc.fit,
      fadeSeconds: doc.fadeSeconds,
      duckMusic: doc.duckMusic,
      branding: doc.branding,
      transitions: doc.transitions,
      effects: doc.effects,
      captions: doc.captions,
      audio: doc.audio,
    });
  }
  async refreshSavedItems() {
    const id = this.projectId();
    const items = await this.api('/projects/' + id + '/saved-items');
    if (id === this.projectId()) this.savedItems.set(items);
  }
  async captureItem(kind: string) {
    await this.perform(async () => {
      const version = await this.storeDocument();
      await this.api('/projects/' + this.projectId() + '/saved-items', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ kind, name: this.itemName, version }),
      });
      await this.refreshSavedItems();
      this.notice.set('Đã lưu ' + (kind === 'preset' ? 'preset' : 'mốc khôi phục') + '.');
    });
  }
  async useSavedItem(item: any, action: string) {
    await this.perform(async () => {
      const result = await this.api('/projects/' + this.projectId() + '/saved-items/' + item.id);
      if (action === 'compare') {
        this.comparison.set(
          'Mốc: ' + item.name + '\n' + compareDocuments(result.document, this.draft()),
        );
        return;
      }
      if (item.kind === 'preset') this.applySettings(result.document);
      else this.applyDraft(result.document);
      this.boardSession.update((v) => v + 1);
      this.comparison.set('');
      this.workflowReport.set('');
      this.notice.set(
        'Đã mở vào bản nháp. Bấm Lưu storyboard để giữ thay đổi; bản đã lưu chưa bị thay.',
      );
    });
  }
  async renameSavedItem(item: any) {
    await this.perform(async () => {
      await this.api('/projects/' + this.projectId() + '/saved-items/' + item.id, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: item.name }),
      });
      await this.refreshSavedItems();
      this.notice.set('Đã đổi tên mục đã lưu.');
    });
  }
  async deleteSavedItem(item: any) {
    if (!confirm('Xóa ' + item.name + '? Mục này sẽ mất; storyboard và media vẫn được giữ.'))
      return;
    await this.perform(async () => {
      await this.api('/projects/' + this.projectId() + '/saved-items/' + item.id, {
        method: 'DELETE',
      });
      await this.refreshSavedItems();
      this.notice.set('Đã xóa mục đã lưu.');
    });
  }
  async importPreset(event: Event) {
    const input = event.target as HTMLInputElement,
      file = input.files?.[0];
    if (!file) return;
    await this.perform(async () => {
      if (file.size > 65536) throw new Error('Preset tối đa 64 KiB.');
      const text = (await file.text()).replace(/^\uFEFF/, '');
      const data = JSON.parse(text);
      await this.api('/projects/' + this.projectId() + '/saved-items/import-preset', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(data),
      });
      await this.refreshSavedItems();
      this.notice.set('Đã nhập preset. Chọn Áp preset để dùng.');
    });
    input.value = '';
  }
  async projectSummary() {
    await this.perform(async () => {
      const summary = await this.api('/projects/' + this.projectId() + '/summary');
      this.workflowReport.set(
        `${summary.assetCount} tài nguyên · ${(summary.bytes / 1048576).toFixed(2)} MiB · ${summary.missingFiles} file thiếu\n${summary.scenes} cảnh · ${summary.duration.toFixed(1)} giây · bản lưu v${summary.version}\n${summary.checkpoints} mốc · ${summary.presets} preset\n` +
          summary.jobs.map((j: any) => this.status(j.status) + ': ' + j.count).join(' · '),
      );
    });
  }
  async preflight() {
    await this.perform(async () => {
      const version = await this.storeDocument();
      const result = await this.api('/projects/' + this.projectId() + '/storyboard/preflight', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ version }),
      });
      this.workflowReport.set(
        'Kiểm tra bản lưu v' +
          result.version +
          ' — chạy lại sau khi chỉnh.\n' +
          (result.ready ? 'Không có lỗi chặn dựng.' : 'Có lỗi cần sửa trước khi dựng.') +
          '\n' +
          result.issues
            .map(
              (i: any) =>
                (i.severity === 'error' ? 'Lỗi: ' : 'Lưu ý: ') +
                (i.sceneId ? '[' + i.sceneId.slice(0, 8) + '] ' : '') +
                i.message,
            )
            .join('\n'),
      );
    });
  }
  async renderVariants() {
    await this.perform(async () => {
      const version = await this.storeDocument();
      await this.api('/projects/' + this.projectId() + '/storyboard/render-variants', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ version }),
      });
      await this.refreshJobs();
      this.schedulePoll();
      this.tab.set('studio');
      this.notice.set('Đã xếp hàng hai bản 9:16 và 16:9, dùng cùng snapshot.');
    });
  }
  async repeatJob(id: string) {
    await this.perform(async () => {
      await this.api('/jobs/' + id + '/duplicate', { method: 'POST' });
      await this.refreshJobs();
      this.schedulePoll();
      this.notice.set('Đã tạo job mới từ snapshot hoàn tất, không thay job cũ.');
    });
  }
  async exportSceneSelection(scenes: SceneDraft[]) {
    await this.perform(async () => {
      if (!scenes.length) throw new Error('Chọn ít nhất một cảnh.');
      await this.storeDocument();
      this.downloadJson(
        {
          format: 'flowvideo.storyboard',
          schemaVersion: 1,
          name:
            (this.projects().find((p) => p.id === this.projectId())?.name || 'Storyboard').slice(
              0,
              100,
            ) + ' · cảnh chọn',
          document: { ...this.draft(), scenes },
        },
        'flowvideo-selected-scenes.json',
      );
      this.notice.set('Đã xuất các cảnh được chọn; JSON không kèm file media.');
    });
  }
  showSubtitleTimeline() {
    this.error.set('');
    try {
      this.subtitleRows.set(parseSrt(this.subtitleContent));
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : 'SRT không hợp lệ.');
    }
  }
  formatCue(ms: number) {
    return stamp(ms);
  }
  private async storeDocument() {
    if (!this.documentLoaded())
      throw new Error('Chưa tải xong dự án. Hãy tải lại trang trước khi lưu.');
    const result = await this.api('/projects/' + this.projectId() + '/document', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        version: this.documentVersion(),
        document: this.draft(),
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
    const preference = this.preference(asset.id);
    this.preferenceFavorite = preference.favorite;
    this.preferenceTags = preference.tags.join(', ');
    this.preferenceNote = preference.note;
    this.libraryReport.set('');
    this.metadata.set(null);
    this.clearDerived();
    this.frameAt = 0;
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
      if (action === 'range') {
        this.subtitleContent = subtitleRange(
          this.subtitleContent,
          this.subtitleRangeStart,
          this.subtitleRangeEnd,
        );
        this.notice.set('Đã lấy đoạn SRT và dịch mốc về 0; hãy lưu bản mới.');
        return;
      }
      if (action === 'csv') {
        this.downloadBlob(
          new Blob([subtitleCsv(this.subtitleContent)], { type: 'text/csv;charset=utf-8' }),
          'flowvideo-subtitles.csv',
        );
        return;
      }
      if (action === 'report') {
        this.subtitleReport.set(subtitleAnalysis(this.subtitleContent));
        return;
      }
      if (action === 'text') {
        this.downloadBlob(
          new Blob([subtitlePlainText(this.subtitleContent)], { type: 'text/plain;charset=utf-8' }),
          'flowvideo-subtitles.txt',
        );
        return;
      }
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
                : action === 'split'
                  ? this.subtitleSplit
                  : action === 'clamp'
                    ? this.subtitleClamp
                    : action === 'minimum'
                      ? this.subtitleMinimum
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
  clearDerived() {
    if (this.derivedUrl) URL.revokeObjectURL(this.derivedUrl);
    this.derivedUrl = '';
    this.derivedPreview.set('');
  }
  async mediaTool(action: string) {
    const asset = this.assetPreview();
    if (!asset) return;
    await this.perform(async () => {
      if (action === 'frame' || action === 'extract-audio') {
        const derived = await this.api('/assets/' + asset.id + '/' + action, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: action === 'frame' ? JSON.stringify({ at: this.frameAt }) : undefined,
        });
        this.addAsset(derived);
        this.notice.set('Đã thêm ' + derived.name + ' vào thư viện.');
      } else {
        const response = await fetch(
          '/api/assets/' +
            asset.id +
            '/' +
            action +
            (action === 'thumbnail' ? '?at=' + this.frameAt : ''),
          { signal: AbortSignal.any([this.controller.signal, AbortSignal.timeout(70000)]) },
        );
        if (!response.ok) {
          const result = await response.json().catch(() => ({}));
          throw new Error(result.error || result.detail || 'Không tạo được ảnh xem trước.');
        }
        const blob = await response.blob();
        if (this.assetPreview()?.id !== asset.id) return;
        this.clearDerived();
        this.derivedUrl = URL.createObjectURL(blob);
        this.derivedPreview.set(this.derivedUrl);
      }
    });
  }
  preference(id: string): any {
    return (
      this.assetPreferences().find((p) => p.assetId === id) || {
        assetId: id,
        favorite: false,
        tags: [],
        note: '',
      }
    );
  }
  async refreshPreferences() {
    const id = this.projectId();
    const rows = await this.api('/projects/' + id + '/asset-preferences');
    if (id === this.projectId()) this.assetPreferences.set(rows);
  }
  private async putPreference(id: string, preference: any) {
    const saved = await this.api('/projects/' + this.projectId() + '/asset-preferences/' + id, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(preference),
    });
    this.assetPreferences.update((rows) => [...rows.filter((p) => p.assetId !== id), saved]);
  }
  async favoriteAsset(id: string) {
    await this.perform(async () => {
      const p = this.preference(id);
      await this.putPreference(id, { ...p, favorite: !p.favorite });
    });
  }
  async savePreference() {
    const asset = this.assetPreview();
    if (!asset) return;
    await this.perform(async () => {
      await this.putPreference(asset.id, {
        favorite: this.preferenceFavorite,
        tags: this.preferenceTags
          .split(',')
          .map((t) => t.trim())
          .filter(Boolean),
        note: this.preferenceNote,
      });
      this.notice.set('Đã lưu yêu thích, nhãn và ghi chú tư liệu.');
    });
  }
  async assetUsage() {
    const asset = this.assetPreview();
    if (!asset) return;
    await this.perform(async () => {
      const usage = await this.api(
        '/projects/' + this.projectId() + '/assets/' + asset.id + '/usage',
      );
      this.libraryReport.set(
        'Bản đã lưu: ' +
          usage.scenes.length +
          ' cảnh · audio: ' +
          (usage.audio ? 'có' : 'không') +
          '\n' +
          usage.scenes.map((s: any) => s.title + (s.enabled ? '' : ' (tắt)')).join(', ') +
          '\n' +
          usage.checkpoints.length +
          ' mốc khôi phục · ' +
          usage.jobCount +
          ' job tham chiếu',
      );
    });
  }
  async copyAssets() {
    await this.perform(async () => {
      const result = await this.api('/projects/' + this.projectId() + '/assets/copy', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          assetIds: this.librarySelected(),
          targetProjectId: this.copyTarget,
        }),
      });
      this.notice.set(
        'Đã sao chép ' + result.assets.length + ' file độc lập sang dự án đích, kèm nhãn/ghi chú.',
      );
    });
  }
  async findDuplicates() {
    await this.perform(async () => {
      const result = await this.api('/projects/' + this.projectId() + '/assets/duplicates', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ assetIds: this.librarySelected() }),
      });
      this.libraryReport.set(
        'Đã kiểm tra ' +
          result.checkedCount +
          ' file: ' +
          result.groups.length +
          ' nhóm trùng byte.\n' +
          result.groups.map((g: any) => g.assets.map((a: any) => a.name).join(' = ')).join('\n'),
      );
    });
  }
  async bulkRenameAssets() {
    await this.perform(async () => {
      const updated = await this.api('/projects/' + this.projectId() + '/assets/bulk-name', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          assetIds: this.librarySelected(),
          prefix: this.bulkPrefix,
          suffix: this.bulkSuffix,
          startNumber: this.bulkStart,
        }),
      });
      this.assets.update((rows) => rows.map((a) => updated.find((b: Asset) => b.id === a.id) || a));
      this.notice.set('Đã đổi tên ' + updated.length + ' file theo thứ tự chọn, giữ phần mở rộng.');
    });
  }
  addSelectedScenes() {
    const media = this.librarySelected()
      .map((id) => this.assets().find((a) => a.id === id))
      .filter((a): a is Asset => !!a && (a.kind === 'video' || a.kind === 'image'));
    if (
      !media.length ||
      this.scenes().length + media.length > 30 ||
      this.scenes().reduce((sum, s) => sum + s.duration, 0) + media.length * 5 > 600
    ) {
      this.error.set('Cần chọn ảnh/video; tối đa 30 cảnh và tổng 600 giây.');
      return;
    }
    this.scenes.update((s) => [
      ...s,
      ...media.map((a, i) => ({
        id: crypto.randomUUID(),
        title: 'Cảnh ' + (s.length + i + 1),
        narration: '',
        prompt: '',
        assetId: a.id,
        duration: 5,
        start: 0,
        options: defaultSceneOptions(),
      })),
    ]);
    this.tab.set('board');
    this.notice.set('Đã thêm ' + media.length + ' cảnh từ tư liệu chọn. Bấm Lưu để giữ.');
  }
  async mergeSubtitleFile(event: Event) {
    const input = event.target as HTMLInputElement,
      file = input.files?.[0];
    if (!file) return;
    await this.perform(async () => {
      if (file.size > 102400) throw new Error('File SRT tối đa 100 KiB.');
      this.subtitleContent = mergeSrt(
        this.subtitleContent,
        await file.text(),
        this.subtitleMergeMode === 'append',
        this.subtitleMergeGap,
      );
      this.notice.set('Đã ghép vào bản nháp SRT. Kiểm tra timeline rồi lưu bản mới.');
    });
    input.value = '';
  }
  async inspectDelivery() {
    if (!this.deliveryJob) return;
    await this.perform(async () => {
      const info = await this.api('/jobs/' + this.deliveryJob + '/media-info');
      this.deliveryInfo.set(
        `${info.duration.toFixed(3)} giây · ${(info.bytes / 1048576).toFixed(2)} MiB\n${info.width}×${info.height} · ${info.frameRate} fps · ${info.videoCodec}\nAudio: ${info.audioCodec || 'không có'} · ${info.channels || 0} kênh · ${info.sampleRate || 0} Hz`,
      );
    });
  }
  async downloadDelivery(kind: string) {
    if (!this.deliveryJob) return;
    await this.perform(async () => {
      const response = await fetch(
        '/api/jobs/' +
          this.deliveryJob +
          '/deliver/' +
          kind +
          '?at=' +
          this.deliveryAt +
          '&duration=' +
          this.deliveryDuration,
        { signal: AbortSignal.any([this.controller.signal, AbortSignal.timeout(90000)]) },
      );
      if (!response.ok) {
        const result = await response.json().catch(() => ({}));
        throw new Error(result.error || result.detail || 'Không xuất được file.');
      }
      const extension =
        kind === 'mute'
          ? 'mp4'
          : kind === 'bundle'
            ? 'zip'
            : ['poster', 'contact'].includes(kind)
              ? 'png'
              : kind;
      this.downloadBlob(await response.blob(), 'flowvideo-' + kind + '.' + extension);
      this.notice.set('Đã xuất ' + kind + '.');
    });
  }
  async generateScript() {
    await this.perform(async () => {
      const result = await this.api('/ai/script', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ idea: this.idea, projectId: this.projectId() }),
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
