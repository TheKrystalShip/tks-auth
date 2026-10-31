using System.Globalization;
using Microsoft.Data.Sqlite;


namespace TheKrystalShip.Auth.Server;

/// <summary>
/// A browser's sign-in at the provider itself: the row its <c>tks_auth_session</c> cookie names.
/// </summary>
/// <param name="SessionId">The provider session, as every <c>id_token</c> minted under it carries it in <c>sid</c>.</param>
/// <param name="Handle">The credential handle it was last proved with.</param>
/// <param name="Identity">That credential's identity, as the sessions minted under it carry it.</param>
/// <param name="Created">When this browser first signed in here.</param>
/// <param name="Expires">When the cookie stops being honoured.</param>
/// <param name="CredentialAt">When a credential was last typed or a provider last completed for it.</param>
internal sealed record ProviderSessionRow(
    string SessionId,
    string Handle,
    StoredIdentity Identity,
    DateTimeOffset Created,
    DateTimeOffset Expires,
    DateTimeOffset CredentialAt);

/// <summary>
/// An authorization request, held at the provider while a person proves who they are.
/// </summary>
/// <remarks>
/// Held here rather than carried by the page, so the page, its fallback form and its provider links carry
/// no request field for anybody to edit on the way through. The browser holds only the secret that names
/// it, in <c>tks_auth_authz</c>.
/// </remarks>
/// <param name="SecretHash">The hash of the cookie's secret, which is the row's key.</param>
/// <param name="ClientId">The client that asked.</param>
/// <param name="RedirectUri">Where the code goes, already matched against the client's registration.</param>
/// <param name="State">The client's <c>state</c>, returned untouched.</param>
/// <param name="CodeChallenge">The S256 challenge the code will be held to.</param>
/// <param name="Nonce">The client's <c>nonce</c>, stated back in the <c>id_token</c>.</param>
/// <param name="Prompt">What the client asked about showing a page.</param>
/// <param name="UpstreamState">
/// The <c>state</c> a round trip to an external provider was started with, so its callback completes this
/// request and not whatever else the browser has in flight.
/// </param>
/// <param name="Expires">When the request is abandoned.</param>
internal sealed record AuthorizeRequest(
    string SecretHash,
    string ClientId,
    string RedirectUri,
    string? State,
    string CodeChallenge,
    string? Nonce,
    string? Prompt,
    string? UpstreamState,
    DateTimeOffset Expires);

/// <summary>A code, as it waits to be exchanged at <c>/token</c>.</summary>
/// <param name="ClientId">The client it was issued to.</param>
/// <param name="RedirectUri">The exact redirect it was sent to, which the exchange must repeat.</param>
/// <param name="CodeChallenge">What the exchange's verifier must hash to.</param>
/// <param name="Nonce">Stated back in the <c>id_token</c>.</param>
/// <param name="UserId">The account it was issued for.</param>
/// <param name="ProviderSession">The provider session it was issued under.</param>
/// <param name="AuthTime">When that provider session last saw a credential.</param>
/// <param name="Expires">Sixty seconds after issue.</param>
internal sealed record AuthorizationCode(
    string ClientId,
    string RedirectUri,
    string CodeChallenge,
    string? Nonce,
    string UserId,
    string ProviderSession,
    DateTimeOffset AuthTime,
    DateTimeOffset Expires);

/// <summary>Where a registered client came from.</summary>
internal static class ClientSources
{
    /// <summary>Registered by hand.</summary>
    public const string Admin = "admin";

    /// <summary>The admin pages' own client, built in and never stored.</summary>
    public const string Builtin = "builtin";
}

/// <summary>A client this provider will issue codes to.</summary>
/// <param name="ClientId">What the client names itself with.</param>
/// <param name="Name">What a person is shown.</param>
/// <param name="RedirectUris">The only places a code is sent, matched exactly.</param>
/// <param name="PostLogoutRedirectUris">The only places a signed-out browser is returned to, matched exactly.</param>
/// <param name="Source"><c>admin</c> for a client registered by hand, <c>builtin</c> for the admin pages' own.</param>
/// <param name="Created">When it was registered.</param>
/// <param name="ApplicationId">The application it signs people in to, or null for a client that signs nobody in.</param>
/// <param name="SecretHash">
/// The hash of its client secret, for a confidential client; null for a public one, which has no secret
/// to keep.
/// </param>
/// <param name="Device">
/// Whether it signs in with the device grant (RFC 8628): a screen with no browser to bounce, approved by a
/// person signed in here on another one.
/// </param>
internal sealed record RegisteredClient(
    string ClientId,
    string Name,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    string Source,
    DateTimeOffset Created,
    string? ApplicationId = null,
    string? SecretHash = null,
    bool Device = false)
{
    /// <summary>Whether it authenticates at <c>/token</c> with a secret.</summary>
    public bool Confidential => SecretHash is not null;
}

/// <summary>
/// The provider's own rows: its sign-ins, the requests in flight, the codes, and the clients.
/// </summary>
/// <remarks>
/// <para>
/// Beside the sessions rather than in a file of their own, because a provider session is a session — it
/// is listed with the others and ended with them — and every session minted through the provider records
/// the one it came from, so the two are joined by a column rather than across files.
/// </para>
/// <para>
/// A cookie, a request secret and a code are stored hashed. Each is a bearer in its own right, and a
/// copy of this file must not be a way to present one.
/// </para>
/// </remarks>
internal sealed partial class SqliteSessionRegistry
{
    /// <summary>The <c>kind</c> a provider session's row carries.</summary>
    internal const string ProviderKind = "provider";

    /// <summary>
    /// The <c>kind</c> a session signed in with the device grant carries. It is minted under no provider
    /// session, because the browser that approved it is not the device that holds it; every other session
    /// minted through the provider carries no kind.
    /// </summary>
    internal const string DeviceKind = "device";

    private static void InitializeProvider(SqliteConnection connection)
    {
        // Additive and nullable, like every column this file has gained: a row written before one
        // existed has no answer for it, and null says so.
        foreach (string column in (string[])
        [
            // The provider session a session was minted under. Survives every rotation, because a
            // rotation updates its row in place.
            "provider_session TEXT NULL",
            // 'provider' on a browser's sign-in at the provider, null on everything a surface holds.
            "kind TEXT NULL",
            // A provider session's cookie secret, hashed.
            "cookie_hash TEXT NULL",
            // When a provider session last saw a credential: the recent-proof clock and auth_time.
            "credential_at TEXT NULL",
            // The identity a provider session was proved with, which every session minted under it carries.
            "identity TEXT NULL",
        ])
        {
            using SqliteCommand add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE sessions ADD COLUMN {column};";
            try
            {
                add.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Already there.
            }
        }

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE INDEX IF NOT EXISTS ix_sessions_provider ON sessions (provider_session);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_sessions_cookie ON sessions (cookie_hash)
                WHERE cookie_hash IS NOT NULL;

            CREATE TABLE IF NOT EXISTS authorize_requests (
                secret_hash    TEXT PRIMARY KEY,
                client_id      TEXT NOT NULL,
                redirect_uri   TEXT NOT NULL,
                state          TEXT NULL,
                code_challenge TEXT NOT NULL,
                nonce          TEXT NULL,
                prompt         TEXT NULL,
                upstream_state TEXT NULL,
                expires        TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS authorization_codes (
                code_hash        TEXT PRIMARY KEY,
                client_id        TEXT NOT NULL,
                redirect_uri     TEXT NOT NULL,
                code_challenge   TEXT NOT NULL,
                nonce            TEXT NULL,
                user_id          TEXT NOT NULL,
                provider_session TEXT NOT NULL,
                auth_time        TEXT NOT NULL,
                expires          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS clients (
                client_id        TEXT PRIMARY KEY,
                name             TEXT NOT NULL,
                redirect_uris    TEXT NOT NULL,
                post_logout_uris TEXT NOT NULL,
                source           TEXT NOT NULL,
                member_id        TEXT NULL,
                created          TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Every session lives under a sign-in made here: a provider session, one minted under one, or a
        // device's, approved by a person signed in on a browser and carrying its kind. A row that is none
        // of these is no session this provider continues — nothing ends it on a sign-out and nothing lists it
        // with the sign-in it came from — so it goes as the registry opens. Its bearer is refused at its
        // next renewal and runs out within one access lifetime.
        using SqliteCommand orphans = connection.CreateCommand();
        orphans.CommandText = "DELETE FROM sessions WHERE kind IS NULL AND provider_session IS NULL;";
        orphans.ExecuteNonQuery();
    }

    // ── Sessions minted under a provider session ─────────────────────────────

    /// <summary>
    /// Record a session minted through the provider, naming the provider session it came from and the
    /// client it was minted for.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="providerSession">The provider session it was minted under, or null for a device's.</param>
    /// <param name="clientId">The client it was minted for, or null for a session of no client.</param>
    /// <param name="kind"><see cref="DeviceKind"/> for a device's session, otherwise null.</param>
    /// <param name="name">The name the person gave a device at sign-in, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    internal Task CreateAsync(
        SessionRegistration session, string? providerSession, string? clientId = null, string? kind = null,
        string? name = null, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO sessions
                    (session_id, user_id, host_id, created, expires, user_agent, current_jti, revoked,
                     provider_session, client_id, kind, name)
                VALUES ($sid, $user, $host, $created, $expires, $ua, $jti, 0, $provider, $client, $kind, $name);
                """;
            cmd.Parameters.AddWithValue("$client", (object?)clientId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sid", session.SessionId);
            cmd.Parameters.AddWithValue("$user", session.UserId);
            cmd.Parameters.AddWithValue("$host", session.HostId);
            cmd.Parameters.AddWithValue("$created", session.Created.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", session.Expires.ToString("O"));
            cmd.Parameters.AddWithValue("$ua", (object?)session.UserAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$jti", (object?)session.CurrentJti ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$provider", (object?)providerSession ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    // ── Provider sessions ─────────────────────────────────────────────────────

    /// <summary>Start a browser's sign-in at the provider.</summary>
    internal Task CreateProviderSessionAsync(
        string sessionId, string handle, string identityJson, string cookieHash, string issuer,
        DateTimeOffset now, DateTimeOffset expires, string? userAgent, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO sessions
                    (session_id, user_id, host_id, created, expires, user_agent, current_jti, revoked,
                     kind, cookie_hash, credential_at, identity)
                VALUES ($sid, $user, $host, $now, $expires, $ua, NULL, 0, $kind, $cookie, $now, $identity);
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$user", handle);
            cmd.Parameters.AddWithValue("$host", issuer);
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", expires.ToString("O"));
            cmd.Parameters.AddWithValue("$ua", (object?)userAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", ProviderKind);
            cmd.Parameters.AddWithValue("$cookie", cookieHash);
            cmd.Parameters.AddWithValue("$identity", identityJson);
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    /// <summary>The live provider session a cookie names, or null.</summary>
    internal Task<ProviderSessionRow?> FindProviderSessionByCookieAsync(string cookieHash, CancellationToken ct = default) =>
        Task.FromResult(ReadProviderSession("cookie_hash = $key", cookieHash));

    /// <summary>The live provider session with this id, or null.</summary>
    internal Task<ProviderSessionRow?> FindProviderSessionAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(ReadProviderSession("session_id = $key", sessionId));

    /// <summary>
    /// When the browser sign-in a live session was minted under last proved a credential, or null when
    /// the session is not live or no proof is recorded.
    /// </summary>
    /// <remarks>
    /// A session is minted under a provider session, and the provider session records every time a
    /// password is typed or a provider completes for it, so this is the one answer to "how recently did
    /// the person holding this session prove who they are".
    /// </remarks>
    internal Task<DateTimeOffset?> CredentialAtAsync(string sessionId, CancellationToken ct = default)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.credential_at
              FROM sessions s
              JOIN sessions p ON p.session_id = s.provider_session
             WHERE s.session_id = $sid AND s.revoked = 0 AND s.expires > $now
               AND p.kind = $kind AND p.revoked = 0;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$kind", ProviderKind);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        return Task.FromResult(cmd.ExecuteScalar() is string at ? Parse(at) : (DateTimeOffset?)null);
    }

    private ProviderSessionRow? ReadProviderSession(string where, string key)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT session_id, user_id, identity, created, expires, credential_at
              FROM sessions
             WHERE {where} AND kind = $kind AND revoked = 0 AND expires > $now;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$kind", ProviderKind);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        // A row whose identity cannot be read is a sign-in nothing can be minted under. It is treated as
        // absent, so the browser is asked for a credential rather than handed something half-formed.
        if (reader.IsDBNull(2) || reader.IsDBNull(5) || StoredIdentity.Read(reader.GetString(2)) is not { } identity)
            return null;

        return new ProviderSessionRow(
            reader.GetString(0),
            reader.GetString(1),
            identity,
            Parse(reader.GetString(3)),
            Parse(reader.GetString(4)),
            Parse(reader.GetString(5)));
    }

    /// <summary>
    /// A credential proved the same account again: record when, and which credential, and give the cookie
    /// its full lifetime from now.
    /// </summary>
    internal Task<bool> ReproveProviderSessionAsync(
        string sessionId, string handle, string identityJson, DateTimeOffset now, DateTimeOffset expires,
        CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE sessions
                   SET user_id = $user, identity = $identity, credential_at = $now, expires = $expires
                 WHERE session_id = $sid AND kind = $kind AND revoked = 0;
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$user", handle);
            cmd.Parameters.AddWithValue("$identity", identityJson);
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", expires.ToString("O"));
            cmd.Parameters.AddWithValue("$kind", ProviderKind);

            return Task.FromResult(cmd.ExecuteNonQuery() == 1);
        }
    }

    /// <summary>
    /// End a provider session and every session minted under it, and say which of them were live.
    /// </summary>
    /// <remarks>
    /// Found by the column rather than by the cookie, so a sign-out naming the provider session in an
    /// <c>id_token_hint</c> ends it whether or not the browser still carries the cookie. The ids come back
    /// with the handle each was keyed by, because each one is recorded against its account.
    /// </remarks>
    /// <returns>The live sessions ended, the provider session itself included when it was live.</returns>
    internal Task<IReadOnlyList<(string SessionId, string Handle, bool Provider)>> EndProviderSessionAsync(
        string providerSession, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            var ended = new List<(string, string, bool)>();
            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText =
                    """
                    SELECT session_id, user_id, kind
                      FROM sessions
                     WHERE (session_id = $psid OR provider_session = $psid) AND revoked = 0 AND expires > $now;
                    """;
                read.Parameters.AddWithValue("$psid", providerSession);
                read.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                using SqliteDataReader reader = read.ExecuteReader();
                while (reader.Read())
                {
                    ended.Add((reader.GetString(0), reader.GetString(1),
                        !reader.IsDBNull(2) && reader.GetString(2) == ProviderKind));
                }
            }

            using (SqliteCommand revoke = connection.CreateCommand())
            {
                revoke.Transaction = tx;
                revoke.CommandText =
                    """
                    UPDATE sessions SET revoked = 1, current_jti = NULL, cookie_hash = NULL
                     WHERE (session_id = $psid OR provider_session = $psid) AND revoked = 0;
                    """;
                revoke.Parameters.AddWithValue("$psid", providerSession);
                revoke.ExecuteNonQuery();
            }

            tx.Commit();
            return Task.FromResult<IReadOnlyList<(string, string, bool)>>(ended);
        }
    }

    /// <summary>Whether this session id is a provider session's, live or not.</summary>
    internal Task<bool> IsProviderSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sessions WHERE session_id = $sid AND kind = $kind;";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$kind", ProviderKind);
        return Task.FromResult(cmd.ExecuteScalar() is not null);
    }

    // ── Requests in flight ────────────────────────────────────────────────────

    /// <summary>Hold a validated authorization request under the hash of the secret its cookie carries.</summary>
    internal Task StoreRequestAsync(AuthorizeRequest request, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO authorize_requests
                    (secret_hash, client_id, redirect_uri, state, code_challenge, nonce, prompt, upstream_state, expires)
                VALUES ($key, $client, $redirect, $state, $challenge, $nonce, $prompt, NULL, $expires);
                """;
            cmd.Parameters.AddWithValue("$key", request.SecretHash);
            cmd.Parameters.AddWithValue("$client", request.ClientId);
            cmd.Parameters.AddWithValue("$redirect", request.RedirectUri);
            cmd.Parameters.AddWithValue("$state", (object?)request.State ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$challenge", request.CodeChallenge);
            cmd.Parameters.AddWithValue("$nonce", (object?)request.Nonce ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prompt", (object?)request.Prompt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$expires", request.Expires.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    /// <summary>The live request a cookie's secret names, or null.</summary>
    internal Task<AuthorizeRequest?> FindRequestAsync(string secretHash, CancellationToken ct = default)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT secret_hash, client_id, redirect_uri, state, code_challenge, nonce, prompt, upstream_state, expires
              FROM authorize_requests
             WHERE secret_hash = $key AND expires > $now;
            """;
        cmd.Parameters.AddWithValue("$key", secretHash);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<AuthorizeRequest?>(null);

        return Task.FromResult<AuthorizeRequest?>(new AuthorizeRequest(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            NullableString(reader, 3),
            reader.GetString(4),
            NullableString(reader, 5),
            NullableString(reader, 6),
            NullableString(reader, 7),
            Parse(reader.GetString(8))));
    }

    /// <summary>Record the state a round trip to an external provider began with.</summary>
    internal Task SetUpstreamStateAsync(string secretHash, string upstreamState, CancellationToken ct = default) =>
        Execute("UPDATE authorize_requests SET upstream_state = $value WHERE secret_hash = $key;",
            secretHash, upstreamState);

    /// <summary>Keep a request alive while its account waits for approval.</summary>
    internal Task ExtendRequestAsync(string secretHash, DateTimeOffset expires, CancellationToken ct = default) =>
        Execute("UPDATE authorize_requests SET expires = $value WHERE secret_hash = $key;",
            secretHash, expires.ToString("O"));

    /// <summary>Forget a request, answered or abandoned.</summary>
    internal Task DeleteRequestAsync(string secretHash, CancellationToken ct = default) =>
        Execute("DELETE FROM authorize_requests WHERE secret_hash = $key;", secretHash, null);

    // ── Codes ─────────────────────────────────────────────────────────────────

    /// <summary>Hold a code until it is exchanged or expires.</summary>
    internal Task IssueCodeAsync(string codeHash, AuthorizationCode code, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO authorization_codes
                    (code_hash, client_id, redirect_uri, code_challenge, nonce, user_id, provider_session,
                     auth_time, expires)
                VALUES ($key, $client, $redirect, $challenge, $nonce, $user, $provider, $auth, $expires);
                """;
            cmd.Parameters.AddWithValue("$key", codeHash);
            cmd.Parameters.AddWithValue("$client", code.ClientId);
            cmd.Parameters.AddWithValue("$redirect", code.RedirectUri);
            cmd.Parameters.AddWithValue("$challenge", code.CodeChallenge);
            cmd.Parameters.AddWithValue("$nonce", (object?)code.Nonce ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$user", code.UserId);
            cmd.Parameters.AddWithValue("$provider", code.ProviderSession);
            cmd.Parameters.AddWithValue("$auth", code.AuthTime.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", code.Expires.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Take a code out of the store, whatever else is wrong with the exchange presenting it.
    /// </summary>
    /// <remarks>
    /// One statement that deletes and returns, so two exchanges racing with the same code cannot both
    /// read it: exactly one gets the row and the other gets nothing. The caller checks expiry, client,
    /// redirect and verifier afterwards — a code presented wrongly is spent, because the only party that
    /// presents one wrongly is one that should not have it.
    /// </remarks>
    internal Task<AuthorizationCode?> ConsumeCodeAsync(string codeHash, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                DELETE FROM authorization_codes WHERE code_hash = $key
                RETURNING client_id, redirect_uri, code_challenge, nonce, user_id, provider_session, auth_time, expires;
                """;
            cmd.Parameters.AddWithValue("$key", codeHash);

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<AuthorizationCode?>(null);

            return Task.FromResult<AuthorizationCode?>(new AuthorizationCode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                NullableString(reader, 3),
                reader.GetString(4),
                reader.GetString(5),
                Parse(reader.GetString(6)),
                Parse(reader.GetString(7))));
        }
    }

    // ── Clients ───────────────────────────────────────────────────────────────

    /// <summary>Every registered client.</summary>
    internal Task<IReadOnlyList<RegisteredClient>> ListClientsAsync(CancellationToken ct = default)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT client_id, name, redirect_uris, post_logout_uris, source, created,
                   application_id, secret_hash, device_grant
              FROM clients ORDER BY client_id;
            """;

        var clients = new List<RegisteredClient>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            clients.Add(new RegisteredClient(
                reader.GetString(0),
                reader.GetString(1),
                Lines(reader.GetString(2)),
                Lines(reader.GetString(3)),
                reader.GetString(4),
                Parse(reader.GetString(5)),
                NullableString(reader, 6),
                NullableString(reader, 7),
                !reader.IsDBNull(8) && reader.GetInt64(8) == 1));
        }

        return Task.FromResult<IReadOnlyList<RegisteredClient>>(clients);
    }

    /// <summary>
    /// Put every client that belongs to no application under <paramref name="applicationId"/>, as a client
    /// registered by hand.
    /// </summary>
    /// <returns>The ids it moved, in order.</returns>
    internal Task<IReadOnlyList<string>> AdoptClientsAsync(string applicationId, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            var moved = new List<string>();
            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT client_id FROM clients WHERE application_id IS NULL ORDER BY client_id;";
                using SqliteDataReader reader = read.ExecuteReader();
                while (reader.Read())
                    moved.Add(reader.GetString(0));
            }

            if (moved.Count > 0)
            {
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText =
                    """
                    UPDATE clients
                       SET application_id = $application, source = $source, member_id = NULL
                     WHERE application_id IS NULL;
                    """;
                update.Parameters.AddWithValue("$application", applicationId);
                update.Parameters.AddWithValue("$source", ClientSources.Admin);
                update.ExecuteNonQuery();
                BumpGeneration(connection, tx);
            }

            tx.Commit();
            return Task.FromResult<IReadOnlyList<string>>(moved);
        }
    }

    private static bool InsertClient(SqliteConnection connection, SqliteTransaction tx, RegisteredClient client)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO clients
                (client_id, name, redirect_uris, post_logout_uris, source, member_id, created, application_id, secret_hash,
                 device_grant)
            VALUES ($id, $name, $redirects, $logouts, $source, $member, $created, $application, $secret, $device)
            ON CONFLICT (client_id) DO NOTHING;
            """;
        BindClient(cmd, client);
        return cmd.ExecuteNonQuery() == 1;
    }

    private static void BindClient(SqliteCommand cmd, RegisteredClient client)
    {
        cmd.Parameters.AddWithValue("$id", client.ClientId);
        cmd.Parameters.AddWithValue("$name", client.Name);
        cmd.Parameters.AddWithValue("$redirects", Join(client.RedirectUris));
        cmd.Parameters.AddWithValue("$logouts", Join(client.PostLogoutRedirectUris));
        cmd.Parameters.AddWithValue("$source", client.Source);
        cmd.Parameters.AddWithValue("$member", DBNull.Value);
        cmd.Parameters.AddWithValue("$created", client.Created.ToString("O"));
        cmd.Parameters.AddWithValue("$application", (object?)client.ApplicationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$secret", (object?)client.SecretHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$device", client.Device ? 1 : (object)DBNull.Value);
    }

    // A URI never contains a line break, so a list of them is stored one per line.
    private static string Join(IReadOnlyList<string> uris) => string.Join('\n', uris);

    private static IReadOnlyList<string> Lines(string stored) =>
        stored.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private Task Execute(string sql, string key, string? value)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset Parse(string stored) =>
        DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture);
}
