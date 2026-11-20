import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

const code = fs.readFileSync(new URL("../vecto-preview.js", import.meta.url), "utf8");
const context = {window:{},WeakMap,AbortController,console,
  requestAnimationFrame:()=>1,cancelAnimationFrame:()=>{}};
vm.runInNewContext(code,context,{filename:"vecto-preview.js"});
const m=context.window.vectoPreview.math;

test("zoom control reaches exactly 1000x and never exceeds max",()=>{
  assert.ok(Math.abs(m.zoomFromSlider(1000)-1000)<1e-7);
  assert.equal(m.zoomFromSlider(-100),1);
  assert.ok(Math.abs(m.sliderFromZoom(1000)-1000)<1e-7);
  assert.equal(m.sliderFromZoom(1),0);
  assert.ok(Math.abs(m.zoomFromSlider(m.sliderFromZoom(5))-5)<1e-6);
  assert.ok(Math.abs(m.zoomFromSlider(m.sliderFromZoom(250))-250)<1e-6);
});
test("anchored wheel/pinch zoom keeps pointer's image position fixed",()=>{
  const s={zoom:2,x:30,y:-25};
  const old={zoom:s.zoom,x:s.x,y:s.y};
  const client={x:390,y:140},w=800,h=400;
  const px=(client.x-w/2-old.x)/old.zoom;
  const py=(client.y-h/2-old.y)/old.zoom;
  m.zoomAt(s,8,client.x,client.y,w,h);
  assert.ok(Math.abs((client.x-w/2-s.x)/s.zoom-px)<1e-9);
  assert.ok(Math.abs((client.y-h/2-s.y)/s.zoom-py)<1e-9);
});
test("pan clamps correctly at fit, deep zoom, and maximum zoom",()=>{
  const s={zoom:1,x:999,y:-999};
  m.panBounds(s,800,400);
  assert.equal(s.x,0);assert.equal(s.y,0);
  m.zoomAt(s,1000,400,200,800,400);
  assert.equal(s.zoom,1000);
  s.x=9999999;s.y=-9999999;
  m.panBounds(s,800,400);
  assert.equal(s.x,399600);
  assert.equal(s.y,-199800);
  m.zoomAt(s,0.001,400,200,800,400);
  assert.equal(s.zoom,1);
  assert.equal(s.x,0);assert.equal(s.y,0);
});
test("slider labels and clamp avoid NaN in standard bounds",()=>{
  assert.equal(m.zoomLabel(1),"1×");
  assert.equal(m.zoomLabel(1000),"1000×");
  assert.equal(m.clamp(101,0,100),100);
  assert.equal(m.clamp(-1,0,100),0);
});
test("controller implements rAF coalescing and no per-gesture canvas work",()=>{
  assert.match(code,/if \(state\.frame\) return;/);
  assert.match(code,/requestAnimationFrame\(/);
  assert.match(code,/\.setPointerCapture\(/);
  assert.match(code,/pointercancel/);
  assert.match(code,/lostpointercapture/);
  assert.match(code,/ResizeObserver/);
  assert.match(code,/clip-path/); // explanatory header
  assert.doesNotMatch(code,/\.toDataURL\(|\.getImageData\(|\.drawImage\(/);
});
