/* DikaTrace Preview V2: viewport-bounded raster canvas + native SVG viewBox.
 * Never CSS-scale a whole 8MP texture by 1000x: Chrome Android may cache a
 * low-res bitmap, skip compositor tiles and expose blank white areas.
 * Source raster is decoded once; only a FIXED screen-sized canvas is painted.
 * Vector output stays as native <svg><path> with a cropped viewBox, so changes
 * of zoom/pan rasterize actual vector paths at the current viewport scale.
 */
(function () {
  "use strict";
  const MAX_ZOOM = 1000, MIN_ZOOM = 1;
  const SVG_NS = "http://www.w3.org/2000/svg";
  const instances = new WeakMap();
  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  const zoomFromSlider = v => Math.exp(clamp(v, 0, 1000) / 1000 * Math.log(MAX_ZOOM));
  const sliderFromZoom = z => clamp(Math.log(clamp(z, 1, MAX_ZOOM)) /
    Math.log(MAX_ZOOM) * 1000, 0, 1000);
  function zoomLabel(z) {
    if (z < 10) return z.toFixed(z >= 5 ? 1 : 2).replace(/0+$/, "").replace(/\.$/, "") + "×";
    if (z < 100) return z.toFixed(1).replace(/\.0$/, "") + "×";
    return Math.round(z) + "×";
  }
  // Geometry is expressed in source-image coordinates, but pan remains in
  // CSS screen pixels so pointer-anchored zoom is deterministic and smooth.
  function fitScale(state, w, h) {
    if (!state.imageWidth || !state.imageHeight) return 1;
    return Math.min(w / state.imageWidth, h / state.imageHeight);
  }
  function panBounds(state, w, h) {
    const fit = fitScale(state, w, h);
    // Both axes clamp to actual image coverage, NOT viewport*(zoom-1)/2.
    // The old formula allowed panning entirely outside non-square images.
    const mx = state.imageWidth
      ? Math.max(0, (state.imageWidth * fit * state.zoom - w) / 2)
      : Math.max(0, w * (state.zoom - 1) / 2);
    const my = state.imageHeight
      ? Math.max(0, (state.imageHeight * fit * state.zoom - h) / 2)
      : Math.max(0, h * (state.zoom - 1) / 2);
    state.x = clamp(state.x, -mx, mx);
    state.y = clamp(state.y, -my, my);
    if (state.zoom <= 1) state.x = state.y = 0;
    return state;
  }
  function zoomAt(state, next, cx, cy, w, h) {
    const from = state.zoom, to = clamp(next, MIN_ZOOM, MAX_ZOOM);
    const ratio = to / from;
    state.x = cx - w / 2 - (cx - w / 2 - state.x) * ratio;
    state.y = cy - h / 2 - (cy - h / 2 - state.y) * ratio;
    state.zoom = to;
    return panBounds(state, w, h);
  }
  function viewWindow(state, w, h) {
    const scale = fitScale(state, w, h) * state.zoom;
    return {
      x: (state.imageWidth - w / scale) / 2 - state.x / scale,
      y: (state.imageHeight - h / scale) / 2 - state.y / scale,
      width: w / scale, height: h / scale,
      scale
    };
  }
  function rect(state) {
    return state.root.querySelector("[data-preview-viewport]")?.getBoundingClientRect() || null;
  }
  function labelError(state, message) {
    const el = state.root.querySelector("[data-preview-loading]");
    if (el) {
      el.hidden = false;
      el.textContent = message;
    }
  }
  function labelReady(state) {
    const el = state.root.querySelector("[data-preview-loading]");
    if (el) el.hidden = true;
  }
  function isComparison(root) { return root.dataset.comparison === "true"; }
  async function rasterFromUrl(url, signal) {
    const img = new Image();
    img.decoding = "async";
    const done = new Promise((resolve, reject) => {
      img.onload = () => resolve(img);
      img.onerror = () => reject(new Error("Raster JPG/PNG gagal dibaca."));
    });
    img.src = url;
    if (typeof img.decode === "function") {
      try { await img.decode(); }
      catch (_) { await done; }
    } else await done;
    if (signal.aborted) throw new DOMException("Preview diganti.", "AbortError");
    if (!img.naturalWidth || !img.naturalHeight)
      throw new Error("Resolusi raster tidak valid.");
    return { kind: "raster", image: img, width: img.naturalWidth, height: img.naturalHeight };
  }
  async function svgFromUrl(url, signal) {
    const response = await fetch(url, { signal });
    if (!response.ok) throw new Error("SVG hasil tracing tidak dapat dibaca.");
    // The engine emits a doctype with SVG 1.1 DTD. Strip it to avoid any
    // parser's external-entity behavior. Only safe <path> elements are kept.
    const source = (await response.text())
      .replace(/<\?xml[^>]*\?>/gi, "")
      .replace(/<!DOCTYPE[^>]*>/gi, "");
    const doc = new DOMParser().parseFromString(source, "image/svg+xml");
    const src = doc.documentElement;
    if (!src || src.localName !== "svg" || doc.querySelector("parsererror"))
      throw new Error("SVG tidak valid.");
    const vb = (src.getAttribute("viewBox") || "").trim().split(/[\s,]+/).map(Number);
    if (vb.length !== 4 || !vb.every(Number.isFinite) || vb[2] <= 0 || vb[3] <= 0)
      throw new Error("SVG tidak memiliki viewBox yang valid.");
    // Retain ONLY geometry/style attributes needed by SvgWriter-generated
    // SVG; never inject scripts, external images, style sheets or event attrs.
    const native = document.createElementNS(SVG_NS, "svg");
    native.setAttribute("xmlns", SVG_NS);
    native.setAttribute("preserveAspectRatio", "none");
    native.setAttribute("width", "100%");
    native.setAttribute("height", "100%");
    let count = 0;
    for (const original of src.querySelectorAll("path")) {
      const d = original.getAttribute("d");
      if (!d) continue;
      const path = document.createElementNS(SVG_NS, "path");
      path.setAttribute("d", d);
      for (const name of ["fill", "fill-rule", "opacity", "stroke", "stroke-width"]) {
        const value = original.getAttribute(name);
        // No URL-based paints, foreign objects or other external resources.
        if (value && !/url\s*\(/i.test(value)) path.setAttribute(name, value);
      }
      native.appendChild(path);
      count++;
    }
    if (!count) throw new Error("SVG tidak berisi path vektor.");
    return { kind: "vector", svg: native, vb, width: vb[2], height: vb[3] };
  }

  function clearSurface(surface) {
    if (!surface) return;
    const target = surface.querySelector("[data-preview-vector]");
    if (target) target.replaceChildren();
    const canvas = surface.querySelector("[data-preview-raster]");
    if (canvas) {
      canvas.width = 1; canvas.height = 1;
      canvas.hidden = true;
    }
  }
  function attachSurface(state, selector, source) {
    const surface = state.root.querySelector(selector);
    if (!surface) return;
    if (!source) { clearSurface(surface); return; }
    const canvas = surface.querySelector("[data-preview-raster]");
    const vector = surface.querySelector("[data-preview-vector]");
    if (source.kind === "vector") {
      if (canvas) canvas.hidden = true;
      if (vector && source.svg.parentNode !== vector)
        vector.replaceChildren(source.svg);
    } else {
      if (vector) vector.replaceChildren();
      if (canvas) canvas.hidden = false;
    }
  }
  function drawRaster(canvas, image, state, w, h, windowRect) {
    if (!canvas) return;
    const dpr = clamp(Math.min(window.devicePixelRatio || 1, 2,
      2048 / Math.max(1,w), 1536 / Math.max(1,h)), 0.25, 2);
    const cw = Math.max(1, Math.round(w * dpr));
    const ch = Math.max(1, Math.round(h * dpr));
    if (canvas.width !== cw || canvas.height !== ch) {
      canvas.width = cw; canvas.height = ch;
    }
    const context = canvas.getContext("2d", { alpha: true, desynchronized: true }) ||
      canvas.getContext("2d");
    if (!context) throw new Error("Canvas 2D tidak tersedia pada browser ini.");
    context.setTransform(1,0,0,1,0,0);
    context.clearRect(0,0,cw,ch);
    // Raster never expands to a giant compositor surface. Viewport only.
    const scaleX = windowRect.scale * state.imageWidth / image.naturalWidth;
    const scaleY = windowRect.scale * state.imageHeight / image.naturalHeight;
    context.imageSmoothingEnabled = windowRect.scale < 2;
    context.setTransform(dpr*scaleX,0,0,dpr*scaleY,
      dpr*(-windowRect.x*windowRect.scale),
      dpr*(-windowRect.y*windowRect.scale));
    context.drawImage(image,0,0);
    context.setTransform(1,0,0,1,0,0);
  }
  function drawVector(svg, vb, state, windowRect) {
    const baseW = state.imageWidth, baseH = state.imageHeight;
    const sx = vb[2] / baseW, sy = vb[3] / baseH;
    const values = [
      vb[0] + windowRect.x * sx, vb[1] + windowRect.y * sy,
      windowRect.width * sx, windowRect.height * sy
    ];
    // Keep sub-pixel precision at 1000x and avoid invalid zero viewBox.
    const next = values.map(x => Number(x.toFixed(8))).join(" ");
    if (svg.getAttribute("viewBox") !== next) svg.setAttribute("viewBox", next);
  }
  function drawSurface(state, selector, source, w, h, win) {
    if (!source) return;
    const surface = state.root.querySelector(selector);
    if (!surface) return;
    if (source.kind === "vector") {
      drawVector(source.svg, source.vb, state, win);
    } else {
      drawRaster(surface.querySelector("[data-preview-raster]"),
        source.image, state, w, h, win);
    }
  }
  function render(state) {
    const root = state.root, r = rect(state);
    if (!r) return;
    const w = r.width, h = r.height;
    if (!w || !h) return;
    if (state.imageWidth > 0) {
      panBounds(state,w,h);
      const win = viewWindow(state,w,h);
      drawSurface(state,"[data-preview-before]",state.before,w,h,win);
      if (state.comparison)
        drawSurface(state,"[data-preview-after]",state.after,w,h,win);
    }
    root.style.setProperty("--preview-split", state.split.toFixed(2)+"%");
    const label = root.querySelector("[data-preview-reset]");
    if (label) label.textContent = zoomLabel(state.zoom)+" · Pas";
    const range = root.querySelector("[data-preview-zoom-range]");
    if (range) {
      range.value = String(Math.round(sliderFromZoom(state.zoom)));
      range.setAttribute("aria-valuetext",zoomLabel(state.zoom));
    }
    const splitRange=root.querySelector("[data-preview-split-range]");
    if(splitRange) splitRange.value=String(Math.round(state.split));
    const splitLabel=root.querySelector("[data-preview-split-label]");
    if(splitLabel) splitLabel.textContent=Math.round(state.split)+"%";
    const handle=root.querySelector("[data-preview-handle]");
    if(handle){
      handle.setAttribute("aria-valuenow",String(Math.round(state.split)));
      handle.setAttribute("aria-valuetext",Math.round(state.split)+" persen Before, "+
        Math.round(100-state.split)+" persen After");
    }
    const ready=!!state.before&&!!root.querySelector("[data-preview-viewport]");
    const out=root.querySelector("[data-preview-zoom-out]");
    if(out) out.disabled=!ready||state.zoom<=MIN_ZOOM;
    const plus=root.querySelector("[data-preview-zoom-in]");
    if(plus) plus.disabled=!ready||state.zoom>=MAX_ZOOM;
  }
  function schedule(state) {
    if(state.frame)return;
    state.frame=requestAnimationFrame(()=>{
      state.frame=0;
      if(state.alive)render(state);
    });
  }
  async function load(state, beforeUrl, afterUrl, beforeVector, afterVector) {
    const signature=JSON.stringify([beforeUrl,afterUrl,beforeVector,afterVector,isComparison(state.root)]);
    state.comparison=isComparison(state.root);
    if (signature===state.signature) {
      attachSurface(state,"[data-preview-before]",state.before);
      attachSurface(state,"[data-preview-after]",state.after);
      schedule(state);
      return;
    }
    state.signature=signature;
    const version=++state.version;
    state.loadAbort?.abort();
    const controller=new AbortController();
    state.loadAbort=controller;
    if (!beforeUrl) {
      state.before=state.after=null;
      state.imageWidth=state.imageHeight=0;
      return;
    }
    labelError(state,"Memuat pratinjau…");
    try {
      const [before,after]=await Promise.all([
        beforeVector?svgFromUrl(beforeUrl,controller.signal):rasterFromUrl(beforeUrl,controller.signal),
        afterUrl
          ? (afterVector?svgFromUrl(afterUrl,controller.signal):rasterFromUrl(afterUrl,controller.signal))
          : Promise.resolve(null)
      ]);
      if(!state.alive||version!==state.version)return;
      state.before=before;
      state.after=after;
      state.imageWidth=before.width;
      state.imageHeight=before.height;
      state.zoom=1;
      state.x=state.y=0;
      state.split=50;
      attachSurface(state,"[data-preview-before]",before);
      attachSurface(state,"[data-preview-after]",after);
      // First successful frame must be rendered before removing loading UI.
      render(state);
      labelReady(state);
    }catch(error){
      if(!state.alive||version!==state.version)return;
      labelError(state,"Gagal menampilkan pratinjau: "+(error?.message||String(error)));
    }
  }
  function mount(root,beforeUrl,afterUrl,beforeVector,afterVector) {
    if(!root)return;
    let state=instances.get(root);
    if(state){
      load(state,beforeUrl,afterUrl,beforeVector,afterVector);
      return;
    }
    const abort=new AbortController();
    state={
      root,abort,alive:true,frame:0,version:0,loadAbort:null,signature:null,
      zoom:1,x:0,y:0,split:50,comparison:isComparison(root),
      imageWidth:0,imageHeight:0,before:null,after:null,
      pointers:new Map(),dragDivider:null,gesture:null,observer:null
    };
    instances.set(root,state);
    function reset(){state.zoom=1;state.x=state.y=0;schedule(state);}
    function changeZoom(next,cx,cy){
      const r=rect(state);if(!r||!state.before)return;
      zoomAt(state,next,cx-r.left,cy-r.top,r.width,r.height);schedule(state);
    }
    function zoomCenter(next){
      const r=rect(state);if(!r)return;
      changeZoom(next,r.left+r.width/2,r.top+r.height/2);
    }
    function setSplit(clientX){
      const r=rect(state);if(!r)return;
      state.split=clamp(100*(clientX-r.left)/Math.max(1,r.width),0,100);
      schedule(state);
    }
    function currentGesture(){
      const pts=[...state.pointers.values()];
      if(pts.length<2)return null;
      const [a,b]=pts;
      return{midX:(a.x+b.x)/2,midY:(a.y+b.y)/2,
        distance:Math.max(2,Math.hypot(a.x-b.x,a.y-b.y)),
        zoom:state.zoom,x:state.x,y:state.y};
    }
    function onPointerDown(e){
      const view=e.target.closest?.("[data-preview-viewport]");
      if(!view||!root.contains(view)||!state.before)return;
      if(e.pointerType==="mouse"&&e.button!==0)return;
      const handle=e.target.closest?.("[data-preview-handle]");
      if(handle&&state.comparison&&state.dragDivider===null){
        state.dragDivider=e.pointerId;setSplit(e.clientX);
      } else if(state.dragDivider===null){
        state.pointers.set(e.pointerId,{x:e.clientX,y:e.clientY,
          startX:e.clientX,startY:e.clientY,baseX:state.x,baseY:state.y});
        state.gesture=currentGesture();
      }
      try{view.setPointerCapture(e.pointerId);}catch(_){}
      e.preventDefault();
    }
    function onPointerMove(e){
      if(state.dragDivider===e.pointerId){
        setSplit(e.clientX);e.preventDefault();return;
      }
      const p=state.pointers.get(e.pointerId);if(!p)return;
      p.x=e.clientX;p.y=e.clientY;
      const r=rect(state);if(!r)return;
      if(state.pointers.size>=2){
        if(!state.gesture)state.gesture=currentGesture();
        const g=state.gesture,[a,b]=[...state.pointers.values()];
        const mx=(a.x+b.x)/2,my=(a.y+b.y)/2;
        const distance=Math.max(2,Math.hypot(a.x-b.x,a.y-b.y));
        const ratio=clamp(g.zoom*distance/g.distance,MIN_ZOOM,MAX_ZOOM)/g.zoom;
        state.zoom=g.zoom*ratio;
        const startX=g.midX-r.left-r.width/2,startY=g.midY-r.top-r.height/2;
        const endX=mx-r.left-r.width/2,endY=my-r.top-r.height/2;
        state.x=endX-(startX-g.x)*ratio;
        state.y=endY-(startY-g.y)*ratio;
      }else{
        state.x=p.baseX+p.x-p.startX;
        state.y=p.baseY+p.y-p.startY;
      }
      panBounds(state,r.width,r.height);
      schedule(state);e.preventDefault();
    }
    function onPointerUp(e){
      if(state.dragDivider===e.pointerId)state.dragDivider=null;
      if(state.pointers.delete(e.pointerId)){
        state.gesture=currentGesture();
        if(state.pointers.size===1){
          const p=[...state.pointers.values()][0];
          p.startX=p.x;p.startY=p.y;p.baseX=state.x;p.baseY=state.y;
        }
      }
    }
    function onWheel(e){
      if(!e.target.closest?.("[data-preview-viewport]"))return;
      e.preventDefault();
      changeZoom(state.zoom*Math.exp(-clamp(e.deltaY,-120,120)*.008),e.clientX,e.clientY);
    }
    function onDoubleClick(e){
      if(!e.target.closest?.("[data-preview-viewport]"))return;
      e.preventDefault();changeZoom(state.zoom<3?4:1,e.clientX,e.clientY);
    }
    function onKeyDown(e){
      if(e.target.matches?.("[data-preview-handle]")&&state.comparison){
        const step=e.shiftKey?10:1;
        if(e.key==="ArrowLeft"||e.key==="ArrowDown")state.split=clamp(state.split-step,0,100);
        else if(e.key==="ArrowRight"||e.key==="ArrowUp")state.split=clamp(state.split+step,0,100);
        else if(e.key==="Home")state.split=0;
        else if(e.key==="End")state.split=100;
        else return;
        schedule(state);e.preventDefault();return;
      }
      if(!e.target.matches?.("[data-preview-viewport]"))return;
      if(e.key==="+"||e.key==="=")zoomCenter(state.zoom*1.6);
      else if(e.key==="-"||e.key==="_")zoomCenter(state.zoom/1.6);
      else if(e.key==="0")reset();
      else if(e.key.startsWith("Arrow")){
        const r=rect(state);if(!r)return;
        const step=e.shiftKey?70:24;
        state.x+=e.key==="ArrowRight"?-step:e.key==="ArrowLeft"?step:0;
        state.y+=e.key==="ArrowDown"?-step:e.key==="ArrowUp"?step:0;
        panBounds(state,r.width,r.height);schedule(state);
      } else return;
      e.preventDefault();
    }
    function onClick(e){
      const button=e.target.closest?.("button");
      if(!button||!root.contains(button))return;
      if(button.hasAttribute("data-preview-reset"))reset();
      else if(button.hasAttribute("data-preview-zoom-in"))zoomCenter(state.zoom*1.6);
      else if(button.hasAttribute("data-preview-zoom-out"))zoomCenter(state.zoom/1.6);
      else if(button.hasAttribute("data-preview-max"))zoomCenter(MAX_ZOOM);
    }
    function onInput(e){
      if(e.target.matches?.("[data-preview-zoom-range]"))
        zoomCenter(zoomFromSlider(Number(e.target.value)));
      else if(e.target.matches?.("[data-preview-split-range]")){
        state.split=clamp(Number(e.target.value),0,100);schedule(state);
      }
    }
    const signal=abort.signal;
    root.addEventListener("pointerdown",onPointerDown,{signal});
    root.addEventListener("pointermove",onPointerMove,{signal});
    root.addEventListener("pointerup",onPointerUp,{signal});
    root.addEventListener("pointercancel",onPointerUp,{signal});
    root.addEventListener("lostpointercapture",onPointerUp,{signal});
    root.addEventListener("wheel",onWheel,{passive:false,signal});
    root.addEventListener("dblclick",onDoubleClick,{signal});
    root.addEventListener("keydown",onKeyDown,{signal});
    root.addEventListener("click",onClick,{signal});
    root.addEventListener("input",onInput,{signal});
    if(typeof ResizeObserver!=="undefined"){
      state.observer=new ResizeObserver(()=>{
        const r=rect(state);
        if(r)panBounds(state,r.width,r.height);
        schedule(state);
      });
      state.observer.observe(root);
    }
    load(state,beforeUrl,afterUrl,beforeVector,afterVector);
  }
  function dispose(root){
    const state=instances.get(root);
    if(!state)return;
    state.alive=false;
    state.version++;
    state.loadAbort?.abort();
    if(state.frame)cancelAnimationFrame(state.frame);
    state.abort.abort();
    state.observer?.disconnect();
    state.pointers.clear();
    clearSurface(root.querySelector("[data-preview-before]"));
    clearSurface(root.querySelector("[data-preview-after]"));
    instances.delete(root);
  }
  window.vectoPreview={
    mount,dispose,
    math:{clamp,zoomAt,panBounds,zoomFromSlider,sliderFromZoom,zoomLabel,viewWindow}
  };
})();