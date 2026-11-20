import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { chromium } = require("/tmp/dikatrace-browser-test/node_modules/playwright-core");

const chrome = process.env.CHROME_EXECUTABLE;
if (!chrome) throw new Error("CHROME_EXECUTABLE is required");
const url = "http://127.0.0.1:8765/js/tests/preview-browser-smoke.html";
const browser = await chromium.launch({
  executablePath: chrome,
  headless: true,
  args: ["--no-sandbox", "--disable-dev-shm-usage"]
});
try {
  const page = await browser.newPage({ viewport: { width: 800, height: 650 } });
  const errors = [];
  page.on("pageerror", error => errors.push("PAGE: " + error.message));
  page.on("console", message => {
    if (message.type() === "error") errors.push("CONSOLE: " + message.text());
  });
  page.on("requestfailed", request =>
    errors.push("REQUEST: " + request.url() + ": " + request.failure()?.errorText));

  await page.goto(url, { waitUntil: "load" });
  try {
    await page.waitForFunction(
      () => document.body.dataset.smoke !== "running", { timeout: 20000 });
  } catch (_) {
    throw new Error("Smoke did not finish async image/SVG decode.");
  }
  const result = await page.evaluate(() => ({
    status: document.body.dataset.smoke,
    report: document.querySelector("#result")?.textContent,
    canvas: (() => {
      const c = document.querySelector("[data-preview-before] canvas");
      return { width: c?.width, height: c?.height };
    })(),
    vectorViewBox: document.querySelector("[data-preview-after] svg")?.getAttribute("viewBox")
  }));
  if (result.status !== "pass")
    throw new Error(JSON.stringify({ result, errors }, null, 2));
  console.log(JSON.stringify(result, null, 2));
  console.log("PASS real Chrome, native SVG zoom 1000x, bounded raster, no blank pan.");
} catch (error) {
  console.error(error?.stack || String(error));
  throw error;
} finally {
  await browser.close();
}
