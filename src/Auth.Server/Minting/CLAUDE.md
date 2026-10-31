# Minting — locked decisions

- **The token layer knows nothing about providers.** It mints and reads whatever `provider:subject`
  it is handed. A Discord sign-in produces the subject `discord:<id>` — `SessionTokenServiceTests`
  pins that string, because changing its spelling is a flag day that invalidates every live token and
  orphans every stored session row at once.
- **One signer, ES256, required.** `SessionTokenService` takes the `EcdsaSessionSigner` and nothing
  else can sign with it; there is no symmetric path, so nothing an application holds can be a key.
- **`RefreshLifetime`, `BearerAudience` and `Issuer` are settings, and all are load-bearing.** The lifetime
  is written once and used for both the token and the row, so there is no second copy to drift. The
  issuer is validated by every application, so changing it ends every session.
- **Access tokens are an application's.** `MintApplicationAccess` mints one from an `ApplicationAccess` —
  its audience, its lifetime, `client_id`, the account as `sub`, and `ApplicationClaims.Actions`
  (`tks_actions`) as an array however many it holds — typed `at+jwt`, with `sid` only when it belongs to a
  session and `act` (`ApplicationClaims.Actor`, RFC 8693 §4.1) only when a client acts for the account.
  The provider's own admin pages are the application `auth`, audienced to `BearerAudience`.
- **A refresh token is audienced to the issuer,** the one place it is presented
  (`MintApplicationRefresh`). `ReadRefreshAsync` accepts that audience only; `ValidationParameters`, the
  bearer check, accepts `BearerAudience` only.
