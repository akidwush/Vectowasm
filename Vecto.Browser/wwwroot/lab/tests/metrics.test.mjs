import test from "node:test";
import assert from "node:assert/strict";
import { qualityMetrics, svgStats, chooseBest, candidateLoss } from "../quality-metrics.mjs";

function bitmap(w, h, rgba = [255,255,255,255]) {
  const out = new Uint8Array(w * h * 4);
  for (let i=0;i<out.length;i+=4) out.set(rgba, i);
  return out;
}
test("identical RGB pixels: exact MAE=0, localSSIM=1, IoU=1", () => {
  const input = bitmap(16,16);
  const m = qualityMetrics(input, input, 16, 16);
  assert.equal(m.mae, 0);
  assert.ok(Math.abs(m.localSsim8-1)<1e-9);
  assert.equal(m.darkMaskIou,1);
  assert.equal(m.strongPixels,0);
  assert.equal(m.accentMae,null);
});
test("a missing small yellow accent worsens local source-relative score", () => {
  const source=bitmap(16,16), good=bitmap(16,16), lost=bitmap(16,16);
  for (let j=0;j<2;j++) for(let i=0;i<3;i++) {
    const k=(j*16+i)*4;
    source.set([235,206,18,255],k);
    good.set([235,206,18,255],k);
  }
  const exact=qualityMetrics(source,good,16,16);
  const bad=qualityMetrics(source,lost,16,16);
  assert.equal(exact.mae,0);
  assert.ok(bad.mae>0);
  assert.ok(bad.accentMae>40);
  assert.ok(bad.accentPixels>=6);
  assert.ok(candidateLoss(bad)>candidateLoss(exact));
});
test("dark mask IoU is measurable independently from color MAE", () => {
  const source=bitmap(4,4), output=bitmap(4,4);
  source.set([0,0,0,255],0);
  output.set([0,0,0,255],4);
  const m=qualityMetrics(source,output,4,4);
  assert.equal(m.darkMaskIou,0);
  assert.equal(m.strongPixels,2);
});
test("count path and command tokens without mistaking text attributes for path commands",()=>{
  const svg='<svg><path fill="#ff0000" d="M 0 0 L 1 1 C 1 2 2 3 3 4 Z" /><path d="M 1 1 H 10 V 10 Z"/></svg>';
  const x=svgStats(svg);
  assert.equal(x.paths,2);assert.equal(x.subpaths,2);
  assert.equal(x.curves,1);assert.equal(x.lines,3);
  assert.equal(x.commandTokens,4);
  assert.throws(()=>svgStats("hello"));
});
test("fidelity beats geometry size except near-tie",()=>{
  const a={name:"accurate",metrics:{mae:1,localSsim8:.99,accentMae:1},stats:{commandTokens:3000}};
  const b={name:"simpler but worse",metrics:{mae:3,localSsim8:.95,accentMae:4},stats:{commandTokens:300}};
  assert.equal(chooseBest([b,a]).best.name,"accurate");
  const c={...a,name:"same fidelity, simpler",stats:{commandTokens:300}};
  assert.equal(chooseBest([a,c]).best.name,"same fidelity, simpler");
  assert.throws(()=>chooseBest([]));
  assert.throws(()=>qualityMetrics(bitmap(1,1),bitmap(2,2),1,1));
});
