/**
 * The payload of a JWT, read without verifying anything: the resource servers verify it. Null for
 * anything that is not a JWT.
 * @param {string | null | undefined} token
 * @returns {Record<string, any> | null}
 */
export function claimsOf(token) {
  try {
    const segment = String(token).split(".")[1];
    const json = atob(segment.replace(/-/g, "+").replace(/_/g, "/"));
    const text = decodeURIComponent(
      Array.from(json, (c) => "%" + c.charCodeAt(0).toString(16).padStart(2, "0")).join(""),
    );
    const claims = JSON.parse(text);
    return claims && typeof claims === "object" ? claims : null;
  } catch {
    return null;
  }
}

/**
 * Who a token names, read from its claims and falling back to the stored profile.
 * @param {Record<string, any>} claims
 * @param {Record<string, any> | undefined} profile
 * @returns {import("./types.js").Account | null}
 */
export function accountOf(claims, profile) {
  const p = profile || {};
  const id = claims.sub || p.sub;
  if (!id) return null;
  const username = claims.preferred_username || p.preferred_username || null;
  return { id, name: claims.name || p.name || username || id, username };
}
