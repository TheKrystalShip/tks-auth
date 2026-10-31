// access.js — looking an action up in the answer the provider gave to `GET /me/access`.
//
// The answer is already evaluated — the caller's effective actions — so this module looks things up
// and decides nothing. It holds no copy of the rules and so cannot disagree with them.
//
// A report is `{ version, current, owner, actions: [...] }`. `owner` is the one answer a list cannot
// give: an Owner performs actions no manifest declares, which no report can name.
//
// Imports nothing, so a script can load it outside a browser.

const has = (list, action) => Array.isArray(list) && list.indexOf(action) !== -1;

// Whether the report allows `action`.
function reportAllows(r, action) {
  if (!r || !action) return false;
  return !!r.owner || has(r.actions, action);
}

export { reportAllows };
