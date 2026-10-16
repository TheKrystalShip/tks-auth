// Node ships its own `localStorage` global, which shadows the DOM environment's. The package reads
// the browser's, so the environment's stands in for it.
const dom = globalThis.jsdom?.window;
if (dom) {
  for (const name of ["localStorage", "sessionStorage"]) {
    Object.defineProperty(globalThis, name, { value: dom[name], configurable: true, writable: true });
  }
}
