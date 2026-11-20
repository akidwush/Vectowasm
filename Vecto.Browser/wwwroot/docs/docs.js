// DikaTrace Docs: local-only navigation, search, and reading progress.
const input = document.querySelector("#doc-search");
const result = document.querySelector("#search-result");
const sections = [...document.querySelectorAll("[data-section]")];
const links = [...document.querySelectorAll(".contents a")];
const indicator = document.querySelector("#reading-progress");

function clearHighlights() {
  links.forEach(link => link.classList.remove("active"));
}

function activate(id) {
  clearHighlights();
  const link = links.find(el => el.hash === "#" + id);
  if (link) link.classList.add("active");
}

function search(text) {
  const query = text.trim().toLocaleLowerCase("id");
  let shown = 0;
  for (const section of sections) {
    const match = !query || section.textContent.toLocaleLowerCase("id").includes(query);
    section.hidden = !match;
    if (match) shown++;
  }
  // Non-section CTA is helpful when browsing, but distracting when filtering.
  const cta = document.querySelector(".end-cta");
  if (cta) cta.hidden = !!query;
  result.textContent = query
    ? shown
      ? shown + " dari " + sections.length + " bagian sesuai pencarian."
      : "Tidak ada bagian cocok. Coba kata seperti palet, segmentasi, Bézier, atau unduh."
    : "Jelajahi panduan DikaTrace.";
  if (query) clearHighlights();
  return shown;
}

input?.addEventListener("input", () => search(input.value));

for (const link of links) {
  link.addEventListener("click", () => {
    // If the destination is currently filtered out, reveal all sections first.
    if (input?.value) {
      input.value = "";
      search("");
    }
    const id = link.getAttribute("href")?.slice(1);
    if (id) activate(id);
  });
}

function onScroll() {
  if (indicator) {
    const max = Math.max(1, document.documentElement.scrollHeight - window.innerHeight);
    const done = Math.min(100, Math.max(0, Math.round(window.scrollY * 100 / max)));
    indicator.style.width = done + "%";
  }
}
window.addEventListener("scroll", onScroll, { passive: true });
window.addEventListener("resize", onScroll);
onScroll();

// Observe section headings rather than using fixed page heights. Prefer the
// user-selected anchor when jumping between chapters on small screens.
if ("IntersectionObserver" in window) {
  const observer = new IntersectionObserver(entries => {
    if (input?.value.trim()) return;
    const visible = entries.filter(e => e.isIntersecting)
      .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
    if (visible[0]) activate(visible[0].target.id);
  }, { rootMargin: "-10% 0px -65% 0px", threshold: 0 });
  for (const section of sections) observer.observe(section);
}

const hash = decodeURIComponent(location.hash.replace(/^#/, ""));
if (hash && sections.some(section => section.id === hash)) activate(hash);
