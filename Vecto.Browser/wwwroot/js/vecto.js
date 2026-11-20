"use strict";
window.vecto = {
  createUrl(bytes, mime) {
    return URL.createObjectURL(new Blob([bytes], { type: mime }));
  },
  afterPaint() {
    // Give the browser two frames to present the newly chosen bitmap.
    return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  },
  revoke(url) {
    if (typeof url === "string" && url.startsWith("blob:")) URL.revokeObjectURL(url);
  },
  async decodeRaster(bytes) {
    const bitmap = await createImageBitmap(new Blob([bytes]), {
      colorSpaceConversion: "none",
      premultiplyAlpha: "none"
    });
    try {
      const width = bitmap.width, height = bitmap.height;
      if (!width || !height || width * height > 8000000)
        throw new Error("Image exceeds 8 megapixels.");
      const canvas = document.createElement("canvas");
      canvas.width = width;
      canvas.height = height;
      const ctx = canvas.getContext("2d", { willReadFrequently: true });
      if (!ctx) throw new Error("Canvas 2D is not available.");
      ctx.drawImage(bitmap, 0, 0);
      const pixels = ctx.getImageData(0, 0, width, height).data;
      const output = new Uint8Array(8 + pixels.byteLength);
      const header = new DataView(output.buffer);
      header.setInt32(0, width, true);
      header.setInt32(4, height, true);
      output.set(pixels, 8);
      return output;
    } finally {
      bitmap.close();
    }
  },
  async measureSvgPair(inputUrl, originalSvgUrl, v3SvgUrl) {
    // Source-relative comparison at a fixed downsampled resolution. This does not
    // affect exported SVG dimensions or the engine's full-resolution tracing.
    async function bitmap(url) {
      const response = await fetch(url);
      if (!response.ok) throw new Error("Could not read preview image.");
      return createImageBitmap(await response.blob());
    }
    const source = await bitmap(inputUrl);
    const scale = Math.min(1, 1024 / Math.max(source.width, source.height));
    const width = Math.max(1, Math.round(source.width * scale));
    const height = Math.max(1, Math.round(source.height * scale));
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    if (!ctx) {
      source.close();
      throw new Error("Canvas 2D unavailable.");
    }
    function paint(image) {
      ctx.clearRect(0, 0, width, height);
      ctx.fillStyle = "#ffffff";
      ctx.fillRect(0, 0, width, height);
      ctx.drawImage(image, 0, 0, width, height);
      return ctx.getImageData(0, 0, width, height).data;
    }
    let originalPixels;
    try {
      originalPixels = paint(source);
    } finally {
      source.close();
    }

    function score(vectorPixels) {
      const n = width * height;
      const a = new Float32Array(n);
      const b = new Float32Array(n);
      let intersection = 0, union = 0, wrong = 0, absoluteDifference = 0;
      for (let i = 0; i < n; i++) {
        const k = i * 4;
        // Canvas is composited against white, so alpha is fully opaque.
        const x = .2126 * originalPixels[k] + .7152 * originalPixels[k + 1] + .0722 * originalPixels[k + 2];
        const y = .2126 * vectorPixels[k] + .7152 * vectorPixels[k + 1] + .0722 * vectorPixels[k + 2];
        a[i] = x;
        b[i] = y;
        const sourceInk = x < 128, outputInk = y < 128;
        if (sourceInk && outputInk) intersection++;
        if (sourceInk || outputInk) union++;
        if (sourceInk !== outputInk) wrong++;
        absoluteDifference +=
          Math.abs(originalPixels[k] - vectorPixels[k]) +
          Math.abs(originalPixels[k + 1] - vectorPixels[k + 1]) +
          Math.abs(originalPixels[k + 2] - vectorPixels[k + 2]);
      }
      // Local luminance SSIM, non-overlapping 8x8 boxes, 8-bit C1/C2.
      // This is deliberately labeled differently from skimage's Gaussian SSIM.
      const c1 = (0.01 * 255) ** 2, c2 = (0.03 * 255) ** 2;
      let sumSsim = 0, blockCount = 0;
      for (let top = 0; top < height; top += 8) {
        for (let left = 0; left < width; left += 8) {
          let sx = 0, sy = 0, sx2 = 0, sy2 = 0, sxy = 0, count = 0;
          for (let row = top; row < Math.min(height, top + 8); row++) {
            for (let col = left; col < Math.min(width, left + 8); col++) {
              const index = row * width + col, x = a[index], y = b[index];
              sx += x; sy += y; sx2 += x * x; sy2 += y * y; sxy += x * y; count++;
            }
          }
          const mx = sx / count, my = sy / count;
          const vx = Math.max(0, sx2 / count - mx * mx);
          const vy = Math.max(0, sy2 / count - my * my);
          const cov = sxy / count - mx * my;
          sumSsim += ((2 * mx * my + c1) * (2 * cov + c2)) /
            ((mx * mx + my * my + c1) * (vx + vy + c2));
          blockCount++;
        }
      }
      return {
        iou: union ? intersection / union : 1,
        ssim: sumSsim / blockCount,
        wrong,
        mae: absoluteDifference / (n * 3)
      };
    }

    async function vectorScore(url) {
      const image = await bitmap(url);
      try {
        return score(paint(image));
      } finally {
        image.close();
      }
    }
    return {
      width,
      height,
      original: await vectorScore(originalSvgUrl),
      v3: await vectorScore(v3SvgUrl)
    };
  }
};
