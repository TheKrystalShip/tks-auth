// What the shell runs in every page of its site, once per page, when the page has loaded.
//
// The colour the page shows is told to the shell (TksShell.theme), which paints the strips behind the
// system bars with it and picks their icons to read on it: the body's background, else the root's,
// else the page's theme-color. The palette it shows, its root's data-theme, is told too
// (TksShell.icon), and the launcher's icon takes it where the app has one for it. It is told when the page loads and again whenever the page's theme can
// have changed (its root's or body's attributes, the theme-color, the system's light or dark), a
// moment after as well, since a theme may ease from one colour to the next.
//
// A link with a `download` attribute to a file the page made itself (a blob: or data: address) is
// saved through the shell: a WebView saves no such link, and nothing outside the page can read one.
// The click is taken before the page's default runs, the bytes are read here while the address still
// resolves, and they go to the shell as base64 with the name the link gave (TksShell.saveFile). Every
// other link is the page's own.
(function () {
  if (window.__tksShellPage || !window.TksShell || !window.TksShell.saveFile) return;
  window.__tksShellPage = true;

  function save(link) {
    var name = link.getAttribute("download") || "download";
    fetch(link.href).then(function (response) { return response.blob(); }).then(function (blob) {
      var reader = new FileReader();
      reader.onload = function () {
        var data = String(reader.result);
        window.TksShell.saveFile(name, blob.type || "", data.slice(data.indexOf(",") + 1));
      };
      reader.readAsDataURL(blob);
    });
  }

  var told = null;
  var toldPalette = null;
  function shown(element) {
    if (!element) return null;
    var colour = getComputedStyle(element).backgroundColor;
    return colour && colour !== "transparent" && !/^rgba\(.*,\s*0\)$/.test(colour) ? colour : null;
  }
  function tell() {
    var meta = document.querySelector('meta[name="theme-color"]');
    var colour = shown(document.body) || shown(document.documentElement) || (meta && meta.content) || null;
    if (colour && colour !== told && window.TksShell.theme) {
      told = colour;
      window.TksShell.theme(colour);
    }
    var palette = document.documentElement.getAttribute("data-theme");
    if (palette && palette !== toldPalette && window.TksShell.icon) {
      toldPalette = palette;
      window.TksShell.icon(palette);
    }
  }
  function soon() {
    tell();
    setTimeout(tell, 60);
    setTimeout(tell, 450);
  }
  var watching = new MutationObserver(soon);
  watching.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "class", "style"] });
  if (document.body) watching.observe(document.body, { attributes: true, attributeFilter: ["data-theme", "class", "style"] });
  watching.observe(document.head, { subtree: true, childList: true, attributes: true, attributeFilter: ["content"] });
  if (window.matchMedia) window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", soon);
  soon();

  document.addEventListener("click", function (event) {
    var link = event.target && event.target.closest ? event.target.closest("a[download]") : null;
    if (!link || !/^(blob|data):/.test(link.href)) return;
    event.preventDefault();
    save(link);
  }, true);
})();
