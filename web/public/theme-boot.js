// theme-boot.js — applies the stored theme preference before first paint.
//
// The documents are served under a policy with no inline script, so the boot script is a file of its
// own, loaded from <head> ahead of the stylesheet. It reads the preference Krystal's theme store
// keeps (localStorage "krystal:theme"), resolves "auto" through the system colour scheme and a missing
// value to Krystal's default "dark", and sets <html data-theme>. Krystal's themeStore takes over
// when the application mounts.
//
// DARK and LIGHT mirror Krystal's THEME_OPTS (id and mode) and change when the Krystal pin moves.
(function () {
  var DARK = [
  "dark", "amoled", "nord", "catppuccin-mocha", "dracula", "tokyo-night", "synthwave", "gruvbox",
  "amber-crt", "one-dark", "rose-pine", "kanagawa", "everforest", "solarized-dark", "github-dark",
  "monokai", "ayu-mirage", "matrix", "winamp", "lcars", "cyberpunk", "dos-blue", "pico8", "c64",
  "cvd-redgreen-dark", "cvd-deuteran-dark", "cvd-protan-dark", "cvd-tritan-dark",
  "cvd-universal-dark", "cvd-mono-dark"
  ];
  var LIGHT = [
  "light", "github-light", "solarized-light", "catppuccin-latte", "nord-light", "gruvbox-light",
  "everforest-light", "rose-pine-dawn", "tokyo-night-day", "one-light", "ayu-light", "win95",
  "cvd-redgreen-light", "cvd-deuteran-light", "cvd-protan-light", "cvd-tritan-light",
  "cvd-universal-light", "cvd-mono-light"
  ];
  // No stored preference is Krystal's default, "dark"; "auto" follows the system scheme.
  var theme = "dark";
  try {
    var pref = localStorage.getItem("krystal:theme");
    if (DARK.indexOf(pref) >= 0 || LIGHT.indexOf(pref) >= 0) theme = pref;
    else if (pref === "auto" && window.matchMedia && matchMedia("(prefers-color-scheme: light)").matches) theme = "light";
  } catch (e) { /* storage refused: the default stands */ }
  document.documentElement.setAttribute("data-theme", theme);
})();
