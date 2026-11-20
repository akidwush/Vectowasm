// Independent, deterministic fidelity metrics shared by browser and Node CI.
// Both images are precomposited onto white at exactly the same dimensions.
export function qualityMetrics(source, output, width, height) {
  const n = width * height;
  if (width < 1 || height < 1 || !Number.isSafeInteger(n) ||
      source.length !== n * 4 || output.length !== n * 4)
    throw new RangeError("Input and output RGBA dimensions must match.");
  const a = new Float32Array(n), b = new Float32Array(n);
  const bins = new Uint32Array(4096);
  for (let i = 0; i < n; i++) {
    const k = 4 * i;
    bins[((source[k] >> 4) << 8) | ((source[k + 1] >> 4) << 4) |
      (source[k + 2] >> 4)]++;
  }
  const rareLimit = Math.max(8, Math.ceil(n * 0.0075));
  let abs = 0, strong = 0, inter = 0, union = 0;
  let accentAbs = 0, accentPixels = 0;
  for (let i = 0; i < n; i++) {
    const k = i * 4, sr = source[k], sg = source[k + 1], sb = source[k + 2];
    const dr = output[k], dg = output[k + 1], db = output[k + 2];
    const e = Math.abs(sr - dr) + Math.abs(sg - dg) + Math.abs(sb - db);
    abs += e;
    if (Math.max(Math.abs(sr - dr), Math.abs(sg - dg), Math.abs(sb - db)) >= 20)
      strong++;
    const sl = .2126 * sr + .7152 * sg + .0722 * sb;
    const dl = .2126 * dr + .7152 * dg + .0722 * db;
    a[i] = sl; b[i] = dl;
    const si = sl < 128, di = dl < 128;
    if (si && di) inter++;
    if (si || di) union++;
    const min = Math.min(sr, sg, sb), max = Math.max(sr, sg, sb);
    const bin = ((sr >> 4) << 8) | ((sg >> 4) << 4) | (sb >> 4);
    // Diagnostic for small, chromatic accents: yellow eyes, red marks etc.
    // Not a ground-truth object classifier; JPEG fringes may also be counted.
    if (max > 65 && max - min >= 55 && bins[bin] <= rareLimit) {
      accentAbs += e;
      accentPixels++;
    }
  }
  // Block-local SSIM (8x8, population covariance), explicitly not
  // equivalent to gaussian-window skimage/standard SSIM.
  const c1 = (.01 * 255) ** 2, c2 = (.03 * 255) ** 2;
  let total = 0, blocks = 0;
  for (let y0 = 0; y0 < height; y0 += 8) {
    for (let x0 = 0; x0 < width; x0 += 8) {
      let sx = 0, sy = 0, sx2 = 0, sy2 = 0, sxy = 0, count = 0;
      for (let y = y0; y < Math.min(y0 + 8, height); y++)
        for (let x = x0; x < Math.min(x0 + 8, width); x++) {
          const i = y * width + x, x1 = a[i], y1 = b[i];
          sx += x1; sy += y1; sx2 += x1 * x1;
          sy2 += y1 * y1; sxy += x1 * y1; count++;
        }
      const mx = sx / count, my = sy / count;
      const vx = Math.max(0, sx2 / count - mx * mx);
      const vy = Math.max(0, sy2 / count - my * my);
      const cov = sxy / count - mx * my;
      total += ((2 * mx * my + c1) * (2 * cov + c2)) /
        ((mx * mx + my * my + c1) * (vx + vy + c2));
      blocks++;
    }
  }
  return {
    mae: abs / (n * 3),
    localSsim8: total / blocks,
    darkMaskIou: union ? inter / union : 1,
    strongPixels: strong,
    strongFraction: strong / n,
    accentMae: accentPixels ? accentAbs / (3 * accentPixels) : null,
    accentPixels
  };
}

export function svgStats(svg) {
  if (typeof svg !== "string" || !/<svg[\s>]/i.test(svg))
    throw new Error("Not an SVG document.");
  const paths = [...svg.matchAll(/<path\b[^>]*>/gi)];
  let subpaths = 0, curves = 0, lines = 0, other = 0;
  for (const match of paths) {
    const tag = match[0];
    const d = /\bd\s*=\s*(["'])([\s\S]*?)\1/i.exec(tag);
    if (!d) continue;
    for (const command of d[2].match(/[MmLlHhVvCcSsQqTtAaZz]/g) || []) {
      if (/[Mm]/.test(command)) subpaths++;
      else if (/[CcSs]/.test(command)) curves++;
      else if (/[LlHhVv]/.test(command)) lines++;
      else if (!/[Zz]/.test(command)) other++;
    }
  }
  return {
    paths: paths.length, subpaths, curves, lines, other,
    // Caveat: repeated coordinate groups can imply commands without another
    // letter. Count is SVG command tokens, NOT exact geometric segments.
    commandTokens: curves + lines + other,
    bytes: new TextEncoder().encode(svg).length
  };
}

export function candidateLoss(m) {
  if (!m || !Number.isFinite(m.mae) || !Number.isFinite(m.localSsim8))
    throw new Error("Unscored SVG candidate.");
  // Fidelity-first: lower is better. Accent details matter without letting
  // tiny JPEG fringes dominate the image's overall visual match.
  return m.mae + 8 * (1 - m.localSsim8) +
    .12 * (m.accentMae ?? 0);
}

export function chooseBest(candidates) {
  if (!Array.isArray(candidates) || candidates.length === 0)
    throw new RangeError("At least one measured candidate required.");
  // Complexity breaks only near-ties in visual fidelity.
  const ranked = [...candidates].map(c => ({
    ...c, loss: candidateLoss(c.metrics)
  })).sort((a, b) => {
    if (Math.abs(a.loss - b.loss) > .15) return a.loss - b.loss;
    if (a.stats.commandTokens !== b.stats.commandTokens)
      return a.stats.commandTokens - b.stats.commandTokens;
    return a.name.localeCompare(b.name);
  });
  return { best: ranked[0], ranked };
}
