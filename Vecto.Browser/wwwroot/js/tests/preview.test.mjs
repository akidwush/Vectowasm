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
test("non-square source uses real image bounds; portrait/landscape cannot pan to blank",()=>{
  const landscape={zoom:2,x:9999,y:9999,imageWidth:1200,imageHeight:200};
  m.panBounds(landscape,600,400);
  assert.equal(landscape.x,300); // fit .5, width 1200px at 2x
  assert.equal(landscape.y,0);   // height 200px < viewport, no fake pan
  const portrait={zoom:3,x:9999,y:9999,imageWidth:200,imageHeight:1200};
  m.panBounds(portrait,600,400);
  assert.equal(portrait.x,0); // image narrower than viewport
  assert.equal(portrait.y,400); // fit 1/3, height 1200px at 3x
  const reset={zoom:1,x:-5000,y:9000,imageWidth:1200,imageHeight:300};
  m.panBounds(reset,600,400);
  assert.equal(reset.x,0);
  assert.equal(reset.y,0);
});
test("viewBox crop mathematically covers visible viewport at all zoom levels",()=>{
  const s={zoom:1,x:0,y:0,imageWidth:1536,imageHeight:1536};
  const full=m.viewWindow(s,360,520);
  assert.equal(full.width,1536); // square fully covers screen width
  assert.ok(full.y<0); // gray letterbox above/below at fit is expected
  m.zoomAt(s,1000,180,260,360,520);
  const detail=m.viewWindow(s,360,520);
  assert.ok(Math.abs(detail.width-1.536)<1e-10);
  assert.ok(Math.abs(detail.height-(520/360*1.536))<1e-10);
  assert.ok(detail.x>=0 && detail.x+detail.width<=1536+1e-7);
  assert.ok(detail.y>=0 && detail.y+detail.height<=1536+1e-7);
  const previous={...s};
  m.zoomAt(s,4,180,260,360,520);
  assert.equal(s.zoom,4);
  assert.ok(Math.abs(s.x)<1e-9&&Math.abs(s.y)<1e-9);
  assert.ok(previous.zoom===1000);
});
test("high zoom cannot overshoot when pan is several million CSS pixels",()=>{
  const s={zoom:1000,x:1e9,y:-1e9,imageWidth:1536,imageHeight:1536};
  m.panBounds(s,360,520);
  const r=m.viewWindow(s,360,520);
  assert.ok(r.x>=-1e-7 && r.x+r.width<=1536+1e-7);
  assert.ok(r.y>=-1e-7 && r.y+r.height<=1536+1e-7);
});
test("rAF coalescing, fixed canvas, native SVG viewBox and safe parser",()=>{
  assert.match(code,/if\(state\.frame\)return;/);
  assert.match(code,/requestAnimationFrame\(/);
  assert.match(code,/\.setPointerCapture\(/);
  assert.match(code,/pointercancel/);
  assert.match(code,/lostpointercapture/);
  assert.match(code,/ResizeObserver/);
  assert.match(code,/context\.drawImage\(image,0,0\)/);
  assert.match(code,/svg\.setAttribute\("viewBox",next\)/);
  assert.match(code,/replace\(\/<\!DOCTYPE/); // parser strips external DTD
  assert.match(code,/createElementNS\(SVG_NS,"path"\)/);
  assert.doesNotMatch(code,/\.toDataURL\(|\.getImageData\(/);
  assert.doesNotMatch(code,/transform:translate3d|scale\(var\(--preview-zoom\)\)/);
});
