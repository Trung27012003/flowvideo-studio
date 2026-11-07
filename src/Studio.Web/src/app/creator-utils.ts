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
export function parseSrt(content: string): Cue[] {
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
    if (start < previous || end <= start || !text || text.length > 2000)
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
  let cues = parseSrt(content);
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
