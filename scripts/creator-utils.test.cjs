const { test } = require("node:test");
const assert = require("node:assert/strict");
const {
  scaleDurations,
  parseSrt,
  writeSrt,
  transformSrt,
  webVtt,
} = require("../src/Studio.Web/src/app/creator-utils.ts");
const source =
  "1\n00:00:01,000 --> 00:00:02,000\nXin chào\n\n2\n00:00:02,100 --> 00:00:03,000\nFlowVideo\n";
test("duration scaling preserves tenths, target and minimum for skewed and equal scenes", () => {
  for (const input of [[5, 10, 15], [0.1, 500, 0.1], Array(30).fill(5)]) {
    const result = scaleDurations(input, 31.7);
    assert.equal(Math.round(result.reduce((a, b) => a + b, 0) * 10), 317);
    assert(result.every((v) => v >= 0.1));
  }
  assert.deepEqual(scaleDurations([1, 1, 1], 0.3), [0.1, 0.1, 0.1]);
  for (const n of [0, 0.2, 601, NaN])
    assert.throws(() => scaleDurations([1, 1, 1], n));
});
test("offset and speed maintain millisecond precision and reject invalid outcomes", () => {
  const shifted = parseSrt(transformSrt(source, "offset", 0.25));
  assert.equal(shifted[0].start, 1250);
  const sped = parseSrt(transformSrt(source, "speed", 2));
  assert.equal(sped[1].end, 1500);
  assert.throws(() => transformSrt(source, "offset", -2));
  assert.throws(() => transformSrt(source, "speed", 0));
});
test("literal replacement preserves special symbols and rejects empty cues", () => {
  const input = writeSrt([{ start: 0, end: 1000, text: "$a $a" }]);
  assert.equal(
    parseSrt(transformSrt(input, "replace", 0, "$a", "$&"))[0].text,
    "$& $&",
  );
  assert.throws(() => transformSrt(input, "replace", 0, "$a $a", ""));
});
test("Unicode wrap respects requested length without losing words", () => {
  const text = "Xin chào " + "ắ".repeat(65) + " và bạn bè của FlowVideo";
  const out = parseSrt(
    transformSrt(writeSrt([{ start: 0, end: 1000, text }]), "wrap", 20),
  )[0].text;
  assert(out.split("\n").every((l) => Array.from(l).length <= 20));
  assert.equal(out.replace(/\s/g, ""), text.replace(/\s/g, ""));
});
test("merge obeys gap, duration limits and never combines overlapping cues", () => {
  assert.equal(parseSrt(transformSrt(source, "merge", 0.2)).length, 1);
  assert.equal(parseSrt(transformSrt(source, "merge", 0.05)).length, 2);
  const input = writeSrt([
    { start: 0, end: 6000, text: "A" },
    { start: 5000, end: 7000, text: "B" },
    { start: 7200, end: 18000, text: "C" },
  ]);
  assert.equal(parseSrt(transformSrt(input, "merge", 1)).length, 3);
});
test("WebVTT has correct header/timestamps and escapes cue markup", () => {
  const result = webVtt(
    writeSrt([{ start: 1234, end: 2000, text: "<b>A & B</b>" }]),
  );
  assert(result.startsWith("WEBVTT\n\n1\n00:00:01.234 --> 00:00:02.000"));
  assert(result.includes("&lt;b&gt;A &amp; B&lt;/b&gt;"));
});
test("parser handles BOM/CRLF and rejects unordered, malformed or excessive text", () => {
  assert.equal(parseSrt("\uFEFF" + source.replace(/\n/g, "\r\n")).length, 2);
  for (const input of [
    "",
    source.replace("00:00:02,100", "00:00:00,100"),
    source.replace("00:00:02,000", "00:00:01,000"),
    "x".repeat(100001),
  ])
    assert.throws(() => parseSrt(input));
});

test("format cleanup strips HTML/ASS while preserving Vietnamese and rejects an empty cue", () => {
  const input = writeSrt([
    { start: 0, end: 1000, text: "{\\an8}<b>Xin chào</b> <i>bạn</i>" },
  ]);
  assert.equal(
    parseSrt(transformSrt(input, "strip", 0))[0].text,
    "Xin chào bạn",
  );
  assert.throws(() =>
    transformSrt(
      writeSrt([{ start: 0, end: 1000, text: "<b></b>" }]),
      "strip",
      0,
    ),
  );
});
test("duplicate cue merge handles overlap, gap threshold and duration limit", () => {
  const input = writeSrt([
    { start: 0, end: 1000, text: "Xin chào" },
    { start: 900, end: 2000, text: "Xin  chào" },
    { start: 3000, end: 4000, text: "Xin chào" },
    { start: 4000, end: 15000, text: "Xin chào" },
  ]);
  const result = parseSrt(transformSrt(input, "dedupe", 0));
  assert.equal(result.length, 3);
  assert.equal(result[0].end, 2000);
});
test("long cue splitting retains all characters and partitions its original time", () => {
  const text = "Xin chào bạn từ FlowVideo " + "ắ".repeat(45);
  const result = parseSrt(
    transformSrt(writeSrt([{ start: 1000, end: 5000, text }]), "split", 20),
  );
  assert(result.length > 1);
  assert.equal(result[0].start, 1000);
  assert.equal(result.at(-1).end, 5000);
  assert.equal(
    result
      .map((c) => c.text)
      .join("")
      .replace(/\s/g, ""),
    text.replace(/\s/g, ""),
  );
  assert(
    result.every(
      (c, i) =>
        Array.from(c.text).length <= 20 &&
        (i === 0 || result[i - 1].end === c.start),
    ),
  );
  assert.throws(() =>
    transformSrt(writeSrt([{ start: 0, end: 1, text }]), "split", 20),
  );
  const formatted = parseSrt(
    transformSrt(
      writeSrt([{ start: 0, end: 5000, text: "<b>" + text + "</b>" }]),
      "split",
      20,
    ),
  );
  assert(formatted.every((c) => !/[<>]/.test(c.text)));
  assert.equal(
    formatted
      .map((c) => c.text)
      .join("")
      .replace(/\s/g, ""),
    text.replace(/\s/g, ""),
  );
});
test("clamp trims the last cue and drops later cues without changing source on invalid results", () => {
  const result = parseSrt(transformSrt(source, "clamp", 1.5));
  assert.equal(result.length, 1);
  assert.equal(result[0].end, 1500);
  assert.throws(() => transformSrt(source, "clamp", 0.5));
});
test("reading report spots rapid and overlapping cues; text export omits formatting", () => {
  const {
    subtitleAnalysis,
    subtitlePlainText,
  } = require("../src/Studio.Web/src/app/creator-utils.ts");
  const input = writeSrt([
    { start: 0, end: 100, text: "<b>Xin chào mọi người</b>" },
    { start: 50, end: 1000, text: "FlowVideo" },
  ]);
  const report = subtitleAnalysis(input);
  assert(report.includes("1 câu chồng lấn"));
  assert(report.includes("1 câu vượt 20"));
  assert.equal(subtitlePlainText(input), "Xin chào mọi người\n\nFlowVideo\n");
});

test("sort repairs unordered SRT while preserving ties and every cue", () => {
  const input =
    "2\n00:00:03,000 --> 00:00:04,000\nB\n\n1\n00:00:01,000 --> 00:00:02,000\nA\n\n3\n00:00:01,000 --> 00:00:02,500\nC";
  assert.throws(() => parseSrt(input));
  const result = parseSrt(transformSrt(input, "sort", 0));
  assert.deepEqual(
    result.map((c) => c.text),
    ["A", "C", "B"],
  );
  assert.throws(() =>
    transformSrt(input.replace("00:00:04,000", "00:00:02,000"), "sort", 0),
  );
});
test("overlap repair and minimum duration retain all words and avoid zero-length cues", () => {
  const input = writeSrt([
    { start: 0, end: 2000, text: "A" },
    { start: 0, end: 1000, text: "B" },
    { start: 500, end: 2500, text: "C" },
  ]);
  for (const [action, amount] of [
    ["overlaps", 0],
    ["minimum", 1.5],
  ]) {
    const result = parseSrt(transformSrt(input, action, amount));
    assert.deepEqual(
      result.map((c) => c.text),
      ["A", "B", "C"],
    );
    assert(
      result.every(
        (c, i) => c.end > c.start && (i === 0 || c.start >= result[i - 1].end),
      ),
    );
    if (action === "minimum")
      assert(result.every((c) => c.end - c.start >= 1500));
  }
  assert.throws(() => transformSrt(input, "minimum", 11));
});
test("whitespace and case normalize Vietnamese without altering formatting tags", () => {
  const input = writeSrt([
    { start: 0, end: 1000, text: "  <b>xin   chào</b>  \n  bạn  " },
  ]);
  const clean = transformSrt(input, "whitespace", 0);
  assert.equal(parseSrt(clean)[0].text, "<b>xin chào</b>\nbạn");
  const upper = transformSrt(clean, "upper", 0);
  assert.equal(parseSrt(upper)[0].text, "<b>XIN CHÀO</b>\nBẠN");
  assert.equal(
    parseSrt(transformSrt(upper, "lower", 0))[0].text,
    "<b>xin chào</b>\nbạn",
  );
});
test("reading timing enforces bounds and shuffle is immutable with deterministic randomness", () => {
  const {
    readingDurations,
    shuffled,
  } = require("../src/Studio.Web/src/app/creator-utils.ts");
  assert.deepEqual(readingDurations(["xin chào", ""], 120), [1, 0.1]);
  assert.throws(() => readingDurations(["text"], 10));
  assert.throws(() => readingDurations([Array(1000).fill("từ").join(" ")], 60));
  const input = [1, 2, 3, 4];
  assert.deepEqual(
    shuffled(input, () => 0),
    [2, 3, 4, 1],
  );
  assert.deepEqual(input, [1, 2, 3, 4]);
  assert.throws(() => shuffled(input, () => NaN));
});
test("checkpoint comparison reports changed, removed, added scenes and settings", () => {
  const {
    compareDocuments,
  } = require("../src/Studio.Web/src/app/creator-utils.ts");
  assert.match(
    compareDocuments(
      { scenes: [], branding: null },
      {
        scenes: [],
        branding: { texts: [{ text: "Title" }] },
        transitions: { style: "fade" },
      },
    ),
    /branding, transitions/,
  );
  const result = compareDocuments(
    {
      scenes: [
        { id: "a", title: "A" },
        { id: "b", title: "B" },
      ],
      ratio: "9:16",
    },
    {
      scenes: [
        { id: "a", title: "Changed" },
        { id: "c", title: "C" },
      ],
      ratio: "16:9",
    },
  );
  assert(result.includes("thêm 1 cảnh, bỏ 1 cảnh, sửa 1 cảnh"));
  assert(result.includes("ratio"));
  assert(
    compareDocuments(
      { scenes: [], captions: { color: "#fff" } },
      { scenes: [], captions: { color: "#000" } },
    ).includes("captions"),
  );
});

test("sentence capitalization and punctuation retain tags and protect URLs", () => {
  const input = writeSrt([
    { start: 0, end: 1000, text: "<b>xIN CHÀO</b>!tỐT chỨ? vÂNG." },
  ]);
  assert.equal(
    parseSrt(transformSrt(input, "sentence", 0))[0].text,
    "<b>Xin chào</b>!Tốt chứ? Vâng.",
  );
  const punctuation = writeSrt([
    {
      start: 0,
      end: 1000,
      text: "Xin ,chào!bạn;đẹp:quá https://example.com?a=b www.example.com/a?b=c 1,000",
    },
  ]);
  const result = parseSrt(transformSrt(punctuation, "punctuation", 0))[0].text;
  assert(result.includes("Xin, chào! bạn; đẹp: quá"));
  assert(result.includes("https://example.com?a=b"));
  assert(result.includes("1,000"));
});
test("emoji cleanup preserves numeric text and nonemoji joiners, rejects an empty cue", () => {
  const input = writeSrt([
    { start: 0, end: 1000, text: "Xin 👨‍👩‍👧‍👦 chào 🇻🇳 👍🏽 1️⃣ test क्‍ष" },
  ]);
  const result = parseSrt(transformSrt(input, "emoji", 0))[0].text;
  assert.equal(result, "Xin chào 1 test क्‍ष");
  assert.throws(() =>
    transformSrt(writeSrt([{ start: 0, end: 1000, text: "🙂" }]), "emoji", 0),
  );
});
test("subtitle range intersects cues and shifts the selected timeline to zero", () => {
  const {
    subtitleRange,
  } = require("../src/Studio.Web/src/app/creator-utils.ts");
  const input = writeSrt([
    { start: 0, end: 2000, text: "A" },
    { start: 2500, end: 5000, text: "B" },
    { start: 6000, end: 7000, text: "C" },
  ]);
  assert.deepEqual(parseSrt(subtitleRange(input, 1, 4)), [
    { start: 0, end: 1000, text: "A" },
    { start: 1500, end: 3000, text: "B" },
  ]);
  assert.throws(() => subtitleRange(input, 8, 9));
  assert.throws(() => subtitleRange(input, 3, 2));
});
test("merge append aligns first imported cue and timeline mode retains original timestamps", () => {
  const { mergeSrt } = require("../src/Studio.Web/src/app/creator-utils.ts");
  const incoming = writeSrt([{ start: 10000, end: 11000, text: "C" }]);
  const appended = parseSrt(mergeSrt(source, incoming, true, 0.2));
  assert.equal(appended.at(-1).start, 3200);
  assert.equal(appended.at(-1).end, 4200);
  const timeline = parseSrt(mergeSrt(source, incoming, false, 0));
  assert.equal(timeline.at(-1).start, 10000);
  assert.throws(() => mergeSrt(source, incoming, true, -1));
  assert.equal(parseSrt(mergeSrt("", incoming, true, 0)).length, 1);
});
test("subtitle CSV quotes multilingual content and neutralizes spreadsheet formulas", () => {
  const { subtitleCsv } = require("../src/Studio.Web/src/app/creator-utils.ts");
  const csv = subtitleCsv(
    writeSrt([
      { start: 0, end: 1234, text: "=SUM(1,2)" },
      { start: 1500, end: 2000, text: 'Xin, "chào"' },
    ]),
  );
  assert(csv.startsWith("\uFEFFindex,start_ms"));
  assert(csv.includes('"\'=SUM(1,2)"'));
  assert(csv.includes('"Xin, ""chào"""'));
  assert(csv.includes('"1234"'));
});
