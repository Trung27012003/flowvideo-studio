export interface Cue {
  start: number;
  end: number;
  text: string;
}
export function scaleDurations(values: number[], target: number): number[] {
  const units = Math.round(target * 10);
  if (
    !values.length ||
    !Number.isFinite(target) ||
    target > 600 ||
    units < values.length ||
    values.some((v) => !Number.isFinite(v) || v <= 0)
  )
    throw new Error('Tổng phải đủ 0,1 giây/cảnh và không quá 600 giây.');
  const sum = values.reduce((a, b) => a + b, 0),
    remaining = units - values.length;
  const raw = values.map((v) => (v / sum) * remaining),
    result = raw.map((v) => 1 + Math.floor(v));
  const order = raw
    .map((v, i) => ({ i, fraction: v - Math.floor(v) }))
    .sort((a, b) => b.fraction - a.fraction || a.i - b.i);
  let extra = units - result.reduce((a, b) => a + b, 0);
  for (let i = 0; i < extra; i++) result[order[i].i]++;
  return result.map((v) => v / 10);
}
function time(value: string): number {
  const match = /^(\d{2}):([0-5]\d):([0-5]\d),(\d{3})$/.exec(value);
  if (!match) throw new Error('Mốc SRT không hợp lệ.');
  return ((+match[1] * 60 + +match[2]) * 60 + +match[3]) * 1000 + +match[4];
}
export function stamp(ms: number, vtt = false): string {
  ms = Math.round(ms);
  if (!Number.isFinite(ms) || ms < 0 || ms >= 360000000)
    throw new Error('Mốc thời gian vượt giới hạn.');
  return (
    String(Math.floor(ms / 3600000)).padStart(2, '0') +
    ':' +
    String(Math.floor(ms / 60000) % 60).padStart(2, '0') +
    ':' +
    String(Math.floor(ms / 1000) % 60).padStart(2, '0') +
    (vtt ? '.' : ',') +
    String(ms % 1000).padStart(3, '0')
  );
}
export function parseSrt(content: string, ordered = true): Cue[] {
  if (content.length > 100000) throw new Error('SRT tối đa 100.000 ký tự.');
  const blocks = content
    .replace(/^\uFEFF/, '')
    .replace(/\r\n?/g, '\n')
    .trim()
    .split(/\n\s*\n/);
  if (!content.trim() || blocks.length > 5000) throw new Error('SRT trống hoặc quá nhiều câu.');
  let previous = -1;
  return blocks.map((block) => {
    const lines = block.split('\n');
    if (!/^\d+$/.test(lines.shift() || '')) throw new Error('Thiếu số thứ tự SRT.');
    const match = /^(\d{2}:\d{2}:\d{2},\d{3}) --> (\d{2}:\d{2}:\d{2},\d{3})$/.exec(
      lines.shift() || '',
    );
    if (!match) throw new Error('Dòng thời gian SRT không hợp lệ.');
    const start = time(match[1]),
      end = time(match[2]),
      text = lines.join('\n').trim();
    if ((ordered && start < previous) || end <= start || !text || text.length > 2000)
      throw new Error('Kiểm tra thứ tự, thời lượng và nội dung SRT.');
    previous = start;
    return { start, end, text };
  });
}
export function writeSrt(cues: Cue[]): string {
  const result =
    cues.map((c, i) => `${i + 1}\n${stamp(c.start)} --> ${stamp(c.end)}\n${c.text}`).join('\n\n') +
    '\n';
  parseSrt(result);
  return result;
}
export function transformSrt(
  content: string,
  action: string,
  amount: number,
  find = '',
  replacement = '',
): string {
  let cues = parseSrt(content, action !== 'sort');
  if (action === 'offset') {
    if (!Number.isFinite(amount) || Math.abs(amount) > 3600)
      throw new Error('Độ lệch từ -3.600 đến 3.600 giây.');
    cues = cues.map((c) => ({ ...c, start: c.start + amount * 1000, end: c.end + amount * 1000 }));
  } else if (action === 'speed') {
    if (!Number.isFinite(amount) || amount < 0.25 || amount > 4)
      throw new Error('Tốc độ từ 0,25 đến 4×.');
    cues = cues.map((c) => ({ ...c, start: c.start / amount, end: c.end / amount }));
  } else if (action === 'replace') {
    if (!find) throw new Error('Nhập nội dung cần tìm.');
    cues = cues.map((c) => ({ ...c, text: c.text.split(find).join(replacement) }));
  } else if (action === 'wrap') {
    if (!Number.isInteger(amount) || amount < 20 || amount > 80)
      throw new Error('Độ dài dòng từ 20 đến 80 ký tự.');
    cues = cues.map((c) => {
      const lines: string[] = [];
      let line = '';
      for (const word of c.text.split(/\s+/)) {
        const chars = Array.from(word);
        while (chars.length > amount) {
          if (line) {
            lines.push(line);
            line = '';
          }
          lines.push(chars.splice(0, amount).join(''));
        }
        const part = chars.join('');
        if (!part) continue;
        if (Array.from(line + (line ? ' ' : '') + part).length > amount) {
          lines.push(line);
          line = part;
        } else line += (line ? ' ' : '') + part;
      }
      if (line) lines.push(line);
      return { ...c, text: lines.join('\n') };
    });
  } else if (action === 'merge') {
    if (!Number.isFinite(amount) || amount < 0 || amount > 5)
      throw new Error('Khoảng nghỉ từ 0 đến 5 giây.');
    const merged: Cue[] = [];
    for (const c of cues) {
      const last = merged.at(-1),
        joined = last ? last.text + '\n' + c.text : '';
      if (
        last &&
        c.start >= last.end &&
        c.start - last.end <= amount * 1000 &&
        c.end - last.start <= 10000 &&
        joined.length <= 2000
      ) {
        last.end = c.end;
        last.text = joined;
      } else merged.push({ ...c });
    }
    cues = merged;
  } else if (action === 'strip') {
    cues = cues.map((c) => ({ ...c, text: stripFormatting(c.text) }));
  } else if (action === 'dedupe') {
    const result: Cue[] = [];
    for (const cue of cues) {
      const previous = result.at(-1);
      if (
        previous &&
        previous.text.replace(/\s+/g, ' ').trim() === cue.text.replace(/\s+/g, ' ').trim() &&
        cue.start <= previous.end + 250 &&
        Math.max(cue.end, previous.end) - previous.start <= 10000
      )
        previous.end = Math.max(previous.end, cue.end);
      else result.push({ ...cue });
    }
    cues = result;
  } else if (action === 'clamp') {
    if (!Number.isFinite(amount) || amount < 0.1 || amount > 600)
      throw new Error('Giới hạn từ 0,1 đến 600 giây.');
    cues = cues
      .filter((c) => c.start < amount * 1000)
      .map((c) => ({ ...c, end: Math.min(c.end, Math.round(amount * 1000)) }));
    if (!cues.length)
      throw new Error('Không còn câu nào trong khoảng đã chọn. Bản nháp được giữ nguyên.');
  } else if (action === 'split') {
    if (!Number.isInteger(amount) || amount < 20 || amount > 200)
      throw new Error('Tách theo 20–200 ký tự mỗi câu.');
    cues = cues.flatMap((c) => {
      const parts: string[] = [];
      let part = '';
      const plain = stripFormatting(c.text);
      if (!plain) throw new Error('Câu không còn lời sau khi bỏ định dạng.');
      for (const word of plain.split(/\s+/)) {
        const chars = Array.from(word);
        while (chars.length > amount) {
          if (part) {
            parts.push(part);
            part = '';
          }
          parts.push(chars.splice(0, amount).join(''));
        }
        const rest = chars.join('');
        if (!rest) continue;
        if (Array.from(part + (part ? ' ' : '') + rest).length > amount) {
          parts.push(part);
          part = rest;
        } else part += (part ? ' ' : '') + rest;
      }
      if (part) parts.push(part);
      if (c.end - c.start < parts.length) throw new Error('Câu quá ngắn để chia mốc thời gian.');
      return parts.map((text, i) => ({
        start: c.start + Math.round(((c.end - c.start) * i) / parts.length),
        end: c.start + Math.round(((c.end - c.start) * (i + 1)) / parts.length),
        text,
      }));
    });
  } else if (action === 'sort') {
    cues = cues
      .map((cue, index) => ({ ...cue, index }))
      .sort((a, b) => a.start - b.start || a.index - b.index)
      .map(({ index, ...cue }) => cue);
  } else if (action === 'overlaps') {
    let previousEnd = 0;
    cues = cues.map((c) => {
      const start = Math.max(c.start, previousEnd),
        end = Math.max(c.end, start + 1);
      previousEnd = end;
      return { ...c, start, end };
    });
  } else if (action === 'minimum') {
    if (!Number.isFinite(amount) || amount < 0.1 || amount > 10)
      throw new Error('Thời lượng tối thiểu từ 0,1–10 giây.');
    let previousEnd = 0;
    cues = cues.map((c) => {
      const start = Math.max(c.start, previousEnd),
        end = Math.max(c.end, start + Math.round(amount * 1000));
      previousEnd = end;
      return { ...c, start, end };
    });
  } else if (action === 'whitespace') {
    cues = cues.map((c) => ({
      ...c,
      text: c.text
        .normalize('NFC')
        .split('\n')
        .map((line) => line.trim().replace(/[ \t]+/g, ' '))
        .filter(Boolean)
        .join('\n'),
    }));
  } else if (action === 'upper' || action === 'lower') {
    cues = cues.map((c) => ({
      ...c,
      text: c.text
        .split(/(<[^>]*>|\{\\[^}]*\})/g)
        .map((token) =>
          /^(<[^>]*>|\{\\[^}]*\})$/.test(token)
            ? token
            : action === 'upper'
              ? token.toLocaleUpperCase('vi')
              : token.toLocaleLowerCase('vi'),
        )
        .join(''),
    }));
  } else if (action === 'sentence') {
    cues = cues.map((c) => ({ ...c, text: sentenceCase(c.text) }));
  } else if (action === 'punctuation') {
    cues = cues.map((c) => ({
      ...c,
      text: c.text
        .split(/(https?:\/\/\S+|www\.\S+|<[^>]*>|\{\\[^}]*\})/g)
        .map((part) =>
          /^(https?:\/\/|www\.|<|\{\\)/.test(part)
            ? part
            : part.replace(/[ \t]+([,.;:!?])/g, '$1').replace(/([,;:!?])(?=\p{L})/gu, '$1 '),
        )
        .join(''),
    }));
  } else if (action === 'emoji') {
    cues = cues.map((c) => ({
      ...c,
      text: c.text
        .replace(/([0-9#*])\uFE0F?\u20E3/gu, '$1')
        .replace(
          /\p{Regional_Indicator}{2}|\p{Extended_Pictographic}(?:\uFE0F|\p{Emoji_Modifier})*(?:\u200D\p{Extended_Pictographic}(?:\uFE0F|\p{Emoji_Modifier})*)*/gu,
          '',
        )
        .replace(/\p{Emoji_Modifier}/gu, '')
        .replace(/[ \t]{2,}/g, ' ')
        .trim(),
    }));
  } else throw new Error('Thao tác không hỗ trợ.');
  return writeSrt(cues);
}
export function webVtt(content: string): string {
  return (
    'WEBVTT\n\n' +
    parseSrt(content)
      .map(
        (c, i) =>
          `${i + 1}\n${stamp(c.start, true)} --> ${stamp(c.end, true)}\n${c.text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')}`,
      )
      .join('\n\n') +
    '\n'
  );
}

function stripFormatting(text: string): string {
  return text
    .replace(/<[^>]*>/g, '')
    .replace(/\{\\[^}]*\}/g, '')
    .trim();
}
export function subtitlePlainText(content: string): string {
  return (
    parseSrt(content)
      .map((c) => stripFormatting(c.text))
      .join('\n\n') + '\n'
  );
}
export function subtitleAnalysis(content: string): string {
  const cues = parseSrt(content);
  let maxEnd = 0,
    overlaps = 0;
  const fast: number[] = [];
  for (const [i, c] of cues.entries()) {
    if (c.start < maxEnd) overlaps++;
    maxEnd = Math.max(maxEnd, c.end);
    if (
      Array.from(stripFormatting(c.text).replace(/\s/g, '')).length / ((c.end - c.start) / 1000) >
      20
    )
      fast.push(i + 1);
  }
  return `${cues.length} câu · kết thúc ${(maxEnd / 1000).toFixed(3)} giây\n${overlaps} câu chồng lấn · ${fast.length} câu vượt 20 ký tự/giây\n${fast.length ? 'Cần xem lại câu: ' + fast.slice(0, 20).join(', ') + (fast.length > 20 ? '…' : '') : 'Không phát hiện câu đọc nhanh theo ngưỡng 20 ký tự/giây.'}\nƯớc lượng theo ký tự không tính khoảng trắng; không đo tốc độ giọng thực tế.`;
}

export function readingDurations(texts: string[], wpm: number): number[] {
  if (!Number.isFinite(wpm) || wpm < 60 || wpm > 300)
    throw new Error('Tốc độ đọc từ 60–300 từ/phút.');
  const result = texts.map(
    (text) =>
      Math.max(1, Math.round((text.trim().split(/\s+/u).filter(Boolean).length / wpm) * 600)) / 10,
  );
  if (result.reduce((a, b) => a + b, 0) > 600)
    throw new Error('Thời lượng ước tính vượt 600 giây.');
  return result;
}
export function shuffled<T>(items: T[], random: () => number = Math.random): T[] {
  const result = [...items];
  for (let i = result.length - 1; i > 0; i--) {
    const value = random();
    if (!Number.isFinite(value) || value < 0 || value >= 1)
      throw new Error('Nguồn ngẫu nhiên không hợp lệ.');
    const j = Math.floor(value * (i + 1));
    if (j < 0 || j > i) throw new Error('Nguồn ngẫu nhiên không hợp lệ.');
    [result[i], result[j]] = [result[j], result[i]];
  }
  return result;
}
export function compareDocuments(saved: any, draft: any): string {
  const oldScenes = new Map<string, any>((saved.scenes || []).map((s: any) => [s.id, s]));
  const currentScenes = new Map<string, any>((draft.scenes || []).map((s: any) => [s.id, s]));
  const added = [...currentScenes.keys()].filter((id) => !oldScenes.has(id)).length,
    removed = [...oldScenes.keys()].filter((id) => !currentScenes.has(id)).length;
  const changed = [...currentScenes.keys()].filter(
    (id) =>
      oldScenes.has(id) &&
      JSON.stringify(oldScenes.get(id)) !== JSON.stringify(currentScenes.get(id)),
  ).length;
  const order = JSON.stringify([...oldScenes.keys()]) !== JSON.stringify([...currentScenes.keys()]);
  const settings = [
    'idea',
    'script',
    'ratio',
    'voiceId',
    'musicId',
    'autoCaptions',
    'resolution',
    'voiceGain',
    'musicGain',
    'fit',
    'fadeSeconds',
    'duckMusic',
    'effects',
    'captions',
    'audio',
    'branding',
    'transitions',
  ].filter((key) => JSON.stringify(saved[key] ?? null) !== JSON.stringify(draft[key] ?? null));
  return `So với mốc: thêm ${added} cảnh, bỏ ${removed} cảnh, sửa ${changed} cảnh.\nThứ tự/danh sách: ${order ? 'khác' : 'giữ nguyên'}.\nTrường khác: ${settings.length ? settings.join(', ') : 'không có'}.`;
}

function sentenceCase(text: string): string {
  let upper = true;
  return text
    .split(/(<[^>]*>|\{\\[^}]*\})/g)
    .map((part) => {
      if (/^(<[^>]*>|\{\\[^}]*\})$/.test(part)) return part;
      return Array.from(part)
        .map((char) => {
          if (/[.!?…\n]/.test(char)) {
            upper = true;
            return char;
          }
          if (/\p{L}/u.test(char)) {
            const next = upper ? char.toLocaleUpperCase('vi') : char.toLocaleLowerCase('vi');
            upper = false;
            return next;
          }
          return char;
        })
        .join('');
    })
    .join('');
}
export function subtitleRange(content: string, start: number, end: number): string {
  if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end <= start || end > 600)
    throw new Error('Đoạn SRT cần đầu >=0, cuối lớn hơn đầu và không quá 600 giây.');
  const from = Math.round(start * 1000),
    to = Math.round(end * 1000);
  const cues = parseSrt(content)
    .filter((c) => c.end > from && c.start < to)
    .map((c) => ({ ...c, start: Math.max(c.start, from) - from, end: Math.min(c.end, to) - from }));
  if (!cues.length) throw new Error('Không có câu trong đoạn đã chọn.');
  return writeSrt(cues);
}
export function mergeSrt(content: string, incoming: string, append: boolean, gap: number): string {
  if (!Number.isFinite(gap) || gap < 0 || gap > 10)
    throw new Error('Khoảng nghỉ nối từ 0–10 giây.');
  const current = content.trim() ? parseSrt(content) : [],
    next = parseSrt(incoming);
  const offset =
    append && current.length
      ? Math.max(...current.map((c) => c.end)) + Math.round(gap * 1000) - next[0].start
      : 0;
  return writeSrt(
    [...current, ...next.map((c) => ({ ...c, start: c.start + offset, end: c.end + offset }))].sort(
      (a, b) => a.start - b.start,
    ),
  );
}
export function subtitleCsv(content: string): string {
  const cell = (value: string) => {
    if (/^[\s]*[=+@-]/.test(value)) value = "'" + value;
    return '"' + value.replace(/"/g, '""') + '"';
  };
  return (
    '\uFEFFindex,start_ms,end_ms,duration_ms,text\r\n' +
    parseSrt(content)
      .map((c, i) =>
        [String(i + 1), String(c.start), String(c.end), String(c.end - c.start), c.text]
          .map(cell)
          .join(','),
      )
      .join('\r\n') +
    '\r\n'
  );
}
