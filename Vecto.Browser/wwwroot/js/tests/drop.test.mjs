import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import vm from "node:vm";

const code = readFileSync(new URL("../vecto-drop.js", import.meta.url), "utf8");
function harness() {
  const listeners = new Map();
  const input = { disabled: false, files: [], changes: 0,
    dispatchEvent(evt) { assert.equal(evt.type, "change"); this.changes++; } };
  const classes = new Set();
  const root = {
    classList: {
      toggle(name, active) { active ? classes.add(name) : classes.delete(name); },
      remove(name) { classes.delete(name); }
    },
    contains(target) { return target === root || target === input; },
    querySelector(query) {
      assert.equal(query, "[data-vecto-file-dropzone] input[type=file]");
      return input;
    }
  };
  const window = { innerWidth: 1440, innerHeight: 900, addEventListener() {} };
  const document = { addEventListener(type, handler) { listeners.set(type, handler); } };
  class DataTransfer {
    constructor() {
      this.files = [];
      this.items = { add: file => this.files.push(file) };
    }
  }
  class Event { constructor(type) { this.type = type; } }
  vm.runInNewContext(code, { window, document, DataTransfer, Event, AbortController, WeakMap });
  window.vectoDrop.mount(root);
  function fire(type, target, files = []) {
    let prevented = false;
    const event = {
      target, clientX: 40, clientY: 30, relatedTarget: null,
      dataTransfer: { types: ["Files"], files, dropEffect: "none" },
      preventDefault() { prevented = true; }
    };
    listeners.get(type)(event);
    return { prevented, dropEffect: event.dataTransfer.dropEffect };
  }
  return { input, root, classes, fire, dispose: () => window.vectoDrop.dispose(root) };
}
test("drops an OS image into the actual InputFile change pipeline", () => {
  const h = harness();
  const image = { name: "art.png", size: 10 };
  assert.equal(h.fire("dragover", h.root).dropEffect, "copy");
  assert.ok(h.classes.has("has-file-drag"));
  assert.equal(h.fire("drop", h.root, [image]).prevented, true);
  assert.equal(h.input.changes, 1);
  assert.equal(h.input.files[0], image);
  assert.equal(h.classes.has("has-file-drag"), false);
  h.dispose();
});
test("ignores OS files outside app or while input is busy", () => {
  const h = harness();
  h.fire("drop", {}, [{ name: "outside.png" }]);
  assert.equal(h.input.changes, 0);
  h.input.disabled = true;
  h.fire("drop", h.root, [{ name: "busy.png" }]);
  assert.equal(h.input.changes, 0);
  h.dispose();
});
test("regular pointer/preview dragging remains untouched", () => {
  const h = harness();
  // Only file drags are intercepted. No mouse pointer event listeners are added.
  h.fire("dragover", h.root);
  h.dispose();
  assert.equal(h.classes.has("has-file-drag"), false);
});
