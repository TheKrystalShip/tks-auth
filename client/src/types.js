/**
 * @typedef {"none" | "restoring" | "live" | "stale" | "ended"} Status
 * @typedef {"refused" | "revoked" | "denied"} EndReason
 *
 * @typedef {object} Account
 * @property {string} id The token's `sub`.
 * @property {string} name The token's `name`, else its `preferred_username`.
 * @property {string | null} username The token's `preferred_username`.
 *
 * @typedef {object} Snapshot
 * @property {Status} status
 * @property {EndReason | null} reason Set only with `ended`.
 * @property {Account | null} account
 * @property {Record<string, any>} claims The access token's decoded payload.
 * @property {number | null} expiresAt The access token's expiry, in milliseconds since the epoch.
 *
 * @typedef {object} Resolved
 * @property {string} issuer
 * @property {string} clientId
 *
 * @typedef {object} SessionConfig
 * @property {string} [issuer] With `clientId`, or `resolve`.
 * @property {string} [clientId]
 * @property {() => Promise<Resolved> | Resolved} [resolve] A site's own discovery. It answers from its
 *   own cache when the network is down, because the client cannot read a stored session without
 *   knowing the client id.
 * @property {string} [redirectPath] The callback, a real path. Default `/signed-in`.
 * @property {string} [postSignOutPath] Where the anchor sends the browser after signing out. Default `/`.
 * @property {number} [graceMs] How long a refused session may recover before `ended`. Default 30000.
 * @property {number} [renewAheadS] How long before expiry the timer renews. Default 60.
 *
 * @typedef {object} AdoptedTokens A session minted elsewhere. `access_token` (or `token`) is required;
 *   the expiry is `expires_in` (or `expiresIn`) seconds from now, `expires_at` seconds since the epoch,
 *   else the token's own `exp`.
 * @property {string} [access_token]
 * @property {string} [token]
 * @property {number} [expires_in]
 * @property {number} [expiresIn]
 * @property {number} [expires_at]
 *
 * @typedef {object} DeviceCode
 * @property {string} userCode
 * @property {string} verificationUri
 * @property {string} verificationUriComplete
 * @property {number} expiresIn Seconds.
 * @property {() => Promise<"signed-in" | "refused" | "expired" | "cancelled">} poll Resolves when the
 *   person decides, the code runs out or `cancel()` is called. Honours `slow_down` and keeps polling
 *   through network failures.
 * @property {() => void} cancel
 *
 * @typedef {object} Session
 * @property {() => Promise<string | null>} restore
 * @property {(options?: { returnTo?: string }) => Promise<boolean>} signIn
 * @property {() => Promise<string>} fresh
 * @property {(input: RequestInfo | URL, init?: RequestInit & { fresh?: boolean }) => Promise<Response>} authorizedFetch
 * @property {() => Promise<void>} signOut
 * @property {(tokens: AdoptedTokens, renew: () => Promise<AdoptedTokens | null | undefined>) => void} adopt
 * @property {(reason?: EndReason) => void} end
 * @property {(patch: Partial<Account>) => void} updateAccount
 * @property {(listener: () => void) => () => void} subscribe
 * @property {() => Snapshot} getSnapshot
 * @property {{ start: (options?: { name?: string }) => Promise<DeviceCode> }} device
 */
export {};
