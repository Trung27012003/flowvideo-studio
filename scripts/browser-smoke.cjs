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
(async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), "flowvideo-browser-"));
  let server,
    browser,
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
    const page = await browser.newPage({
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
    await page.locator("input[type=file]").setInputFiles([clip, subtitle]);
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
    await fs.mkdir(path.join(root, "test-results"), { recursive: true });
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({
      path: path.join(root, "test-results/voice.png"),
      fullPage: true,
    });
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
      "PASS browser: project, upload, trim, real export/preview, cancel, library, subtitle editor/download, AI capability, mobile layout; no JS errors",
    );
  } catch (error) {
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
