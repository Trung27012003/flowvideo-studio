// Requires a Release backend build, Angular production build and Playwright Chromium.
const path = require("node:path");
const fs = require("node:fs/promises");
const os = require("node:os");
const { spawn, execFileSync } = require("node:child_process");
const { once } = require("node:events");
const assert = require("node:assert/strict");
const root = path.resolve(__dirname, "..");
const { chromium } = require(
  path.join(root, "src/Studio.Web/node_modules/playwright"),
);
const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
async function waitForJobs(page, project, predicate) {
  const deadline = Date.now() + 120000;
  let last = [];
  while (Date.now() < deadline) {
    const response = await page.request.get(
      "http://127.0.0.1:5099/api/projects/" + project + "/jobs",
    );
    assert.equal(response.status(), 200);
    last = await response.json();
    assert(!last.some((j) => j.status === "Failed"), JSON.stringify(last));
    if (predicate(last)) return last;
    await delay(250);
  }
  throw new Error("Jobs did not reach expected state: " + JSON.stringify(last));
}

(async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), "flowvideo-browser-"));
  let server,
    browser,
    page,
    logs = "";
  try {
    const clip = path.join(temp, "sample.mp4");
    execFileSync("ffmpeg", [
      "-y",
      "-hide_banner",
      "-loglevel",
      "error",
      "-f",
      "lavfi",
      "-i",
      "color=c=teal:s=320x240:d=2",
      "-c:v",
      "libx264",
      "-pix_fmt",
      "yuv420p",
      clip,
    ]);
    const subtitle = path.join(temp, "sample.srt");
    await fs.writeFile(
      subtitle,
      "1\n00:00:00,000 --> 00:00:02,000\nXin chào FlowVideo\n",
    );
    server = spawn(
      "dotnet",
      [
        path.join(root, "src/Studio.Api/bin/Release/net10.0/Studio.Api.dll"),
        "--urls",
        "http://127.0.0.1:5099",
        "--webroot",
        path.join(root, "src/Studio.Web/dist/web/browser"),
      ],
      {
        cwd: root,
        env: {
          ...process.env,
          STUDIO_DATA: path.join(temp, "data"),
          OPENAI_API_KEY: "",
        },
        stdio: ["ignore", "pipe", "pipe"],
      },
    );
    server.stdout.on("data", (data) => {
      logs = (logs + data).slice(-12000);
    });
    server.stderr.on("data", (data) => {
      logs = (logs + data).slice(-12000);
    });
    server.on("error", (error) => {
      logs += error.message;
    });
    let ready = false;
    for (let i = 0; i < 100; i++) {
      try {
        const r = await fetch("http://127.0.0.1:5099/api/health");
        if (r.ok) {
          ready = true;
          break;
        }
      } catch {}
      await delay(100);
    }
    assert(ready, "Backend did not start: " + logs);
    browser = await chromium.launch({
      headless: true,
      ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE
        ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE }
        : {}),
      args: ["--no-sandbox"],
    });
    page = await browser.newPage({
      viewport: { width: 1440, height: 1050 },
    });
    const errors = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("http://127.0.0.1:5099");
    await page
      .getByRole("textbox", { name: "Tên dự án mới" })
      .fill("Browser smoke");
    await page.getByRole("button", { name: "＋ Tạo dự án" }).click();
    await page.getByRole("heading", { name: "Thiết lập video" }).waitFor();
    await page.waitForFunction(
      () => !document.querySelector("input[type=file][multiple]")?.disabled,
    );
    await page
      .locator("input[type=file][multiple]")
      .setInputFiles([clip, subtitle]);
    await page
      .getByRole("status")
      .filter({ hasText: "Đã nhập 2 file" })
      .waitFor();
    await page.getByRole("button", { name: /sample.mp4/ }).click();
    await page
      .getByRole("spinbutton", { name: "Điểm vào sample.mp4" })
      .fill("0.5");
    await page
      .getByRole("spinbutton", { name: "Điểm ra sample.mp4" })
      .fill("1.5");
    const renderRequest = page.waitForRequest(
      (r) => r.method() === "POST" && r.url().endsWith("/renders"),
    );
    await page.getByRole("button", { name: "▷ Tạo & chạy video" }).click();
    const body = (await renderRequest).postDataJSON();
    assert.equal(body.cuts[0].start, 0.5);
    assert.equal(body.cuts[0].end, 1.5);
    await page
      .getByRole("link", { name: "Tải MP4 ↓" })
      .waitFor({ timeout: 60000 });
    await page.getByRole("button", { name: "Xem", exact: true }).click();
    await page.locator("video").evaluate((video) => video.load());
    await page.waitForFunction(() => {
      const v = document.querySelector("video");
      return v && Number.isFinite(v.duration) && v.duration > 0;
    });
    const duration = await page.locator("video").evaluate((v) => v.duration);
    assert(Math.abs(duration - 1) < 0.15, duration);
    await page.getByRole("button", { name: "Đóng ×" }).click();
    await page.getByLabel("Chế độ dựng").selectOption("loop");
    await page.getByLabel("Giới hạn (giây)").fill("600");
    await page.getByLabel("Phụ đề SRT").selectOption({ label: "sample.srt" });
    await page.getByRole("button", { name: "▷ Tạo & chạy video" }).click();
    await page.getByRole("button", { name: "Hủy", exact: true }).click();
    await page.locator(".status").filter({ hasText: "Đã hủy" }).waitFor();
    await page.getByRole("button", { name: /Thư viện media/ }).click();
    await page.getByRole("link", { name: /sample.mp4/ }).waitFor();
    await page.getByLabel("Tìm tài nguyên").fill("sample");
    await page.getByLabel("Loại tài nguyên").selectOption("video");
    await page.waitForFunction(
      () => document.querySelectorAll(".library-card").length === 1,
    );
    await page.getByLabel("Tìm tài nguyên").fill("khong co ket qua");
    await page.waitForFunction(
      () => document.querySelectorAll(".library-card").length === 0,
    );
    await page.getByRole("button", { name: "Xóa bộ lọc", exact: true }).click();
    await page.waitForFunction(
      () => document.querySelectorAll(".library-card").length === 2,
    );
    await page
      .getByRole("button", { name: "Xem trước sample.mp4", exact: true })
      .click();
    await page.getByRole("dialog").waitFor();
    await page.waitForFunction(
      () => document.querySelector("dialog video")?.readyState >= 1,
    );
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => !document.querySelector("dialog").open);
    await page.getByLabel("Loại tài nguyên").selectOption("subtitle");
    await page
      .getByRole("button", { name: "Xem trước sample.srt", exact: true })
      .click();
    await page
      .getByRole("dialog")
      .locator("pre")
      .filter({ hasText: "Xin chào" })
      .waitFor();
    await page
      .getByRole("button", { name: "Đóng xem trước tài nguyên", exact: true })
      .click();
    await page.getByRole("button", { name: "Xóa bộ lọc", exact: true }).click();
    const tone = path.join(temp, "tone.wav");
    execFileSync("ffmpeg", [
      "-y",
      "-hide_banner",
      "-loglevel",
      "error",
      "-f",
      "lavfi",
      "-i",
      "sine=frequency=440:duration=1",
      tone,
    ]);
    await page.waitForFunction(
      () => !document.querySelector("input[type=file][multiple]")?.disabled,
    );
    await page.locator("input[type=file][multiple]").setInputFiles(tone);
    await page
      .getByRole("status")
      .filter({ hasText: "Đã nhập 1 file" })
      .waitFor();
    await page
      .getByRole("button", { name: "Xem trước tone.wav", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelector("dialog audio")?.readyState >= 1,
    );
    await page.keyboard.press("Escape");

    await page.getByRole("button", { name: /Kịch bản AI/ }).click();
    assert.equal(
      await page.getByRole("button", { name: "✧ Viết kịch bản" }).isDisabled(),
      true,
    );
    await page.getByRole("button", { name: /Giọng đọc & phụ đề/ }).click();
    assert.equal(
      await page
        .getByRole("button", { name: "Tạo giọng đọc AI", exact: true })
        .isDisabled(),
      true,
    );
    assert.equal(
      await page
        .getByRole("button", { name: "Nhận diện & tạo SRT" })
        .isDisabled(),
      true,
    );
    await page
      .getByLabel("Phụ đề có sẵn")
      .selectOption({ label: "sample.srt" });
    await page.getByRole("button", { name: "Mở phụ đề" }).click();
    await page.waitForFunction(() =>
      document.querySelector(".srt-content")?.value.includes("Xin chào"),
    );
    await page.getByLabel("Tên bản mới").fill("Phụ đề đã sửa");
    await page
      .getByLabel("Nội dung SRT")
      .fill("1\n00:00:00,000 --> 00:00:01,000\nNội dung đã sửa");
    await page.getByRole("button", { name: "Lưu bản phụ đề" }).click();
    await page
      .getByRole("status")
      .filter({ hasText: "Đã lưu bản phụ đề mới" })
      .waitFor();
    const saved = await page
      .getByRole("link", { name: "Tải bản đã lưu ↓" })
      .getAttribute("href");
    const savedText = await (
      await page.request.get("http://127.0.0.1:5099" + saved)
    ).text();
    assert(savedText.includes("Nội dung đã sửa"));
    await page
      .getByRole("button", { name: "Thư viện media", exact: true })
      .click();
    await page.getByLabel("Tìm tài nguyên").fill("PHU DE DA SUA");
    await page.waitForFunction(
      () => document.querySelectorAll(".library-card").length === 1,
    );
    await page.getByRole("link", { name: /Phụ đề đã sửa/ }).waitFor();
    await page.getByRole("button", { name: "Xóa bộ lọc", exact: true }).click();
    await page
      .getByRole("button", { name: "Giọng đọc & phụ đề", exact: true })
      .click();

    await fs.mkdir(path.join(root, "test-results"), { recursive: true });
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({
      path: path.join(root, "test-results/voice.png"),
      fullPage: true,
    });
    await page.getByRole("button", { name: /Kịch bản AI/ }).click();
    const scriptText = "Mở đầu câu chuyện.\n\nKết thúc câu chuyện.";
    await page
      .getByRole("textbox", { name: "Kịch bản", exact: true })
      .fill(scriptText);
    await page
      .getByRole("button", { name: "Lưu kịch bản", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Đã lưu kịch bản và storyboard" })
      .waitFor();
    await page.reload();
    await page.getByRole("button", { name: /Kịch bản AI/ }).click();
    await page.waitForFunction(() =>
      document
        .querySelector(".script-output")
        ?.value.includes("Mở đầu câu chuyện."),
    );
    assert.equal(
      await page
        .getByRole("textbox", { name: "Kịch bản", exact: true })
        .inputValue(),
      scriptText,
    );
    await page.getByRole("button", { name: "Chia đoạn thành cảnh" }).click();
    await page
      .getByRole("button", { name: "Preset Shorts", exact: true })
      .click();
    await page.waitForFunction(() =>
      Array.from(
        document.querySelectorAll("app-storyboard .settings select"),
      ).some((s) => s.value === "9:16"),
    );
    await page
      .getByLabel("Độ phân giải storyboard")
      .locator("option:checked")
      .filter({ hasText: "1080p" })
      .waitFor({ state: "attached" });
    assert.equal(
      await page
        .getByLabel("Độ phân giải storyboard")
        .evaluate((s) => s.selectedOptions[0].textContent.trim()),
      "1080p",
    );
    await page
      .getByRole("button", { name: "Preset YouTube", exact: true })
      .click();
    await page.waitForFunction(() =>
      Array.from(
        document.querySelectorAll("app-storyboard .settings select"),
      ).some((s) => s.value === "16:9"),
    );
    await page
      .getByRole("button", { name: "Preset 720p", exact: true })
      .click();
    await page.waitForFunction(() =>
      Array.from(document.querySelectorAll("app-storyboard select")).some(
        (s) => s.selectedOptions[0]?.textContent.trim() === "720p",
      ),
    );
    await page
      .getByRole("slider", { name: "Âm lượng giọng storyboard", exact: true })
      .evaluate((input) => {
        input.value = "65";
        input.dispatchEvent(new Event("input", { bubbles: true }));
      });
    await page
      .getByRole("slider", { name: "Âm lượng nhạc storyboard", exact: true })
      .evaluate((input) => {
        input.value = "12";
        input.dispatchEvent(new Event("input", { bubbles: true }));
      });

    await page
      .getByRole("combobox", { name: "Tư liệu của cảnh" })
      .nth(0)
      .selectOption({ label: "sample.mp4" });
    await page
      .getByRole("combobox", { name: "Tư liệu của cảnh" })
      .nth(1)
      .selectOption({ label: "sample.mp4" });
    await page
      .getByRole("spinbutton", { name: "Thời lượng cảnh (s)" })
      .nth(0)
      .fill("1");
    await page
      .getByRole("spinbutton", { name: "Thời lượng cảnh (s)" })
      .nth(1)
      .fill("2");
    await page
      .getByRole("button", { name: "Nhân bản cảnh storyboard", exact: true })
      .nth(0)
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll("app-storyboard .scene").length === 3,
    );
    await page.waitForFunction(() =>
      Array.from(document.querySelectorAll("app-storyboard .scene input")).some(
        (input) => input.value?.includes("bản sao"),
      ),
    );
    assert(
      (
        await page
          .getByRole("textbox", { name: "Tên cảnh", exact: true })
          .nth(1)
          .inputValue()
      ).includes("bản sao"),
    );
    await page
      .getByRole("button", { name: "Lưu storyboard", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Đã lưu kịch bản và storyboard" })
      .waitFor();
    const duplicateProject = await page
      .getByRole("combobox", { name: "Chọn dự án", exact: true })
      .inputValue();
    const duplicateDoc = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + duplicateProject + "/document",
      )
    ).json();
    assert.equal(
      new Set(duplicateDoc.document.scenes.map((s) => s.id)).size,
      3,
    );
    assert.equal(
      duplicateDoc.document.scenes[0].assetId,
      duplicateDoc.document.scenes[1].assetId,
    );
    assert.equal(duplicateDoc.document.voiceGain, 0.65);
    assert.equal(duplicateDoc.document.musicGain, 0.12);
    await page
      .getByRole("button", { name: "Xóa cảnh storyboard", exact: true })
      .nth(1)
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll("app-storyboard .scene").length === 2,
    );

    await page.getByRole("button", { name: "Lưu & dựng storyboard" }).click();
    await page
      .getByRole("status")
      .filter({ hasText: "Đã lưu và đưa storyboard" })
      .waitFor();
    await page.waitForFunction(
      () =>
        document.querySelectorAll('a.download[href$="/download"]').length === 2,
      { timeout: 60000 },
    );
    const srtLink = await page
      .getByRole("link", { name: "Tải SRT ↓", exact: true })
      .first()
      .getAttribute("href");
    const renderedSrt = await (
      await page.request.get("http://127.0.0.1:5099" + srtLink)
    ).text();
    assert(renderedSrt.includes("Mở đầu câu chuyện."));
    assert(renderedSrt.includes("Kết thúc câu chuyện."));
    await page
      .getByRole("button", { name: /Storyboard/, exact: false })
      .click();
    await page
      .getByRole("button", { name: "Đưa cảnh storyboard xuống" })
      .nth(0)
      .click();
    await page
      .getByRole("button", { name: "Lưu storyboard", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Đã lưu kịch bản và storyboard" })
      .waitFor();
    await page.reload();
    await page.getByRole("button", { name: /Storyboard/ }).click();
    await page.getByRole("textbox", { name: "Lời của cảnh" }).nth(0).waitFor();
    assert.equal(
      await page
        .getByRole("textbox", { name: "Lời của cảnh" })
        .nth(0)
        .inputValue(),
      "Kết thúc câu chuyện.",
    );
    // UI contract simulation only: the provider integration and durable queue
    // are tested separately in ImageTests; this server has no real API key.
    const projectId = await page
      .getByRole("combobox", { name: "Chọn dự án" })
      .inputValue();
    const picture = path.join(temp, "ai-ui.png");
    execFileSync("ffmpeg", [
      "-y",
      "-hide_banner",
      "-loglevel",
      "error",
      "-f",
      "lavfi",
      "-i",
      "color=c=teal:s=320x240",
      "-frames:v",
      "1",
      "-threads",
      "1",
      picture,
    ]);
    const uploadImage = await page.request.post(
      "http://127.0.0.1:5099/api/projects/" + projectId + "/assets",
      {
        multipart: {
          file: {
            name: "ai-ui.png",
            mimeType: "image/png",
            buffer: await fs.readFile(picture),
          },
        },
      },
    );
    assert.equal(uploadImage.ok(), true);
    const uiImage = await uploadImage.json();
    let imageRow = null;
    let imageStatus = "Queued";
    await page.route("**/api/capabilities", async (route) => {
      const response = await route.fetch();
      const caps = await response.json();
      await route.fulfill({ json: { ...caps, imageAi: true } });
    });
    await page.route(
      "**/api/projects/" + projectId + "/image-generations",
      async (route) => {
        if (route.request().method() === "POST") {
          const payload = route.request().postDataJSON();
          assert(payload.version >= 1);
          imageRow = {
            id: payload.requestId,
            sceneId: payload.sceneId,
            status: "Queued",
            prompt: "Một căn phòng yên tĩnh",
            message: "Đang chờ tạo ảnh",
            model: "gpt-image-1",
            size: "1024x1536",
            quality: "low",
          };
          await route.fulfill({
            status: 202,
            json: { generation: imageRow, reused: false },
          });
        } else
          await route.fulfill({
            json: {
              dailyLimit: 10,
              used: imageRow ? 1 : 0,
              items: imageRow
                ? [
                    {
                      generation: {
                        ...imageRow,
                        status: imageStatus,
                        message:
                          imageStatus === "Succeeded"
                            ? "Đã lưu ảnh"
                            : "Đang chờ tạo ảnh",
                      },
                      asset: imageStatus === "Succeeded" ? uiImage : null,
                    },
                  ]
                : [],
            },
          });
      },
    );
    await page.reload();
    await page.getByRole("button", { name: "Storyboard", exact: true }).click();
    await page
      .getByRole("textbox", { name: "Ghi chú hình ảnh / prompt" })
      .nth(0)
      .fill("Một căn phòng yên tĩnh");
    await page
      .getByRole("button", { name: "✧ Tạo ảnh AI cho cảnh", exact: true })
      .nth(0)
      .click();
    await page
      .locator(".image-job b")
      .filter({ hasText: "Đang chờ" })
      .waitFor();
    assert.equal(
      await page
        .getByRole("button", { name: "Đang xử lý ảnh…", exact: true })
        .isDisabled(),
      true,
    );
    imageStatus = "Succeeded";
    await page
      .getByRole("button", { name: "Dùng ảnh cho cảnh", exact: true })
      .waitFor({ timeout: 15000 });
    await page
      .getByRole("button", { name: "Dùng ảnh cho cảnh", exact: true })
      .click();
    await page.waitForFunction(
      (id) =>
        document.querySelector("app-storyboard .scene-media select")?.value ===
        id,
      uiImage.id,
    );
    assert.equal(
      await page
        .getByRole("combobox", { name: "Tư liệu của cảnh" })
        .nth(0)
        .inputValue(),
      uiImage.id,
    );
    await page
      .getByRole("button", { name: "Lưu & dựng storyboard", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelectorAll('a.download[href$="/download"]').length === 3,
    );
    const imageDocument = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + projectId + "/document",
      )
    ).json();
    assert.equal(imageDocument.document.scenes[0].assetId, uiImage.id);
    await page.getByRole("button", { name: "Storyboard", exact: true }).click();
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({
      path: path.join(root, "test-results/storyboard.png"),
      fullPage: true,
    });
    await page
      .getByRole("button", { name: "Thư viện media", exact: true })
      .click();
    await page.getByLabel("Loại tài nguyên").selectOption("image");
    await page
      .getByRole("button", { name: "Xem trước ai-ui.png", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelector("dialog img")?.naturalWidth > 0,
    );
    await page.keyboard.press("Escape");
    await page.getByRole("button", { name: "Storyboard", exact: true }).click();
    const downloading = page.waitForEvent("download");
    await page
      .getByRole("button", { name: "Xuất storyboard JSON", exact: true })
      .click();
    const downloaded = await downloading;
    const downloadedPath = await downloaded.path();
    const packageData = JSON.parse(await fs.readFile(downloadedPath, "utf8"));
    assert.equal(packageData.format, "flowvideo.storyboard");
    assert.equal(packageData.schemaVersion, 1);
    assert.equal(packageData.document.voiceGain, 0.65);
    assert.equal(packageData.document.musicGain, 0.12);
    assert.equal(packageData.document.resolution, 720);
    const originalProject = await page
      .getByRole("combobox", { name: "Chọn dự án", exact: true })
      .inputValue();
    await page
      .getByLabel("Nhập storyboard JSON", { exact: true })
      .setInputFiles(downloadedPath);
    await page
      .getByRole("status")
      .filter({ hasText: "Đã nhập thành dự án mới" })
      .waitFor();
    await page.waitForFunction(
      (old) =>
        document.querySelector('select[aria-label="Chọn dự án"]').value !== old,
      originalProject,
    );
    const importedProject = await page
      .getByRole("combobox", { name: "Chọn dự án", exact: true })
      .inputValue();
    assert.notEqual(importedProject, originalProject);
    const imported = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + importedProject + "/document",
      )
    ).json();
    assert.equal(imported.document.scenes.length, 2);
    assert(imported.document.scenes.every((s) => s.assetId === null));
    assert.equal(imported.document.voiceGain, 0.65);
    assert.equal(
      (
        await (
          await page.request.get(
            "http://127.0.0.1:5099/api/projects/" + importedProject + "/assets",
          )
        ).json()
      ).length,
      0,
    );
    // v0.7: exercise offline creator tools against persisted projects and real files.
    await page.getByLabel("Tên dự án hiện tại").fill("Batch 30 features");
    await page
      .getByRole("button", { name: "Đổi tên dự án", exact: true })
      .click();
    await page.waitForFunction(() =>
      document
        .querySelector('[aria-label="Chọn dự án"]')
        ?.selectedOptions[0]?.textContent.includes("Batch 30 features"),
    );
    const board = page.locator("app-storyboard");
    await board.getByLabel("Giây cho mỗi cảnh", { exact: true }).fill("2");
    await board.getByRole("button", { name: "Áp thời lượng tất cả" }).click();
    await board.getByLabel("Tổng mục tiêu (giây)", { exact: true }).fill("6");
    await board.getByRole("button", { name: "Co giãn theo tổng" }).click();
    await page.waitForFunction(
      () =>
        Array.from(
          document.querySelectorAll("app-storyboard .scene input[type=number]"),
        ).filter((x) => x.value === "3").length === 2,
    );
    await board
      .getByRole("button", { name: "Hoàn tác cảnh", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelector("app-storyboard .scene .timing input").value ===
        "2",
    );
    assert.equal(
      await board
        .getByLabel("Thời lượng cảnh (s)", { exact: true })
        .first()
        .inputValue(),
      "2",
    );
    await board
      .getByRole("button", { name: "Làm lại cảnh", exact: true })
      .click();
    await board.getByLabel("Tách đôi cảnh", { exact: true }).first().click();
    await page.waitForFunction(
      () => document.querySelectorAll("app-storyboard .scene").length === 3,
    );
    await board.getByRole("button", { name: "Đảo thứ tự cảnh" }).click();
    await board.getByRole("button", { name: "Đánh số tên cảnh" }).click();
    await board
      .getByLabel("Tên cảnh", { exact: true })
      .first()
      .fill("Kiểm thử tìm thay");
    await board
      .getByLabel("Lời của cảnh", { exact: true })
      .first()
      .fill("Xin chào nhóm sáng tạo");
    await board.getByLabel("Tìm trong lời cảnh", { exact: true }).fill("nhóm");
    await board
      .getByLabel("Thay bằng trong lời cảnh", { exact: true })
      .fill("bạn");
    await board.getByRole("button", { name: "Thay lời hàng loạt" }).click();
    await page.waitForFunction(
      () =>
        document.querySelector(
          "app-storyboard .scene textarea[aria-label='Lời của cảnh']",
        ).value === "Xin chào bạn sáng tạo",
    );
    assert.equal(
      await board
        .getByLabel("Lời của cảnh", { exact: true })
        .first()
        .inputValue(),
      "Xin chào bạn sáng tạo",
    );
    for (const [name, extension] of [
      ["Xuất lời đọc TXT", ".txt"],
      ["Xuất shotlist CSV", ".csv"],
    ]) {
      const download = page.waitForEvent("download");
      await board.getByRole("button", { name, exact: true }).click();
      assert((await download).suggestedFilename().endsWith(extension));
    }
    await page
      .getByRole("button", { name: "Thư viện media", exact: true })
      .click();
    await page.waitForFunction(
      () => !document.querySelector("input[type=file][multiple]")?.disabled,
    );
    await page.locator("input[type=file][multiple]").setInputFiles(clip);
    await page
      .getByRole("button", { name: "Xem trước sample.mp4", exact: true })
      .waitFor();
    await page
      .getByRole("button", { name: "Xem trước sample.mp4", exact: true })
      .click();
    await page.getByRole("button", { name: "Đọc thông số media" }).click();
    await page.getByLabel("Thông số media", { exact: true }).waitFor();
    assert(
      (await page.getByLabel("Thông số media").innerText()).includes("320"),
    );
    await page
      .getByLabel("Tên tài nguyên", { exact: true })
      .fill("Clip độc lập");
    await page
      .getByRole("button", { name: "Đổi tên tài nguyên", exact: true })
      .click();
    await page
      .getByRole("heading", { name: "Clip độc lập.mp4", exact: true })
      .waitFor();
    await page
      .getByRole("button", { name: "Đóng xem trước tài nguyên" })
      .click();
    await page
      .getByRole("button", {
        name: "Chọn trang đang lọc (tối đa 20)",
        exact: true,
      })
      .click();
    let zip = page.waitForEvent("download");
    await page
      .getByRole("button", { name: "Tải ZIP 1 media", exact: true })
      .click();
    assert((await zip).suggestedFilename().endsWith(".zip"));
    let csv = page.waitForEvent("download");
    await page.getByRole("button", { name: "Xuất danh mục CSV" }).click();
    assert((await csv).suggestedFilename().endsWith(".csv"));
    await page.getByRole("button", { name: "Storyboard", exact: true }).click();
    await board.getByRole("button", { name: "Điền tư liệu còn thiếu" }).click();
    await page.waitForFunction(() =>
      Array.from(
        document.querySelectorAll("app-storyboard .scene .scene-media select"),
      ).every(
        (x) => x.value !== "" && x.value !== "null" && x.value !== "0: null",
      ),
    );
    await board.getByLabel("Cách vừa khung storyboard").selectOption("cover");
    await board.getByLabel("Fade đầu/cuối storyboard (s)").fill("0.2");
    await board.getByLabel("Hạ nhạc khi có giọng storyboard").check();
    await page
      .getByRole("button", { name: "Nhân bản dự án kèm media", exact: true })
      .click();
    await page.waitForFunction(() =>
      document
        .querySelector('[aria-label="Chọn dự án"]')
        ?.selectedOptions[0]?.textContent.includes("bản sao"),
    );
    const clonedProject = await page
      .getByLabel("Chọn dự án", { exact: true })
      .inputValue();
    assert.notEqual(clonedProject, importedProject);
    await page.waitForFunction(
      () => !document.querySelector("app-storyboard fieldset")?.disabled,
    );
    const cloned = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/document",
      )
    ).json();
    assert.equal(cloned.document.scenes.length, 3);
    assert(cloned.document.scenes.every((s) => s.assetId));
    assert.equal(cloned.document.fit, "cover");
    assert.equal(cloned.document.fadeSeconds, 0.2);
    assert.equal(cloned.document.duckMusic, true);
    await page
      .getByRole("button", { name: "Giọng đọc & phụ đề", exact: true })
      .click();
    await page
      .getByLabel("Nội dung SRT", { exact: true })
      .fill(
        "1\n00:00:01,000 --> 00:00:02,000\nXin chào\n\n2\n00:00:02,100 --> 00:00:03,000\nFlowVideo",
      );
    await page.getByLabel("Độ lệch SRT (s)", { exact: true }).fill("1");
    await page
      .getByRole("button", { name: "Dịch mốc SRT", exact: true })
      .click();
    await page.waitForFunction(() =>
      document
        .querySelector(".srt-content")
        .value.includes("00:00:02,000 --> 00:00:03,000"),
    );
    assert(
      (
        await page.getByLabel("Nội dung SRT", { exact: true }).inputValue()
      ).includes("00:00:02,000 --> 00:00:03,000"),
    );
    await page.getByLabel("Tốc độ SRT", { exact: true }).fill("2");
    await page
      .getByRole("button", { name: "Đổi tốc độ SRT", exact: true })
      .click();
    await page.getByLabel("Tìm trong SRT", { exact: true }).fill("Xin");
    await page.getByLabel("Thay bằng trong SRT", { exact: true }).fill("Kính");
    await page
      .getByRole("button", { name: "Thay chữ SRT", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Ngắt dòng SRT", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Gộp câu SRT gần nhau", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelector(".srt-content").value.trim().split(" --> ")
          .length === 2,
    );
    assert.equal(
      (await page.getByLabel("Nội dung SRT", { exact: true }).inputValue())
        .trim()
        .split(" --> ").length,
      2,
    );
    const vtt = page.waitForEvent("download");
    await page
      .getByRole("button", { name: "Xuất WebVTT", exact: true })
      .click();
    assert((await vtt).suggestedFilename().endsWith(".vtt"));
    await page.getByRole("button", { name: "Storyboard", exact: true }).click();
    console.log(
      "PASS v0.7 browser: rename, history, bulk scene tools, split, text/CSV/ZIP/VTT downloads, metadata, media rename, independent project clone, fit/fade/duck persistence, SRT transformations",
    );
    // v0.8 production tools: real media derivatives, all effect settings and render exports.
    await page
      .getByRole("button", { name: "Thư viện media", exact: true })
      .click();
    const mux = path.join(temp, "v08-source.mp4");
    execFileSync("ffmpeg", [
      "-y",
      "-hide_banner",
      "-loglevel",
      "error",
      "-i",
      clip,
      "-i",
      tone,
      "-c:v",
      "copy",
      "-c:a",
      "aac",
      "-shortest",
      mux,
    ]);
    await page.waitForFunction(
      () => !document.querySelector("input[type=file][multiple]")?.disabled,
    );
    await page.locator("input[type=file][multiple]").setInputFiles(mux);
    await page
      .getByRole("button", { name: "Xem trước v08-source.mp4", exact: true })
      .click();
    const dialog = page.getByRole("dialog", { name: "Xem trước tài nguyên" });
    await dialog.getByLabel("Mốc lấy ảnh (s)", { exact: true }).fill("0.25");
    await dialog
      .getByRole("button", { name: "Tạo thumbnail PNG", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelector('img[alt="Kết quả công cụ media"]')
          ?.naturalWidth > 0,
    );
    const thumbnailDownload = page.waitForEvent("download");
    await dialog
      .getByRole("link", { name: "Tải ảnh công cụ ↓", exact: true })
      .click();
    assert((await thumbnailDownload).suggestedFilename().endsWith(".png"));
    await dialog
      .getByRole("button", { name: "Xem sóng âm", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelector('img[alt="Kết quả công cụ media"]')
          ?.naturalWidth === 1000,
    );
    await dialog
      .getByRole("button", { name: "Lưu khung hình vào thư viện", exact: true })
      .click();
    await dialog
      .getByRole("status")
      .filter({ hasText: "vào thư viện" })
      .waitFor();
    await dialog
      .getByRole("button", { name: "Tách audio vào thư viện", exact: true })
      .click();
    await dialog
      .getByRole("status")
      .filter({ hasText: "audio.mp3 vào thư viện" })
      .waitFor();
    await dialog
      .getByRole("button", { name: "Đóng xem trước tài nguyên", exact: true })
      .click();
    await page.getByRole("button", { name: "Storyboard", exact: true }).click();
    await board.locator("app-render-options summary").click();
    for (const [label, value] of [
      ["Độ sáng", "0.15"],
      ["Tương phản", "1.1"],
      ["Độ bão hòa", "0.8"],
      ["Độ sắc nét", "0.3"],
      ["Điểm bắt đầu nhạc (s)", "0.1"],
      ["Trễ giọng (s)", "0.2"],
      ["Tốc độ giọng", "1.25"],
    ])
      await board
        .getByLabel(label + " · Storyboard", { exact: true })
        .fill(value);
    for (const label of [
      "Lật ngang",
      "Lật dọc",
      "Tối góc vignette",
      "Xuất âm thanh mono",
      "Chuẩn hóa độ lớn giọng",
    ])
      await board.getByLabel(label + " · Storyboard", { exact: true }).check();
    await board
      .getByLabel("Xoay hình · Storyboard", { exact: true })
      .selectOption({ label: "90°" });
    await board
      .getByLabel("FPS · Storyboard", { exact: true })
      .selectOption({ label: "24 fps" });
    await board
      .getByLabel("Chất lượng · Storyboard", { exact: true })
      .selectOption("compact");
    await board
      .getByLabel("Màu viền · Storyboard", { exact: true })
      .evaluate((el) => {
        el.value = "#112233";
        el.dispatchEvent(new Event("input", { bubbles: true }));
      });
    const v08Assets = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/assets",
      )
    ).json();
    const v08Audio = v08Assets.find((a) => a.name === "v08-source · audio.mp3");
    assert(v08Audio);
    await board.getByLabel("Giọng cho storyboard").selectOption(v08Audio.id);
    await board.getByLabel("Nhạc cho storyboard").selectOption(v08Audio.id);
    await board
      .getByRole("button", { name: "Lưu & dựng storyboard", exact: true })
      .click();
    await page
      .getByRole("heading", { name: "Hàng đợi render", exact: true })
      .waitFor();
    await page
      .getByRole("link", { name: "Tải MP4 ↓", exact: true })
      .waitFor({ timeout: 120000 });
    await page.getByLabel("Lọc trạng thái render").selectOption("Failed");
    await page.waitForFunction(
      () => document.querySelectorAll(".queue .job").length === 0,
    );
    await page.getByLabel("Lọc trạng thái render").selectOption("Succeeded");
    await page.getByLabel("Sắp xếp render").selectOption("oldest");
    await page
      .getByRole("link", { name: "Cấu hình JSON ↓", exact: true })
      .waitFor();
    const recipeDownload = page.waitForEvent("download");
    await page
      .getByRole("link", { name: "Cấu hình JSON ↓", exact: true })
      .click();
    const recipeFile = await recipeDownload;
    const recipe = JSON.parse(
      await fs.readFile(await recipeFile.path(), "utf8"),
    );
    assert.equal(recipe.request.effects.brightness, 0.15);
    assert.equal(recipe.request.effects.rotation, 90);
    assert.equal(recipe.request.effects.fps, 24);
    assert.equal(recipe.request.effects.background, "#112233");
    assert.equal(recipe.request.effects.normalizeVoice, true);
    assert.equal(recipe.request.effects.mono, true);
    const audioDownload = page.waitForEvent("download");
    await page
      .getByRole("link", { name: "Tải audio MP3 ↓", exact: true })
      .click();
    assert((await audioDownload).suggestedFilename().endsWith(".mp3"));
    await page
      .getByRole("button", { name: "Giọng đọc & phụ đề", exact: true })
      .click();
    await page
      .getByLabel("Nội dung SRT", { exact: true })
      .fill(
        "1\n00:00:00,000 --> 00:00:01,000\n<b>Xin chào</b>\n\n2\n00:00:01,050 --> 00:00:02,000\n<b>Xin chào</b>",
      );
    await page
      .getByRole("button", { name: "Xóa định dạng SRT", exact: true })
      .click();
    await page.waitForFunction(
      () => !document.querySelector(".srt-content").value.includes("<b>"),
    );
    await page
      .getByRole("button", { name: "Gộp lời trùng liên tiếp", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelector(".srt-content").value.trim().split(" --> ")
          .length === 2,
    );
    await page
      .getByRole("button", { name: "Phân tích tốc độ đọc SRT", exact: true })
      .click();
    await page
      .locator(".subtitle-report")
      .filter({ hasText: "1 câu" })
      .waitFor();
    const textDownload = page.waitForEvent("download");
    await page
      .getByRole("button", { name: "Xuất lời SRT TXT", exact: true })
      .click();
    const textFile = await textDownload;
    assert(
      (await fs.readFile(await textFile.path(), "utf8")).includes("Xin chào"),
    );
    await page
      .getByLabel("Nội dung SRT", { exact: true })
      .fill(
        "1\n00:00:00,000 --> 00:00:04,000\nXin chào bạn đến với FlowVideo Studio để cùng sáng tạo nội dung video",
      );
    await page.getByLabel("Ký tự mỗi câu khi tách", { exact: true }).fill("20");
    await page
      .getByRole("button", { name: "Tách câu SRT dài", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelector(".srt-content").value.trim().split(" --> ")
          .length > 2,
    );
    await page.getByLabel("Giới hạn SRT (giây)", { exact: true }).fill("1.5");
    await page
      .getByRole("button", { name: "Cắt SRT theo thời lượng", exact: true })
      .click();
    await page.waitForFunction(() =>
      document.querySelector(".srt-content").value.includes("00:00:01,500"),
    );
    await page.getByRole("button", { name: "Storyboard", exact: true }).click();
    await board.locator("app-render-options summary").click();
    assert.equal(
      await board
        .getByLabel("FPS · Storyboard", { exact: true })
        .evaluate((el) => el.selectedOptions[0].textContent.trim()),
      "24 fps",
    );
    console.log(
      "PASS v0.8 browser: 16 persisted effects, thumbnail/waveform/frame/extract audio, 6 subtitle tools, queue filters/sort, recipe JSON and rendered MP3 download",
    );
    // v0.9 durable checkpoints/presets, multi-scene tools, preflight and variant renders.
    const workflow = page.locator(".saved-workflow");
    await workflow.locator("summary").click();
    const checkpointTitle = await board
      .getByLabel("Tên cảnh", { exact: true })
      .first()
      .inputValue();
    await workflow
      .getByLabel("Tên mốc hoặc preset mới", { exact: true })
      .fill("Mốc v09");
    await workflow
      .getByRole("button", { name: "Lưu mốc khôi phục", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelectorAll(".saved-row[data-kind=checkpoint]").length ===
        1,
    );
    const checkpoint = workflow
      .locator(".saved-row[data-kind=checkpoint]")
      .first();
    await board
      .getByLabel("Tên cảnh", { exact: true })
      .first()
      .fill("Bản nháp đã đổi");
    await checkpoint
      .getByRole("button", { name: "So sánh mốc với bản nháp", exact: true })
      .click();
    await workflow
      .getByRole("status")
      .filter({ hasText: "sửa 1 cảnh" })
      .waitFor();
    await checkpoint
      .getByRole("button", { name: "Mở mốc vào bản nháp", exact: true })
      .click();
    await page.waitForFunction(
      (title) =>
        document.querySelector(
          "app-storyboard .scene input[aria-label='Tên cảnh']",
        )?.value === title,
      checkpointTitle,
    );
    await checkpoint.locator("input").fill("Mốc đã đổi tên");
    await checkpoint
      .getByRole("button", { name: "Đổi tên mục đã lưu", exact: true })
      .click();
    let checkpointDownload = page.waitForEvent("download");
    await checkpoint
      .getByRole("link", { name: "Xuất mốc JSON ↓", exact: true })
      .click();
    const cpFile = await checkpointDownload;
    const cpPackage = JSON.parse(
      await fs.readFile(await cpFile.path(), "utf8"),
    );
    assert.equal(cpPackage.name, "Mốc đã đổi tên");
    assert.equal(cpPackage.document.scenes.length, 3);
    await workflow
      .getByLabel("Tên mốc hoặc preset mới", { exact: true })
      .fill("Preset v09");
    await workflow
      .getByRole("button", { name: "Lưu preset dựng", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelectorAll(".saved-row[data-kind=preset]").length === 1,
    );
    const presetRow = workflow.locator(".saved-row[data-kind=preset]").first();
    await board.locator("app-render-options summary").click();
    await board.getByLabel("Độ sáng · Storyboard", { exact: true }).fill("0.4");
    await board
      .getByLabel("Lời của cảnh", { exact: true })
      .first()
      .fill("Giữ lời khi áp preset");
    await presetRow
      .getByRole("button", { name: "Áp preset lên storyboard", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelector(
          "app-storyboard .scene textarea[aria-label='Lời của cảnh']",
        )?.value === "Giữ lời khi áp preset",
    );
    await board.locator("app-render-options summary").click();
    await page.waitForFunction(() =>
      Array.from(
        document.querySelectorAll("app-render-options input[type=number]"),
      ).some((el) => el.value === "0.15"),
    );
    let presetDownload = page.waitForEvent("download");
    await presetRow
      .getByRole("link", { name: "Xuất preset JSON ↓", exact: true })
      .click();
    const presetFile = await presetDownload;
    const presetPath = path.join(temp, "v09-preset.json");
    await presetFile.saveAs(presetPath);
    const savedPreset = JSON.parse(await fs.readFile(presetPath, "utf8"));
    assert.equal(savedPreset.format, "flowvideo.render-preset");
    assert.deepEqual(savedPreset.settings.scenes, []);
    assert.equal(savedPreset.settings.voiceId, null);
    await workflow
      .getByLabel("Nhập preset dựng", { exact: true })
      .setInputFiles(presetPath);
    await page.waitForFunction(
      () =>
        document.querySelectorAll(".saved-row[data-kind=preset]").length === 2,
    );
    const importedPreset = workflow
      .locator(".saved-row[data-kind=preset]")
      .first();
    await importedPreset.locator("input").fill("Preset nhập đổi tên");
    await importedPreset
      .getByRole("button", { name: "Đổi tên mục đã lưu", exact: true })
      .click();
    page.once("dialog", (dialog) => dialog.accept());
    await importedPreset
      .getByRole("button", { name: "Xóa mục đã lưu", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelectorAll(".saved-row[data-kind=preset]").length === 1,
    );
    await workflow
      .getByRole("button", { name: "Tổng quan dữ liệu dự án", exact: true })
      .click();
    await workflow
      .getByRole("status")
      .filter({ hasText: "tài nguyên" })
      .waitFor();
    await workflow
      .getByRole("button", { name: "Kiểm tra trước khi dựng", exact: true })
      .click();
    await workflow
      .getByRole("status")
      .filter({ hasText: "Không có lỗi chặn dựng." })
      .waitFor();
    await board
      .getByText("Chọn nhiều cảnh & thời lượng theo lời", { exact: true })
      .click();
    await board.getByLabel("Chọn cảnh 1", { exact: true }).check();
    await board.getByLabel("Chọn cảnh 2", { exact: true }).check();
    await board
      .getByRole("button", { name: "Nhân bản cảnh đã chọn", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll("app-storyboard .scene").length === 5,
    );
    await board
      .getByRole("button", { name: "Xóa cảnh đã chọn", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll("app-storyboard .scene").length === 3,
    );
    await board
      .getByRole("button", { name: "Hoàn tác cảnh", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll("app-storyboard .scene").length === 5,
    );
    await board
      .getByRole("button", { name: "Hoàn tác cảnh", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll("app-storyboard .scene").length === 3,
    );
    await board
      .getByRole("button", { name: "Bỏ chọn cảnh", exact: true })
      .click();
    await board.getByLabel("Chọn cảnh 1", { exact: true }).check();
    const sceneSelectionDownload = page.waitForEvent("download");
    await board
      .getByRole("button", { name: "Xuất JSON cảnh đã chọn", exact: true })
      .click();
    const selectionFile = await sceneSelectionDownload;
    const selectionPackage = JSON.parse(
      await fs.readFile(await selectionFile.path(), "utf8"),
    );
    assert.equal(selectionPackage.document.scenes.length, 1);
    await board
      .getByRole("button", { name: "Đưa nhóm về cuối", exact: true })
      .click();
    await page.waitForFunction(
      (title) =>
        document.querySelector(
          "app-storyboard .scene:last-child input[aria-label='Tên cảnh']",
        )?.value === title,
      checkpointTitle,
    );
    assert.equal(
      await board.getByLabel("Tên cảnh", { exact: true }).last().inputValue(),
      checkpointTitle,
    );
    await board
      .getByRole("button", { name: "Đưa nhóm về đầu", exact: true })
      .click();
    await page.waitForFunction(
      (title) =>
        document.querySelector(
          "app-storyboard .scene input[aria-label='Tên cảnh']",
        )?.value === title,
      checkpointTitle,
    );
    await board
      .getByLabel("Tốc độ đọc để căn cảnh (từ/phút)", { exact: true })
      .fill("150");
    await board
      .getByRole("button", { name: "Căn thời lượng theo lời", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelector("app-storyboard .scene .timing input")?.value ===
        "2",
    );
    const beforeShuffle = await board
      .getByLabel("Tên cảnh", { exact: true })
      .evaluateAll((els) => els.map((el) => el.value).sort());
    await board
      .getByRole("button", { name: "Trộn thứ tự cảnh", exact: true })
      .click();
    assert.deepEqual(
      await board
        .getByLabel("Tên cảnh", { exact: true })
        .evaluateAll((els) => els.map((el) => el.value).sort()),
      beforeShuffle,
    );
    await board
      .getByRole("button", { name: "Chọn tất cả cảnh", exact: true })
      .click();
    await page.waitForFunction(
      () =>
        document.querySelectorAll(
          "app-storyboard .scene input[aria-label^='Chọn cảnh ']:checked",
        ).length === 3,
    );
    await board
      .getByRole("button", { name: "Bỏ chọn cảnh", exact: true })
      .click();
    await board.getByLabel("Giây cho mỗi cảnh", { exact: true }).fill("1");
    await board
      .getByRole("button", { name: "Áp thời lượng tất cả", exact: true })
      .click();
    const beforeVariants = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/jobs",
      )
    ).json();
    await board
      .getByRole("button", { name: "Dựng cả dọc và ngang", exact: true })
      .click();
    await page
      .getByRole("heading", { name: "Hàng đợi render", exact: true })
      .waitFor();
    await page.getByLabel("Lọc trạng thái render").selectOption("all");
    await waitForJobs(
      page,
      clonedProject,
      (jobs) =>
        jobs.length === 3 && jobs.every((j) => j.status === "Succeeded"),
    );
    const afterVariants = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/jobs",
      )
    ).json();
    const variants = afterVariants.filter(
      (j) => !beforeVariants.some((old) => old.id === j.id),
    );
    assert.equal(variants.length, 2);
    const variantRatios = [];
    for (const variant of variants) {
      const recipe = await (
        await page.request.get(
          "http://127.0.0.1:5099/api/jobs/" + variant.id + "/recipe",
        )
      ).json();
      variantRatios.push(recipe.request.ratio);
    }
    assert.deepEqual(variantRatios.sort(), ["16:9", "9:16"]);
    await page
      .getByRole("button", { name: "Dựng lại bản hoàn tất", exact: true })
      .first()
      .click();
    await waitForJobs(page, clonedProject, (jobs) => jobs.length === 4);
    await page
      .getByLabel("Tìm tác vụ render", { exact: true })
      .fill("not-a-job");
    await page.waitForFunction(
      () => document.querySelectorAll(".queue .job").length === 0,
    );
    assert(
      (await page.locator(".queue-stats").innerText()).includes("Hoàn tất"),
    );
    await page
      .getByLabel("Tìm tác vụ render", { exact: true })
      .fill(variants[0].id);
    await page.waitForFunction(
      () => document.querySelectorAll(".queue .job").length === 1,
    );
    await page.getByLabel("Tìm tác vụ render", { exact: true }).fill("");
    await page
      .getByRole("button", { name: "Giọng đọc & phụ đề", exact: true })
      .click();
    await page
      .getByLabel("Nội dung SRT", { exact: true })
      .fill(
        "2\n00:00:01,000 --> 00:00:02,000\n  chào    bạn\n\n1\n00:00:00,000 --> 00:00:01,500\n  xin   chào",
      );
    await page
      .getByRole("button", { name: "Sắp lại thứ tự câu SRT", exact: true })
      .click();
    await page.waitForFunction(() =>
      document
        .querySelector(".srt-content")
        .value.startsWith("1\n00:00:00,000"),
    );
    await page
      .getByRole("button", { name: "Xử lý mốc SRT chồng lấn", exact: true })
      .click();
    await page.waitForFunction(() =>
      document
        .querySelector(".srt-content")
        .value.includes("00:00:01,500 --> 00:00:02,000"),
    );
    await page
      .getByLabel("Thời lượng câu tối thiểu (s)", { exact: true })
      .fill("2");
    await page
      .getByRole("button", { name: "Giãn câu SRT quá ngắn", exact: true })
      .click();
    await page.waitForFunction(() =>
      document
        .querySelector(".srt-content")
        .value.includes("00:00:02,000 --> 00:00:04,000"),
    );
    await page
      .getByRole("button", { name: "Chuẩn hóa khoảng trắng SRT", exact: true })
      .click();
    await page
      .getByLabel("Kiểu chữ SRT", { exact: true })
      .selectOption("upper");
    await page
      .getByRole("button", { name: "Đổi kiểu chữ SRT", exact: true })
      .click();
    await page.waitForFunction(() =>
      document.querySelector(".srt-content").value.includes("XIN CHÀO"),
    );
    await page
      .getByRole("button", { name: "Xem bảng timeline SRT", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll(".timeline-table tbody tr").length === 2,
    );
    await page.getByRole("button", { name: "Storyboard", exact: true }).click();
    await workflow.locator("summary").click();
    await board
      .getByText("Chọn nhiều cảnh & thời lượng theo lời", { exact: true })
      .click();
    console.log(
      "PASS v0.9 browser: checkpoint restore/compare/rename/export, preset save/apply/rename/import/export/delete, summary/preflight, scene multi-select/batch/export/reading timing/shuffle, 6 SRT tools, real dual-aspect renders/repeat, queue stats/search",
    );
    // v0.10: individual scene settings, styled captions and library reuse.
    const firstScene = board.locator(".scene").first(),
      lastScene = board.locator(".scene").last();
    await firstScene.locator("app-scene-options summary").click();
    await firstScene
      .getByLabel("Vừa khung riêng", { exact: true })
      .selectOption("cover");
    await firstScene.getByLabel("Dùng độ sáng riêng", { exact: true }).check();
    await firstScene.getByLabel("Độ sáng riêng", { exact: true }).fill("0.1");
    await firstScene
      .getByLabel("Xoay riêng", { exact: true })
      .selectOption({ label: "180°" });
    for (const [label, value] of [
      ["Tốc độ hình cảnh", "2"],
      ["Zoom riêng", "1.2"],
      ["Tâm ngang cảnh (0–1)", "0"],
      ["Tâm dọc cảnh (0–1)", "1"],
      ["Fade riêng cảnh (s)", "0.2"],
    ])
      await firstScene.getByLabel(label, { exact: true }).fill(value);
    await firstScene.getByLabel("Hiện phụ đề cảnh", { exact: true }).uncheck();
    await firstScene
      .getByLabel("Ghi chú sản xuất cảnh", { exact: true })
      .fill("Ghi chú cảnh v10");
    await lastScene.locator("app-scene-options summary").click();
    await lastScene.getByLabel("Dùng cảnh khi dựng", { exact: true }).uncheck();
    await lastScene
      .getByLabel("Tư liệu của cảnh", { exact: true })
      .selectOption({ label: "Chọn ảnh / video" });
    await board.locator("app-caption-options summary").click();
    await board
      .getByLabel("Phông phụ đề · Storyboard", { exact: true })
      .selectOption("mono");
    for (const [label, value] of [
      ["Cỡ chữ phụ đề", "24"],
      ["Độ viền phụ đề", "2"],
      ["Bóng chữ phụ đề", "1"],
      ["Lề dọc phụ đề", "12"],
      ["Độ đậm nền hộp", "0.8"],
    ])
      await board
        .getByLabel(label + " · Storyboard", { exact: true })
        .fill(value);
    for (const [label, color] of [
      ["Màu chữ phụ đề", "#ff0000"],
      ["Màu viền phụ đề", "#0000ff"],
      ["Màu nền hộp", "#000000"],
    ])
      await board
        .getByLabel(label + " · Storyboard", { exact: true })
        .evaluate((el, color) => {
          el.value = color;
          el.dispatchEvent(new Event("input", { bubbles: true }));
        }, color);
    await board
      .getByLabel("Vị trí phụ đề · Storyboard", { exact: true })
      .selectOption({ label: "Trên giữa" });
    await board
      .getByLabel("Chữ đậm phụ đề · Storyboard", { exact: true })
      .check();
    await board
      .getByLabel("Nền hộp phụ đề · Storyboard", { exact: true })
      .check();
    const beforeV10 = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/jobs",
      )
    ).json();
    await board
      .getByRole("button", { name: "Lưu & dựng storyboard", exact: true })
      .click();
    await page
      .getByRole("heading", { name: "Hàng đợi render", exact: true })
      .waitFor();
    await waitForJobs(page, clonedProject, (jobs) =>
      jobs.some(
        (j) =>
          !beforeV10.some((old) => old.id === j.id) && j.status === "Succeeded",
      ),
    );
    const afterV10 = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/jobs",
      )
    ).json();
    const styledJob = afterV10.find(
      (j) => !beforeV10.some((old) => old.id === j.id),
    );
    const styledRecipe = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/jobs/" + styledJob.id + "/recipe",
      )
    ).json();
    assert.equal(styledRecipe.request.scenes.length, 2);
    assert.equal(styledRecipe.request.scenes[0].options.speed, 2);
    assert.equal(
      styledRecipe.request.scenes[0].options.note,
      "Ghi chú cảnh v10",
    );
    assert.equal(styledRecipe.request.scenes[0].options.caption, false);
    assert.equal(styledRecipe.request.captions.font, "mono");
    assert.equal(styledRecipe.request.captions.box, true);
    assert.equal(styledRecipe.request.captions.color, "#ff0000");
    const { parseSrt } = require("../src/Studio.Web/src/app/creator-utils.ts");
    const srtResponse = await page.request.get(
      "http://127.0.0.1:5099/api/jobs/" + styledJob.id + "/subtitles",
    );
    assert.equal(
      srtResponse.status(),
      200,
      JSON.stringify({
        status: styledJob.status,
        autoCaptions: styledRecipe.request.autoCaptions,
        scenes: styledRecipe.request.scenes,
      }),
    );
    const styledCues = parseSrt(await srtResponse.text());
    assert.equal(styledCues.length, 1);
    assert.equal(styledCues[0].start, 1000);
    await page
      .getByRole("button", { name: "Thư viện media", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Xem trước Clip độc lập.mp4", exact: true })
      .click();
    const mediaPreferences = page.getByRole("dialog", {
      name: "Xem trước tài nguyên",
    });
    await mediaPreferences
      .getByLabel("Đánh dấu tư liệu yêu thích", { exact: true })
      .check();
    await mediaPreferences
      .getByLabel("Nhãn tư liệu (cách bằng dấu phẩy)", { exact: true })
      .fill("demo, Góc máy");
    await mediaPreferences
      .getByLabel("Ghi chú tư liệu", { exact: true })
      .fill("Tư liệu dùng lại v10");
    await mediaPreferences
      .getByRole("button", { name: "Lưu thông tin tư liệu", exact: true })
      .click();
    await mediaPreferences
      .getByRole("status")
      .filter({ hasText: "Đã lưu yêu thích" })
      .waitFor();
    await mediaPreferences
      .getByRole("button", { name: "Xem nơi dùng tư liệu", exact: true })
      .click();
    await mediaPreferences
      .getByRole("status")
      .filter({ hasText: "job tham chiếu" })
      .waitFor();
    await mediaPreferences
      .getByRole("button", { name: "Đóng xem trước tài nguyên", exact: true })
      .click();
    await page.getByText("Công cụ tư liệu mở rộng", { exact: true }).click();
    await page.getByLabel("Chỉ tư liệu yêu thích", { exact: true }).check();
    await page.getByLabel("Lọc nhãn tư liệu", { exact: true }).fill("goc may");
    await page.waitForFunction(
      () => document.querySelectorAll(".library-card").length === 1,
    );
    await page
      .getByRole("button", { name: "Yêu thích Clip độc lập.mp4", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll(".library-card").length === 0,
    );
    await page.getByLabel("Chỉ tư liệu yêu thích", { exact: true }).uncheck();
    await page
      .getByRole("button", { name: "Yêu thích Clip độc lập.mp4", exact: true })
      .click();
    await page.getByRole("button", { name: "Xóa bộ lọc", exact: true }).click();
    await page.waitForFunction(
      () => !document.querySelector("input[type=file][multiple]")?.disabled,
    );
    await page.locator("input[type=file][multiple]").setInputFiles(clip);
    await page
      .getByRole("button", { name: "Xem trước sample.mp4", exact: true })
      .waitFor();
    await page
      .getByRole("button", { name: "Bỏ chọn media", exact: true })
      .click();
    await page
      .getByLabel("Chọn media Clip độc lập.mp4", { exact: true })
      .check();
    await page.getByLabel("Chọn media sample.mp4", { exact: true }).check();
    await page
      .getByRole("button", { name: "Tìm file trùng byte", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "1 nhóm trùng byte" })
      .waitFor();
    const reusableAssets = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/assets",
      )
    ).json();
    const sourceAsset = reusableAssets.find(
        (a) => a.name === "Clip độc lập.mp4",
      ),
      replacementAsset = reusableAssets.find((a) => a.name === "sample.mp4");
    assert(sourceAsset && replacementAsset);
    await page
      .getByLabel("Tiền tố tên hàng loạt", { exact: true })
      .fill("Batch-");
    await page
      .getByLabel("Hậu tố tên hàng loạt", { exact: true })
      .fill("-demo");
    await page.getByLabel("Số bắt đầu tên", { exact: true }).fill("7");
    await page
      .getByRole("button", { name: "Đổi tên media hàng loạt", exact: true })
      .click();
    await page
      .getByRole("button", { name: "Xem trước Batch-07-demo.mp4", exact: true })
      .waitFor();
    const targetBefore = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + importedProject + "/assets",
      )
    ).json();
    await page
      .getByLabel("Dự án đích sao chép", { exact: true })
      .selectOption(importedProject);
    await page
      .getByRole("button", { name: "Sao chép media sang dự án", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Đã sao chép 2 file độc lập" })
      .waitFor();
    const targetAfter = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + importedProject + "/assets",
      )
    ).json();
    assert.equal(targetAfter.length, targetBefore.length + 2);
    assert(
      !targetAfter.some(
        (a) => a.id === sourceAsset.id || a.id === replacementAsset.id,
      ),
    );
    const copiedPreferences = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" +
          importedProject +
          "/asset-preferences",
      )
    ).json();
    assert(
      copiedPreferences.some(
        (p) => p.favorite && p.note === "Tư liệu dùng lại v10",
      ),
    );
    await page
      .getByRole("button", { name: "Tạo cảnh từ media đã chọn", exact: true })
      .click();
    await page.waitForFunction(
      () => document.querySelectorAll("app-storyboard .scene").length === 5,
    );
    await board
      .getByText("Chọn nhiều cảnh & thời lượng theo lời", { exact: true })
      .click();
    await board
      .getByLabel("Nguồn cần thay", { exact: true })
      .selectOption(sourceAsset.id);
    const imageReplacement = reusableAssets.find((a) => a.kind === "image");
    assert(imageReplacement);
    await board
      .getByLabel("Tư liệu thay thế", { exact: true })
      .selectOption(imageReplacement.id);
    await board
      .getByRole("button", { name: "Thay tư liệu trên mọi cảnh", exact: true })
      .click();
    await board
      .getByRole("button", { name: "Lưu storyboard", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Đã lưu kịch bản và storyboard" })
      .waitFor();
    const imageBindings = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/document",
      )
    ).json();
    assert.equal(imageBindings.document.scenes[0].options.speed, 1);
    assert.equal(imageBindings.document.scenes[0].assetId, imageReplacement.id);
    await board
      .getByRole("button", { name: "Hoàn tác cảnh", exact: true })
      .click();
    await page.waitForFunction(
      (id) =>
        document.querySelector("app-storyboard .scene .scene-media select")
          .value === id,
      sourceAsset.id,
    );
    await board
      .getByLabel("Tư liệu thay thế", { exact: true })
      .selectOption(replacementAsset.id);
    await board
      .getByRole("button", { name: "Thay tư liệu trên mọi cảnh", exact: true })
      .click();
    await board
      .getByRole("button", { name: "Lưu storyboard", exact: true })
      .click();
    await page
      .getByRole("status")
      .filter({ hasText: "Đã lưu kịch bản và storyboard" })
      .waitFor();
    const rebound = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" + clonedProject + "/document",
      )
    ).json();
    assert.equal(rebound.document.scenes.length, 5);
    assert(!rebound.document.scenes.some((s) => s.assetId === sourceAsset.id));
    assert.equal(rebound.document.captions.size, 24);
    assert.equal(rebound.document.scenes[0].options.speed, 2);
    const oldUsage = await (
      await page.request.get(
        "http://127.0.0.1:5099/api/projects/" +
          clonedProject +
          "/assets/" +
          sourceAsset.id +
          "/usage",
      )
    ).json();
    assert.equal(oldUsage.scenes.length, 0);
    assert(oldUsage.checkpoints.length >= 1 && oldUsage.jobCount >= 1);
    await board.locator("app-caption-options summary").click();
    await board
      .locator(".scene")
      .first()
      .locator("app-scene-options summary")
      .click();
    console.log(
      "PASS v0.10 browser: 10 scene controls, 10 caption style settings, real styled render/disabled scene/per-scene captions, favorites/tags/notes/filter, usage, byte duplicates, bulk rename, project copy with preferences, scenes from selection and binding replacement",
    );
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({
      path: path.join(root, "test-results/imported.png"),
      fullPage: true,
    });
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(
      await page.evaluate(
        () => document.documentElement.scrollWidth > innerWidth,
      ),
      false,
      "Storyboard mobile overflow",
    );
    await page.getByRole("button", { name: /Studio & render/ }).click();
    await page.setViewportSize({ width: 390, height: 844 });
    assert.equal(
      await page.evaluate(
        () => document.documentElement.scrollWidth > innerWidth,
      ),
      false,
      "Mobile overflow",
    );
    await fs.mkdir(path.join(root, "test-results"), { recursive: true });
    await page.screenshot({
      path: path.join(root, "test-results/mobile.png"),
      fullPage: true,
    });
    await page.setViewportSize({ width: 1440, height: 1050 });
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({
      path: path.join(root, "test-results/studio.png"),
      fullPage: true,
    });
    assert.deepEqual(errors, []);
    console.log(
      "PASS browser: project, upload, trim, real export/preview, cancel, library, subtitle editor/download, AI capability, persistent script/storyboard, scene render, reorder, image job/apply UI contract, search/filter/preview, duplicate, presets, gains, JSON round trip, SRT download, mobile layout; no JS errors",
    );
  } catch (error) {
    if (page) {
      try {
        await fs.mkdir(path.join(root, "test-results"), { recursive: true });
        await page.screenshot({
          path: path.join(root, "test-results/failure.png"),
          fullPage: true,
        });
      } catch {}
    }
    console.error(logs);
    throw error;
  } finally {
    if (browser) await browser.close();
    if (server && server.exitCode === null) {
      const exited = once(server, "exit");
      server.kill("SIGTERM");
      const killTimer = setTimeout(() => server.kill("SIGKILL"), 10000);
      try {
        await exited;
      } finally {
        clearTimeout(killTimer);
      }
    }
    await fs.rm(temp, { recursive: true, force: true });
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
