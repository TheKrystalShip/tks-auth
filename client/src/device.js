import { User } from "oidc-client-ts";
import { claimsOf } from "./claims.js";

const GRANT = "urn:ietf:params:oauth:grant-type:device_code";
const REQUEST_TIMEOUT_MS = 10000;
const DEFAULT_INTERVAL_S = 5;
const SLOW_DOWN_STEP_S = 5;

/** A device sign-in that could not start. `kind` is `unreachable` or `misconfigured`. */
export class DeviceSignInError extends Error {
  /** @param {"unreachable" | "misconfigured"} kind @param {string} [message] */
  constructor(kind, message) {
    super(message || kind);
    this.kind = kind;
  }
}

/**
 * Posts a form to the provider. Returns `{ status, body }`, or null when the provider could not be asked
 * or answered something that is not an OAuth document (a gateway page, a 5xx, a 429).
 */
async function postForm(url, fields) {
  let response;
  try {
    response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
      body: new URLSearchParams(fields),
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
  } catch {
    return null;
  }
  const body = await response.json().catch(() => null);
  if (!body || response.status >= 500 || response.status === 429) return null;
  return { status: response.status, body };
}

/**
 * Starts a device authorization (RFC 8628) and returns the code to show and the poller that waits for
 * the person's decision. `onTokens` takes the stored user a successful poll produces.
 * @param {{ init: () => Promise<{ manager: import("oidc-client-ts").UserManager }>, adoptUser: (user: User) => Promise<void> }} ctx
 * @param {{ name?: string }} [options]
 * @returns {Promise<import("./types.js").DeviceCode>}
 */
export async function startDevice(ctx, options = {}) {
  let manager;
  let endpoint;
  let tokenEndpoint;
  try {
    ({ manager } = await ctx.init());
    const metadata = /** @type {Record<string, any>} */ (await manager.metadataService.getMetadata());
    endpoint = metadata.device_authorization_endpoint;
    tokenEndpoint = metadata.token_endpoint;
  } catch {
    throw new DeviceSignInError("unreachable");
  }
  if (!endpoint || !tokenEndpoint) throw new DeviceSignInError("misconfigured", "The provider serves no device sign-in.");

  const clientId = manager.settings.client_id;
  const fields = { client_id: clientId, scope: "openid" };
  if (options.name) fields.device_name = options.name;
  const answered = await postForm(endpoint, fields);
  if (!answered) throw new DeviceSignInError("unreachable");
  const a = answered.body;
  if (answered.status >= 400 || !a.device_code) {
    throw new DeviceSignInError("misconfigured", a.error_description || a.error);
  }

  const expiresIn = Number(a.expires_in) || 600;
  const expiresAt = Date.now() + expiresIn * 1000;
  let intervalS = Math.max(1, Number(a.interval) || DEFAULT_INTERVAL_S);
  let cancelled = false;
  let wake = null;

  const sleep = (ms) =>
    new Promise((resolve) => {
      const t = setTimeout(resolve, ms);
      wake = () => { clearTimeout(t); resolve(); };
    });

  async function poll() {
    while (!cancelled) {
      await sleep(intervalS * 1000);
      if (cancelled) break;
      if (Date.now() >= expiresAt) return "expired";
      const result = await postForm(tokenEndpoint, { grant_type: GRANT, device_code: a.device_code, client_id: clientId });
      if (cancelled) break;
      if (!result) continue;
      const { status, body } = result;
      if (status < 400 && body.access_token) {
        const claims = claimsOf(body.access_token) || {};
        const user = new User({
          access_token: body.access_token,
          refresh_token: body.refresh_token,
          id_token: body.id_token || undefined,
          token_type: body.token_type || "Bearer",
          scope: body.scope || "openid",
          expires_at: Math.floor(Date.now() / 1000) + Number(body.expires_in || 0),
          profile: /** @type {any} */ ({ sub: claims.sub, name: claims.name, preferred_username: claims.preferred_username }),
        });
        await manager.storeUser(user);
        await ctx.adoptUser(user);
        return "signed-in";
      }
      switch (body.error) {
        case "authorization_pending": break;
        case "slow_down": intervalS += SLOW_DOWN_STEP_S; break;
        case "access_denied": return "refused";
        case "expired_token":
        case "invalid_grant": return "expired";
        default: break;
      }
    }
    return "cancelled";
  }

  return {
    userCode: a.user_code,
    verificationUri: a.verification_uri,
    verificationUriComplete: a.verification_uri_complete || a.verification_uri,
    expiresIn,
    poll,
    cancel() {
      cancelled = true;
      if (wake) wake();
    },
  };
}
