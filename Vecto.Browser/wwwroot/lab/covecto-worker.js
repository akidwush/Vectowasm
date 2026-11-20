"use strict";
// One benchmark per disposable worker. Terminating the worker is a real
// cancellation, including when Rust WASM is inside a long synchronous call.
self.onmessage = async ({ data }) => {
  if (!data || data.type !== "vectorize") return;
  let exports = null, pxPtr = 0, optionsPtr = 0, outPtr = 0, svgPtr = 0;
  let svgLen = 0, pixelsLen = 0, optionsLen = 0;
  try {
    const width = data.width, height = data.height, rgba = data.pixels;
    if (!Number.isInteger(width) || !Number.isInteger(height) ||
        width < 1 || height < 1 || width * height > 1_500_000 ||
        !(rgba instanceof Uint8Array) || rgba.byteLength !== width * height * 4)
      throw new Error("Input terlalu besar / dimensi RGBA tidak valid (maks. 1,5 MP).");
    const engine = data.engine;
    if (engine !== "spline" && engine !== "pixel-exact")
      throw new Error("Engine harus dipilih eksplisit; auto tidak digunakan untuk benchmark.");
    const wasmUrl = new URL("./covecto.wasm", self.location.href);
    const response = await fetch(wasmUrl);
    if (!response.ok) throw new Error("WASM belum terpasang. Gunakan artifact GitHub Actions PR #19.");
    const { instance } = await WebAssembly.instantiate(await response.arrayBuffer(), {});
    exports = instance.exports;
    if (!exports.memory || !exports.covecto_alloc || !exports.covecto_free ||
        !exports.covecto_vectorize)
      throw new Error("Covecto WASM ABI tidak cocok.");
    const optString = "engine=" + engine + "&optimize=false" +
      (engine === "spline"
        ? "&preset=poster&hierarchical=stacked&path_simplify=spline"
        : "");
    const opts = new TextEncoder().encode(optString);
    pixelsLen = rgba.byteLength; optionsLen = opts.byteLength;
    pxPtr = exports.covecto_alloc(pixelsLen);
    if (!pxPtr) throw new Error("WASM memory allocation failed.");
    new Uint8Array(exports.memory.buffer, pxPtr, pixelsLen).set(rgba);
    optionsPtr = exports.covecto_alloc(optionsLen);
    if (!optionsPtr) throw new Error("WASM options allocation failed.");
    new Uint8Array(exports.memory.buffer, optionsPtr, optionsLen).set(opts);
    outPtr = exports.covecto_alloc(12);
    if (!outPtr) throw new Error("WASM output allocation failed.");
    const t0 = performance.now();
    svgPtr = exports.covecto_vectorize(pxPtr, pixelsLen, width, height,
      optionsPtr, optionsLen, outPtr);
    const ms = performance.now() - t0;
    const info = new DataView(exports.memory.buffer, outPtr, 12);
    svgLen = info.getUint32(0, true);
    const status = info.getUint32(4, true);
    const used = info.getUint32(8, true);
    if (status !== 0 || !svgPtr || svgLen === 0)
      throw new Error("Covecto WASM failed (status " + status + ").");
    if (svgLen > 40_000_000)
      throw new Error("SVG >40 MB: dihentikan untuk melindungi memori browser.");
    const svg = new TextDecoder("utf-8", { fatal: true }).decode(
      new Uint8Array(exports.memory.buffer, svgPtr, svgLen));
    self.postMessage({ type: "result", svg, ms, engine: used === 0 ? "pixel-exact" : "spline" });
  } catch (e) {
    self.postMessage({ type: "error", message: e?.message || String(e) });
  } finally {
    if (exports) {
      try {
        if (svgPtr && svgLen) exports.covecto_free(svgPtr, svgLen);
        if (outPtr) exports.covecto_free(outPtr, 12);
        if (optionsPtr) exports.covecto_free(optionsPtr, optionsLen);
        if (pxPtr) exports.covecto_free(pxPtr, pixelsLen);
      } catch (_) { /* already terminating; the worker owns memory */ }
    }
  }
};
