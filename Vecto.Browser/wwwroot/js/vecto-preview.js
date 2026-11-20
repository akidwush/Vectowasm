/* DikaTrace Before/After preview — pointer-first, composite-only, no raster reprocessing.
 * One full-resolution source image and one SVG image share exactly the same
 * CSS transform. A clip-path reveals the second image at the draggable split.
 * No canvas allocation, PNG regeneration or SVG serialization per gesture.
 */
(function () {
  "use strict";
  const MAX_ZOOM = 1000;
  const MIN_ZOOM = 1;
  const instances = new WeakMap();

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }
  function zoomFromSlider(value) {
    return Math.exp(clamp(value, 0, 1000) / 1000 * Math.log(MAX_ZOOM));
  }
  function sliderFromZoom(zoom) {
    return clamp(Math.log(clamp(zoom, MIN_ZOOM, MAX_ZOOM)) / Math.log(MAX_ZOOM) * 1000, 0, 1000);
  }
  function panBounds(state, width, height) {
    const maxX = Math.max(0, width * (state.zoom - 1) / 2);
    const maxY = Math.max(0, height * (state.zoom - 1) / 2);
    state.x = clamp(state.x, -maxX, maxX);
    state.y = clamp(state.y, -maxY, maxY);
    if (state.zoom <= 1) state.x = state.y = 0;
    return state;
  }
  function zoomAt(state, nextZoom, cx, cy, width, height) {
    const from = state.zoom;
    const to = clamp(nextZoom, MIN_ZOOM, MAX_ZOOM);
    const ratio = to / from;
    // Preserve the same image point beneath the cursor / pinch centroid.
    state.x = cx - width / 2 - (cx - width / 2 - state.x) * ratio;
    state.y = cy - height / 2 - (cy - height / 2 - state.y) * ratio;
    state.zoom = to;
    return panBounds(state, width, height);
  }
  function zoomLabel(value) {
    if (value < 10) return value.toFixed(value >= 5 ? 1 : 2).replace(/0+$/, "").replace(/\.$/, "") + "×";
    if (value < 100) return value.toFixed(1).replace(/\.0$/, "") + "×";
    return Math.round(value) + "×";
  }
  function measure(root) {
    const el = root.querySelector("[data-preview-viewport]");
    return el ? el.getBoundingClientRect() : null;
  }
  function isComparison(root) {
    return root.dataset.comparison === "true";
  }

  function mount(root) {
    if (!root) return;
    let state = instances.get(root);
    if (state) {
      state.comparison = isComparison(root);
      render(state);
      return;
    }
    const abort = new AbortController();
    state = {
      root, abort, zoom: 1, x: 0, y: 0, split: 50,
      comparison: isComparison(root), frame: 0,
      pointers: new Map(), dragDivider: null, gesture: null,
      observer: null
    };
    instances.set(root, state);

    function viewportRect() {
      return measure(root);
    }
    function reset() {
      state.zoom = 1;
      state.x = state.y = 0;
      schedule(state);
    }
    function changeZoom(zoom, clientX, clientY) {
      const rect = viewportRect();
      if (!rect) return;
      zoomAt(state, zoom, clientX - rect.left, clientY - rect.top, rect.width, rect.height);
      schedule(state);
    }
    function zoomCenter(zoom) {
      const rect = viewportRect();
      if (!rect) return;
      changeZoom(zoom, rect.left + rect.width / 2, rect.top + rect.height / 2);
    }
    function setSplit(clientX) {
      const rect = viewportRect();
      if (!rect) return;
      state.split = clamp(100 * (clientX - rect.left) / Math.max(1, rect.width), 0, 100);
      schedule(state);
    }
    function gestureFromPointers() {
      const pts = [...state.pointers.values()];
      if (pts.length < 2) return null;
      const [a, b] = pts;
      return {
        midX: (a.x + b.x) / 2,
        midY: (a.y + b.y) / 2,
        distance: Math.max(2, Math.hypot(a.x - b.x, a.y - b.y)),
        zoom: state.zoom, x: state.x, y: state.y
      };
    }
    function onPointerDown(event) {
      const view = event.target.closest?.("[data-preview-viewport]");
      if (!view || !root.contains(view)) return;
      if (event.pointerType === "mouse" && event.button !== 0) return;
      const handle = event.target.closest?.("[data-preview-handle]");
      if (handle && state.comparison && state.dragDivider === null) {
        state.dragDivider = event.pointerId;
        setSplit(event.clientX);
      } else if (state.dragDivider === null) {
        state.pointers.set(event.pointerId, {
          x: event.clientX, y: event.clientY,
          startX: event.clientX, startY: event.clientY,
          baseX: state.x, baseY: state.y
        });
        state.gesture = gestureFromPointers();
      }
      try { view.setPointerCapture(event.pointerId); } catch (_) {}
      event.preventDefault();
    }
    function onPointerMove(event) {
      if (state.dragDivider === event.pointerId) {
        setSplit(event.clientX);
        event.preventDefault();
        return;
      }
      const pointer = state.pointers.get(event.pointerId);
      if (!pointer) return;
      pointer.x = event.clientX;
      pointer.y = event.clientY;
      const rect = viewportRect();
      if (!rect) return;
      if (state.pointers.size >= 2) {
        if (!state.gesture) state.gesture = gestureFromPointers();
        const snapshot = state.gesture;
        const pts = [...state.pointers.values()];
        const [a, b] = pts;
        const midX = (a.x + b.x) / 2;
        const midY = (a.y + b.y) / 2;
        const dist = Math.max(2, Math.hypot(a.x - b.x, a.y - b.y));
        const ratio = clamp(snapshot.zoom * dist / snapshot.distance,
          MIN_ZOOM, MAX_ZOOM) / snapshot.zoom;
        state.zoom = snapshot.zoom * ratio;
        const startX = snapshot.midX - rect.left - rect.width / 2;
        const startY = snapshot.midY - rect.top - rect.height / 2;
        const endX = midX - rect.left - rect.width / 2;
        const endY = midY - rect.top - rect.height / 2;
        state.x = endX - (startX - snapshot.x) * ratio;
        state.y = endY - (startY - snapshot.y) * ratio;
      } else {
        state.x = pointer.baseX + pointer.x - pointer.startX;
        state.y = pointer.baseY + pointer.y - pointer.startY;
      }
      panBounds(state, rect.width, rect.height);
      schedule(state);
      event.preventDefault();
    }
    function onPointerUp(event) {
      if (state.dragDivider === event.pointerId) state.dragDivider = null;
      if (state.pointers.delete(event.pointerId)) {
        state.gesture = gestureFromPointers();
        // Rebase remaining finger: continuing a pan after pinch must not jump.
        if (state.pointers.size === 1) {
          const remaining = [...state.pointers.values()][0];
          remaining.startX = remaining.x;
          remaining.startY = remaining.y;
          remaining.baseX = state.x;
          remaining.baseY = state.y;
        }
      }
    }
    function onWheel(event) {
      if (!event.target.closest?.("[data-preview-viewport]")) return;
      event.preventDefault();
      const delta = clamp(event.deltaY, -120, 120);
      changeZoom(state.zoom * Math.exp(-delta * .008), event.clientX, event.clientY);
    }
    function onDoubleClick(event) {
      if (!event.target.closest?.("[data-preview-viewport]")) return;
      event.preventDefault();
      changeZoom(state.zoom < 3 ? 4 : 1, event.clientX, event.clientY);
    }
    function onKeyDown(event) {
      if (event.target.matches?.("[data-preview-handle]") && state.comparison) {
        const step = event.shiftKey ? 10 : 1;
        if (event.key === "ArrowLeft" || event.key === "ArrowDown")
          state.split = clamp(state.split - step, 0, 100);
        else if (event.key === "ArrowRight" || event.key === "ArrowUp")
          state.split = clamp(state.split + step, 0, 100);
        else if (event.key === "Home") state.split = 0;
        else if (event.key === "End") state.split = 100;
        else return;
        schedule(state);
        event.preventDefault();
        return;
      }
      if (!event.target.matches?.("[data-preview-viewport]")) return;
      if (event.key === "+" || event.key === "=") zoomCenter(state.zoom * 1.6);
      else if (event.key === "-" || event.key === "_") zoomCenter(state.zoom / 1.6);
      else if (event.key === "0") reset();
      else if (event.key.startsWith("Arrow")) {
        const rect = viewportRect();
        if (!rect) return;
        const step = event.shiftKey ? 70 : 24;
        state.x += event.key === "ArrowRight" ? -step : event.key === "ArrowLeft" ? step : 0;
        state.y += event.key === "ArrowDown" ? -step : event.key === "ArrowUp" ? step : 0;
        panBounds(state, rect.width, rect.height);
        schedule(state);
      } else return;
      event.preventDefault();
    }
    function onClick(event) {
      const button = event.target.closest?.("button");
      if (!button || !root.contains(button)) return;
      if (button.hasAttribute("data-preview-reset")) reset();
      else if (button.hasAttribute("data-preview-zoom-in")) zoomCenter(state.zoom * 1.6);
      else if (button.hasAttribute("data-preview-zoom-out")) zoomCenter(state.zoom / 1.6);
      else if (button.hasAttribute("data-preview-max")) zoomCenter(MAX_ZOOM);
    }
    function onInput(event) {
      if (event.target.matches?.("[data-preview-zoom-range]"))
        zoomCenter(zoomFromSlider(Number(event.target.value)));
      else if (event.target.matches?.("[data-preview-split-range]")) {
        state.split = clamp(Number(event.target.value), 0, 100);
        schedule(state);
      }
    }
    const signal = abort.signal;
    root.addEventListener("pointerdown", onPointerDown, { signal });
    root.addEventListener("pointermove", onPointerMove, { signal });
    root.addEventListener("pointerup", onPointerUp, { signal });
    root.addEventListener("pointercancel", onPointerUp, { signal });
    root.addEventListener("lostpointercapture", onPointerUp, { signal });
    root.addEventListener("wheel", onWheel, { passive: false, signal });
    root.addEventListener("dblclick", onDoubleClick, { signal });
    root.addEventListener("keydown", onKeyDown, { signal });
    root.addEventListener("click", onClick, { signal });
    root.addEventListener("input", onInput, { signal });
    if (typeof ResizeObserver !== "undefined") {
      state.observer = new ResizeObserver(() => {
        const rect = viewportRect();
        if (rect) panBounds(state, rect.width, rect.height);
        schedule(state);
      });
      state.observer.observe(root);
    }
    render(state);
  }
  function schedule(state) {
    if (state.frame) return; // At most one compositor update per animation frame.
    state.frame = requestAnimationFrame(() => {
      state.frame = 0;
      render(state);
    });
  }
  function render(state) {
    const root = state.root;
    const rect = measure(root);
    if (!rect) return;
    panBounds(state, rect.width, rect.height);
    root.style.setProperty("--preview-zoom", String(state.zoom));
    root.style.setProperty("--preview-pan-x", state.x.toFixed(2) + "px");
    root.style.setProperty("--preview-pan-y", state.y.toFixed(2) + "px");
    root.style.setProperty("--preview-split", state.split.toFixed(2) + "%");
    const label = root.querySelector("[data-preview-reset]");
    if (label) label.textContent = zoomLabel(state.zoom) + " · Pas";
    const zoomRange = root.querySelector("[data-preview-zoom-range]");
    if (zoomRange) {
      zoomRange.value = String(Math.round(sliderFromZoom(state.zoom)));
      zoomRange.setAttribute("aria-valuetext", zoomLabel(state.zoom));
    }
    const splitRange = root.querySelector("[data-preview-split-range]");
    if (splitRange) splitRange.value = String(Math.round(state.split));
    const splitLabel = root.querySelector("[data-preview-split-label]");
    if (splitLabel) splitLabel.textContent = Math.round(state.split) + "%";
    const handle = root.querySelector("[data-preview-handle]");
    if (handle) {
      handle.setAttribute("aria-valuenow", String(Math.round(state.split)));
      handle.setAttribute("aria-valuetext",
        Math.round(state.split) + " persen Before, " +
        Math.round(100 - state.split) + " persen After");
    }
    const out = root.querySelector("[data-preview-zoom-out]");
    if (out && !out.hasAttribute("disabled")) out.disabled = state.zoom <= MIN_ZOOM;
    const plus = root.querySelector("[data-preview-zoom-in]");
    if (plus && !plus.hasAttribute("disabled")) plus.disabled = state.zoom >= MAX_ZOOM;
  }
  function dispose(root) {
    const state = instances.get(root);
    if (!state) return;
    if (state.frame) cancelAnimationFrame(state.frame);
    state.abort.abort();
    state.observer?.disconnect();
    state.pointers.clear();
    instances.delete(root);
  }
  window.vectoPreview = {
    mount, dispose,
    // Pure math exported for deterministic Node regression tests.
    math: { clamp, zoomAt, panBounds, zoomFromSlider, sliderFromZoom, zoomLabel }
  };
})();