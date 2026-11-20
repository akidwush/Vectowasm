import { qualityMetrics, svgStats, chooseBest } from "./quality-metrics.mjs";

const $ = id => document.getElementById(id);
const inputs = ["baseline", "experimental", "reference"];
const readable = { baseline: "DikaTracer Baseline", experimental: "DikaTracer Eksperimen", reference: "Vector Magic" };
let activeWorker = null, activeReject = null, currentToken = 0, URLs = [], report = null;
const maxSvgBytes = 12_000_000;

function releaseURLs() {
  for (const url of URLs) URL.revokeObjectURL(url);
  URLs = [];
}
function objectURL(blob) {
  const url = URL.createObjectURL(blob);
  URLs.push(url);
  return url;
}
function status(text) { $("status").textContent = text; }
function busy(value) {
  $("start").disabled = value;
  $("cancel").disabled = !value;
  for (const id of ["input", ...inputs, "maxSide", "engine"]) $(id).disabled = value;
}
function assertActive(token) {
  if (token !== currentToken) throw new Error("Proses dibatalkan.");
}
function stop(reason = "Benchmark dibatalkan.") {
  currentToken++;
  if (activeWorker) { activeWorker.terminate(); activeWorker = null; }
  if (activeReject) { activeReject(new Error(reason)); activeReject = null; }
  status(reason); busy(false);
}
$("cancel").addEventListener("click", () => stop());

async function sourceRaster(file, maxSide) {
  if (!file || file.size > 25_000_000) throw new Error("Gambar belum dipilih atau ukuran >25 MB.");
  const bitmap = await createImageBitmap(file);
  try {
    if (bitmap.width < 1 || bitmap.height < 1 ||
        bitmap.width * bitmap.height > 12_000_000)
      throw new Error("Input harus berukuran 1–12 MP untuk melindungi memori HP.");
    const factor = Math.min(1, maxSide / Math.max(bitmap.width, bitmap.height));
    const w = Math.max(1, Math.round(bitmap.width * factor));
    const h = Math.max(1, Math.round(bitmap.height * factor));
    const canvas = document.createElement("canvas");
    canvas.width = w; canvas.height = h;
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    if (!ctx) throw new Error("Canvas browser tidak tersedia.");
    ctx.drawImage(bitmap, 0, 0, w, h);
    const data = ctx.getImageData(0, 0, w, h).data;
    return { width: w, height: h, pixels: new Uint8Array(data), originalWidth: bitmap.width,
      originalHeight: bitmap.height, sourceFile: file };
  } finally { bitmap.close(); }
}
async function decodeSVG(file) {
  if (!file) return null;
  if (file.size > maxSvgBytes) throw new Error(file.name + ": file SVG >12 MB.");
  const svg = await file.text();
  if (!/<svg[\s>]/i.test(svg) || /<script\b|<foreignObject\b|\bon\w+\s*=|<!ENTITY\b/i.test(svg))
    throw new Error(file.name + ": bukan SVG aman untuk dibandingkan.");
  return svg;
}
function runRust(raster, engine, token) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const worker = new Worker("./covecto-worker.js");
    activeWorker = worker;
    const timeout = setTimeout(() => finish(new Error("Rust melebihi 120 detik; worker dihentikan.")), 120_000);
    const cancelThis = err => finish(err);
    function finish(err, value) {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      worker.terminate();
      if (activeWorker === worker) activeWorker = null;
      if (activeReject === cancelThis) activeReject = null;
      if (err) reject(err);
      else resolve(value);
    }
    activeReject = cancelThis;
    worker.onmessage = ({ data }) => {
      if (!data || token !== currentToken) { finish(new Error("Proses dibatalkan.")); return; }
      if (data.type === "error") finish(new Error(data.message || "WASM error"));
      else if (data.type === "result") finish(null, data);
    };
    worker.onerror = event => finish(new Error(event.message || "Worker Rust gagal."));
    const pixels = raster.pixels.slice(); // clone; worker owns transfer
    worker.postMessage({ type: "vectorize", engine, width: raster.width,
      height: raster.height, pixels }, [pixels.buffer]);
  });
}
async function asImage(svg, width, height) {
  const blob = new Blob([svg], { type: "image/svg+xml;charset=utf-8" });
  const tempURL = URL.createObjectURL(blob);
  try {
    const img = new Image();
    img.decoding = "async";
    img.src = tempURL;
    await new Promise((resolve, reject) => {
      img.onload = resolve;
      img.onerror = () => reject(new Error("SVG gagal dirender di browser."));
    });
    const canvas = document.createElement("canvas");
    canvas.width = width; canvas.height = height;
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    if (!ctx) throw new Error("Canvas untuk scoring tidak tersedia.");
    ctx.fillStyle = "#ffffff";
    ctx.fillRect(0, 0, width, height);
    ctx.drawImage(img, 0, 0, width, height);
    return ctx.getImageData(0, 0, width, height).data;
  } finally { URL.revokeObjectURL(tempURL); }
}
async function scoredInput(raster) {
  const w = Math.max(1, Math.round(raster.originalWidth *
    Math.min(1, 512 / Math.max(raster.originalWidth, raster.originalHeight))));
  const h = Math.max(1, Math.round(raster.originalHeight *
    Math.min(1, 512 / Math.max(raster.originalWidth, raster.originalHeight))));
  const canvas = document.createElement("canvas");
  canvas.width = w; canvas.height = h;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  const img = await createImageBitmap(raster.sourceFile);
  try {
    ctx.fillStyle = "#ffffff"; ctx.fillRect(0, 0, w, h);
    ctx.drawImage(img, 0, 0, w, h);
    return { width: w, height: h, rgba: ctx.getImageData(0, 0, w, h).data };
  } finally { img.close(); }
}
function pixelExactSafe(raster, sourceFile) {
  // Exact engine has catastrophic path counts on smooth JPEGs.
  if (raster.width * raster.height > 65_536 || sourceFile.type === "image/jpeg")
    return false;
  const sampled = new Set(), px = raster.pixels, step = Math.max(1, Math.floor(px.length / (4 * 8192)));
  for (let i = 0; i < raster.width * raster.height; i += step) {
    const k = i * 4;
    sampled.add(((px[k] >> 3) << 15) | ((px[k + 1] >> 3) << 10) |
      ((px[k + 2] >> 3) << 5) | (px[k + 3] < 128 ? 1 : 0));
    if (sampled.size > 32) return false;
  }
  return true;
}
function lineCell(row, text) {
  const cell = document.createElement("td");
  cell.textContent = text;
  row.appendChild(cell);
}
function number(value, digits = 2) {
  return Number.isFinite(value) ? value.toFixed(digits) : "—";
}
function showResults(ranked, sourceFile, originalDims, scoreDims) {
  releaseURLs();
  $("output").hidden = false;
  $("winner").textContent = "Kandidat terbaik menurut skor lokal: " + ranked[0].name +
    " — nilai " + number(ranked[0].loss, 3);
  const tbody = $("metrics").querySelector("tbody");
  tbody.replaceChildren();
  const previews = $("previews");
  previews.replaceChildren();
  for (const c of ranked) {
    const tr = document.createElement("tr");
    const m = c.metrics, s = c.stats;
    [c.name, number(m.mae, 3), number(m.localSsim8, 5),
     number(100 * m.darkMaskIou, 2) + "%",
     m.accentMae == null ? "—" : number(m.accentMae, 2),
     String(s.paths), String(s.subpaths), String(s.commandTokens),
     number(s.bytes / 1024, 1) + " KiB",
     c.timeMs == null ? "—" : number(c.timeMs / 1000, 1) + " dtk",
     number(c.loss, 3)].forEach(t => lineCell(tr, t));
    tbody.appendChild(tr);
    const card = document.createElement("div");
    card.className = "preview";
    const img = document.createElement("img");
    const url = objectURL(new Blob([c.svg], { type: "image/svg+xml;charset=utf-8" }));
    img.src = url; img.alt = c.name;
    const label = document.createElement("strong"); label.textContent = c.name;
    const link = document.createElement("a");
    link.textContent = "Unduh SVG · " + s.paths + " path";
    link.href = url; link.download = "dikatrace-lab-" +
      c.name.toLowerCase().replace(/[^a-z0-9]+/g, "-") + ".svg";
    card.append(img, label, link);
    previews.appendChild(card);
  }
  report = {
    source: sourceFile.name, dimensions: originalDims,
    compareResolution: scoreDims,
    metricNotes: "White compositing; block-local SSIM 8x8; dark-mask IoU; accent pixels = rare chromatic source bins; command counts may miss implicit repetition.",
    rule: "MAE + 8*(1-localSSIM8) + 0.12*accentMAE; near-ties (<0.15) break by SVG command tokens",
    candidates: ranked.map(({ svg, ...other }) => other),
    winner: ranked[0].name
  };
  $("export").disabled = false;
}
$("export").addEventListener("click", () => {
  if (!report) return;
  const url = objectURL(new Blob([JSON.stringify(report, null, 2)],
    { type: "application/json" }));
  const a = document.createElement("a");
  a.href = url; a.download = "dikatrace-hybrid-lab-results.json";
  a.click();
});
$("start").addEventListener("click", async () => {
  const file = $("input").files[0];
  if (!file) { status("Pilih gambar PNG/JPG asli dulu."); return; }
  const token = ++currentToken;
  busy(true); $("output").hidden = true; $("export").disabled = true;
  try {
    status("Membaca gambar asli...");
    const raster = await sourceRaster(file, Number($("maxSide").value));
    assertActive(token);
    const scoreSource = await scoredInput(raster);
    assertActive(token);
    const candidates = [];
    for (const id of inputs) {
      const uploaded = $(id).files[0];
      if (!uploaded) continue;
      const svg = await decodeSVG(uploaded);
      assertActive(token);
      candidates.push({ name: readable[id], svg, timeMs: null, generator: "uploaded" });
    }
    const engines = $("engine").value === "both" ? ["spline", "pixel-exact"] : ["spline"];
    if (engines.includes("pixel-exact") && !pixelExactSafe(raster, file))
      throw new Error("PixelExact hanya untuk ikon PNG flat <=256×256 dengan <=32 warna sample. Pilih Spline untuk JPG harimau.");
    for (const engine of engines) {
      assertActive(token);
      status("Rust WASM " + engine + " memproses " + raster.width + "×" + raster.height +
        " px di Worker; bisa dibatalkan...");
      const res = await runRust(raster, engine, token);
      assertActive(token);
      candidates.push({ name: "Rust " + res.engine, svg: res.svg,
        timeMs: res.ms, generator: "covecto-0.3.0" });
    }
    for (const c of candidates) {
      assertActive(token);
      status("Mengukur " + c.name + " terhadap input asli...");
      const output = await asImage(c.svg, scoreSource.width, scoreSource.height);
      c.metrics = qualityMetrics(scoreSource.rgba, output, scoreSource.width, scoreSource.height);
      c.stats = svgStats(c.svg);
    }
    assertActive(token);
    const { ranked } = chooseBest(candidates);
    showResults(ranked, file, {
      width: raster.originalWidth, height: raster.originalHeight
    }, { width: scoreSource.width, height: scoreSource.height });
    status("Selesai. Hasil diurutkan berdasarkan kemiripan warna + struktur + warna aksen; inspeksi visual tetap wajib.");
  } catch (err) {
    if (token === currentToken) status("Gagal: " + (err?.message || err));
  } finally {
    if (token === currentToken) busy(false);
  }
});
