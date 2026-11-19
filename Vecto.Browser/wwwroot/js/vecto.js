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
  downloadUrl(url, filename) {
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
  }
};
