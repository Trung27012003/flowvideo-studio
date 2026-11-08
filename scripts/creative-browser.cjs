const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const path = require("node:path");
module.exports = async function creativeBrowser(
  page,
  root,
  temp,
  project,
  waitForJobs,
) {
  const workspace = page.locator("app-creative-workspace");
  await workspace.locator(":scope > details > summary").click();
  await workspace.getByText("Thư viện hồ sơ (0/50)", { exact: true }).click();
  await workspace.getByLabel("Tên hồ sơ", { exact: true }).fill("Nhân vật An");
  await workspace
    .getByLabel("Mô tả hồ sơ", { exact: true })
    .fill("Lập trình viên C#, áo xanh.");
  await workspace
    .getByRole("button", { name: "Tạo hồ sơ", exact: true })
    .click();
  await workspace.getByText("Nhân vật An", { exact: true }).first().waitFor();
  await workspace
    .getByLabel("Cảnh nhận hồ sơ", { exact: true })
    .selectOption({ index: 1 });
  await workspace
    .getByLabel("Nhân vật 1", { exact: true })
    .selectOption({ index: 1 });
  await workspace
    .getByRole("button", { name: "Gắn bản chụp vào cảnh", exact: true })
    .click();
  const board = page.locator("app-storyboard");
  await board.getByText("Nhân vật An v1 ·", { exact: false }).waitFor();
  await workspace
    .getByRole("button", { name: "Sửa hồ sơ", exact: true })
    .click();
  await page.waitForFunction(
    () =>
      document.querySelector('app-creative-workspace [aria-label="Tên hồ sơ"]')
        .value === "Nhân vật An",
  );
  await workspace
    .getByLabel("Tên hồ sơ", { exact: true })
    .fill("An phiên bản mới");
  await workspace
    .getByRole("button", { name: "Cập nhật hồ sơ", exact: true })
    .click();
  await workspace
    .getByText("An phiên bản mới", { exact: true })
    .first()
    .waitFor();
  await board.getByText("Nhân vật An v1 ·", { exact: false }).waitFor();
  const profilesDownload = page.waitForEvent("download");
  await workspace
    .getByRole("button", { name: "Xuất gói hồ sơ JSON", exact: true })
    .click();
  const pack = JSON.parse(
    await fs.readFile(await (await profilesDownload).path(), "utf8"),
  );
  assert.equal(pack.format, "flowvideo.profiles");
  assert.equal(pack.profiles[0].content.name, "An phiên bản mới");
  assert.equal(pack.profiles[0].content.referenceId, null);
  const graphics = board.locator("app-graphics-options");
  await graphics.locator("summary").click();
  await graphics.getByRole("button", { name: "Thêm chữ", exact: true }).click();
  await graphics
    .getByLabel("Nội dung chữ", { exact: true })
    .fill("Xin chào: 100% AI");
  await graphics.getByLabel("Kết thúc chữ", { exact: true }).fill("1.5");
  await graphics
    .getByLabel("Chuyển cảnh", { exact: true })
    .selectOption("fade");
  await graphics
    .getByLabel("Thời gian chuyển cảnh", { exact: true })
    .fill("0.2");
  await board
    .getByRole("button", { name: "Lưu storyboard", exact: true })
    .click();
  await page
    .getByRole("status")
    .filter({ hasText: "Đã lưu kịch bản và storyboard" })
    .waitFor();
  const saved = await (
    await page.request.get(
      "http://127.0.0.1:5099/api/projects/" + project + "/document",
    )
  ).json();
  assert.equal(saved.document.branding.texts[0].text, "Xin chào: 100% AI");
  assert.equal(saved.document.transitions.style, "fade");
  assert.equal(
    saved.document.scenes[0].continuity.characters[0].content.name,
    "Nhân vật An",
  );
  await workspace
    .getByText("Ngân sách AI theo đơn giá tự nhập", { exact: true })
    .click();
  await workspace
    .getByLabel("Chặn AI vượt ngân sách dự toán", { exact: true })
    .check();
  await workspace.getByLabel("Giới hạn USD", { exact: true }).fill("1");
  await workspace.getByLabel("USD / ảnh", { exact: true }).fill("0.25");
  await workspace
    .getByRole("button", { name: "Lưu ngân sách", exact: true })
    .click();
  await workspace
    .getByRole("status")
    .filter({ hasText: "Đã lưu ngân sách dự toán" })
    .waitFor();
  await workspace
    .getByRole("button", { name: "Tính thử chi phí", exact: true })
    .click();
  await workspace
    .getByRole("status")
    .filter({ hasText: "Dự toán: 0.25 USD" })
    .waitFor();
  const ledgerDownload = page.waitForEvent("download");
  await workspace
    .getByRole("button", { name: "Xuất sổ dự toán JSON", exact: true })
    .click();
  const ledger = JSON.parse(
    await fs.readFile(await (await ledgerDownload).path(), "utf8"),
  );
  assert.equal(ledger.config.enabled, true);
  assert.equal(ledger.config.imageUsdPerCall, 0.25);
  const archiveDownload = page.waitForEvent("download");
  await workspace
    .getByRole("button", { name: "Backup dự án đã lưu (ZIP)", exact: true })
    .click();
  const archive = await archiveDownload;
  const archiveFile = path.join(temp, "creative-backup.zip");
  await archive.saveAs(archiveFile);
  const before = new Set(
    (
      await (
        await page.request.get("http://127.0.0.1:5099/api/projects")
      ).json()
    ).map((p) => p.id),
  );
  await workspace
    .getByLabel("Khôi phục backup", { exact: true })
    .setInputFiles(archiveFile);
  await page.waitForFunction(() =>
    [...document.querySelectorAll("[role=status]")].some((e) =>
      e.textContent.includes("Đã khôi phục thành dự án mới"),
    ),
  );
  const after = await (
    await page.request.get("http://127.0.0.1:5099/api/projects")
  ).json();
  const restored = after.find((p) => !before.has(p.id));
  assert(restored);
  const restoredDoc = await (
    await page.request.get(
      "http://127.0.0.1:5099/api/projects/" + restored.id + "/document",
    )
  ).json();
  assert.equal(
    restoredDoc.document.branding.texts[0].text,
    "Xin chào: 100% AI",
  );
  assert.equal(
    restoredDoc.document.scenes[0].continuity.characters[0].content.name,
    "Nhân vật An",
  );
  assert.notEqual(
    restoredDoc.document.scenes[0].assetId,
    saved.document.scenes[0].assetId,
  );
  const budget = await (
    await page.request.get(
      "http://127.0.0.1:5099/api/projects/" + restored.id + "/budget",
    )
  ).json();
  assert.equal(budget.config.enabled, false);
  assert.equal(budget.entries.length, 0);
  await board
    .getByLabel("Nội dung chữ", { exact: true })
    .waitFor({ state: "attached" });
  // Existing media and no TTS: restoring never calls AI, even with an enabled budget in the source project.
  await board
    .getByRole("button", { name: "Lưu & dựng storyboard", exact: true })
    .click();
  const jobs = await waitForJobs(page, restored.id, (rows) =>
    rows.some((j) => j.status === "Succeeded"),
  );
  const qa = await (
    await page.request.get(
      "http://127.0.0.1:5099/api/jobs/" + jobs[0].id + "/qa",
    )
  ).json();
  assert.equal(qa.result, "Passed");
  await page.screenshot({
    path: path.join(root, "test-results/creative-studio.png"),
    fullPage: true,
  });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(
    await page.evaluate(
      () => document.documentElement.scrollWidth > innerWidth,
    ),
    false,
    "Creative workspace mobile overflow",
  );
  await page.setViewportSize({ width: 1440, height: 1050 });
  console.log(
    "PASS v0.15 browser: real profile CRUD/snapshot isolation/export, graphics/transitions persistence, budget/pricing/quote/export, ZIP backup+restore with remapped media, real restored render+QA; no paid AI requests",
  );
};
