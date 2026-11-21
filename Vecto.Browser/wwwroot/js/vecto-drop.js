/* Desktop file-drop bridge. Keep Blazor InputFile as the only upload pipeline:
 * its existing 20 MiB / 8 MP / extension checks run for click and drop alike.
 * Listeners are mounted once and resolve the current keyed input on each drop.
 */
"use strict";
(() => {
  const instances = new WeakMap();
  const isFileDrag = event => Array.from(event.dataTransfer?.types || []).includes("Files");

  function mount(root) {
    if (!root || instances.has(root)) return;
    const controller = new AbortController();
    const signal = controller.signal;
    const activate = on => root.classList.toggle("has-file-drag", Boolean(on));

    function onDragEnter(event) {
      if (!isFileDrag(event)) return;
      event.preventDefault();
      activate(true);
    }
    function onDragOver(event) {
      if (!isFileDrag(event)) return;
      event.preventDefault(); // required to enable OS -> browser file drops
      if (event.dataTransfer)
        event.dataTransfer.dropEffect = root.contains(event.target) ? "copy" : "none";
      activate(true);
    }
    function onDragLeave(event) {
      // Child dragleave is not a true window exit; avoid flashing the overlay.
      if (event.relatedTarget) return;
      if (event.clientX <= 0 || event.clientY <= 0 ||
          event.clientX >= window.innerWidth || event.clientY >= window.innerHeight)
        activate(false);
    }
    function onDrop(event) {
      if (!isFileDrag(event)) return;
      event.preventDefault(); // never navigate away to a dropped image
      activate(false);
      if (!root.contains(event.target)) return;
      const input = root.querySelector("[data-vecto-file-dropzone] input[type=file]");
      if (!input || input.disabled) return;
      const file = event.dataTransfer?.files?.[0];
      if (!file) return;
      // InputFile listens for 'change', captures an IBrowserFile, and calls
      // LoadFile. Replacing the file list also allows dropping the same file.
      const transfer = new DataTransfer();
      transfer.items.add(file);
      input.files = transfer.files;
      input.dispatchEvent(new Event("change", { bubbles: true }));
    }
    document.addEventListener("dragenter", onDragEnter, { signal });
    document.addEventListener("dragover", onDragOver, { signal });
    document.addEventListener("dragleave", onDragLeave, { signal });
    document.addEventListener("drop", onDrop, { signal });
    window.addEventListener("dragend", () => activate(false), { signal });
    window.addEventListener("blur", () => activate(false), { signal });
    instances.set(root, controller);
  }
  function dispose(root) {
    const controller = instances.get(root);
    if (!controller) return;
    controller.abort();
    root.classList.remove("has-file-drag");
    instances.delete(root);
  }
  window.vectoDrop = { mount, dispose };
})();
