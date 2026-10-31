using System.Text.Json.Serialization;

namespace TheKrystalShip.Auth.Server;

/// <summary>The body of a refusal: a stable code and a sentence for a person.</summary>
/// <param name="Code">What a client matches on.</param>
/// <param name="Message">What a person is told.</param>
internal sealed record ErrorBody(string Code, string Message);

/// <summary>Every refusal this daemon answers with, as <c>{"error":{"code":"…","message":"…"}}</c>.</summary>
/// <param name="Error">The refusal.</param>
internal sealed record ErrorEnvelope(ErrorBody Error);

/// <summary>What a browser posts to sign in.</summary>
internal sealed record SignInRequest(string? Username, string? Password);

/// <summary>
/// What a browser posts to make an account.
/// </summary>
/// <remarks>
/// A username, a password, and optionally a name to be shown by. Deliberately nothing else — a role
/// or a status on this shape is a field somebody will try to set, and both are decided by the provider.
/// </remarks>
internal sealed record RegisterRequest(string? Username, string? Password, string? DisplayName);

/// <summary>What is posted to set somebody's password, on <c>auth:accounts.create</c>.</summary>
/// <param name="Password">The password the account will hold.</param>
internal sealed record PasswordSetRequest(string? Password);

/// <summary>What is posted to create an account, on <c>auth:accounts.create</c>.</summary>
/// <remarks>
/// A password is optional: an account can be made for somebody who will only ever arrive through a
/// provider. One that <em>is</em> set answers to the same floor as every other, or the door with the
/// least scrutiny becomes the one that admits the weakest password here. The account holds
/// only <c>everyone</c>; assigning it roles is the next, separate act.
/// </remarks>
/// <param name="Username">The name it is keyed by for people, not for the store.</param>
/// <param name="DisplayName">What to show, defaulting to the username.</param>
/// <param name="Password">Optional.</param>
/// <param name="Status">
/// <c>active</c> or <c>pending</c>. Creating one already disabled is a shape with no use — somebody
/// wanting that creates it and disables it, and the trail then says both things happened.
/// </param>
internal sealed record CreateAccountRequest(
    string? Username, string? DisplayName, string? Password, string? Status);

/// <summary>One live session, as a person reviewing their own devices sees it.</summary>
/// <param name="Sid">The session id.</param>
/// <param name="UserId">Whose it is, as the handle it was keyed by.</param>
/// <param name="Created">When they signed in.</param>
/// <param name="Expires">The absolute cap on it.</param>
/// <param name="UserAgent">The device, or absent when it sent none.</param>
/// <param name="LastSeen">
/// When this session last rotated its tokens, or absent for one that has not. It is the only contact
/// the provider has with a live session — every other request goes to the application that holds the
/// token — so it is a rotation, named as the nearest true thing rather than as a request count nothing
/// counts.
/// </param>
/// <param name="Current">True on exactly the session the calling bearer belongs to.</param>
/// <param name="Kind">
/// <c>provider</c> on a browser's sign-in at the provider itself, and ending one of those ends every session
/// minted under it; <c>device</c> on a device signed in with the device grant, which lives under no
/// browser's sign-in; absent on a session a surface holds.
/// </param>
/// <param name="Name">A device's name, as the person who approved it left it; absent when it has none.</param>
internal sealed record SessionRecord(
    string Sid,
    string UserId,
    DateTimeOffset Created,
    DateTimeOffset Expires,
    string? UserAgent,
    DateTimeOffset? LastSeen,
    bool Current,
    string? Kind = null,
    string? Name = null);

/// <summary>Every live session for one account.</summary>
/// <param name="Data">The sessions, most recent first.</param>
internal sealed record SessionsPage(IReadOnlyList<SessionRecord> Data);

/// <summary>
/// What the caller posts to end sessions of their own.
/// </summary>
/// <param name="Sid">One session, which must be theirs. Ending somebody else's is another door, on <c>auth:accounts.disable</c>.</param>
/// <param name="All">Every session they hold, the calling one included.</param>
internal sealed record RevokeRequest(string? Sid, bool? All);

/// <summary>What the caller posts to rename a device signed in to their account.</summary>
/// <param name="Sid">The device's session.</param>
/// <param name="Name">Its new name, cleaned as a device's name always is; empty for none.</param>
internal sealed record RenameRequest(string? Sid, string? Name);

/// <summary>The name a device was left with.</summary>
/// <param name="Name">The name as kept, or null for none.</param>
internal sealed record RenameResult(string? Name);

/// <summary>How many sessions a revocation ended.</summary>
/// <param name="Revoked">The count.</param>
internal sealed record RevokeResult(int Revoked);

/// <summary>What the caller posts to prove their password again.</summary>
/// <param name="Password">The password the account holds.</param>
internal sealed record ReauthRequest(string? Password);

/// <summary>The proof, and when it lapses.</summary>
/// <param name="ExpiresAt">When the caller stops being allowed to change what proves them.</param>
internal sealed record ReauthResult(DateTimeOffset ExpiresAt);

/// <summary>
/// Where to send the browser to attach an account at a provider.
/// </summary>
/// <remarks>
/// A URL rather than a redirect: the caller is an authenticated request carrying a bearer, and a
/// bearer does not survive the redirect chain a browser would follow.
/// </remarks>
/// <param name="Url">The provider's authorize URL, with the one-time ticket cookie set alongside it.</param>
internal sealed record LinkStartResponse(string Url);

/// <summary>An account, as the accounts list shows it. Never carries a secret in any form.</summary>
/// <remarks>What the account may do is its assignments, which the authority lists.</remarks>
/// <param name="Id">The opaque <c>usr_…</c> id, stable across a rename.</param>
/// <param name="Origin">
/// <c>arrived</c> for an account that made itself, which expires if nobody approves it; <c>admitted</c>
/// for one somebody made or approved.
/// </param>
/// <param name="Username">The login name. Renameable, never a key.</param>
/// <param name="DisplayName">What a person is shown.</param>
/// <param name="Status">Whether it may be used at all.</param>
/// <param name="HasPassword">Whether a password can sign this account in at all.</param>
/// <param name="Identities">Linked external identities, as <c>provider:subject</c> handles.</param>
/// <param name="Created">When the account was made.</param>
/// <param name="Updated">When any field above last changed.</param>
internal sealed record AccountEntry(
    string Id,
    string Username,
    string DisplayName,
    string Origin,
    string Status,
    bool HasPassword,
    IReadOnlyList<string> Identities,
    DateTimeOffset Created,
    DateTimeOffset Updated);

/// <summary>The account list. A page shape with no paging, because the store's own list has none.</summary>
internal sealed record AccountsPage(IReadOnlyList<AccountEntry> Data);

/// <summary>A change to an account's standing: approve it, switch it off or on.</summary>
/// <param name="Status">The standing to set: <c>active</c>, <c>pending</c> or <c>disabled</c>.</param>
internal sealed record AccountPatchRequest(string? Status);

/// <summary>
/// An account after a change, and the authority version the change landed at.
/// </summary>
/// <remarks>
/// The version is returned rather than left implicit because it is the whole guarantee: a caller that
/// sees it knows the change is the newest statement about this account, and any edit made against an
/// older version is refused.
/// </remarks>
internal sealed record AccountChanged(AccountEntry Account, long Version);

/// <summary>
/// Serializer metadata for everything this provider puts on the wire.
/// </summary>
/// <remarks>
/// Every shape is registered here. There is no reflection fallback under AOT, so a type that is
/// missing throws when it is first serialized rather than failing the build.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(AccountsPage))]
[JsonSerializable(typeof(AccountPatchRequest))]
[JsonSerializable(typeof(AccountChanged))]
[JsonSerializable(typeof(PasswordSetRequest))]
[JsonSerializable(typeof(ReauthRequest))]
[JsonSerializable(typeof(ReauthResult))]
[JsonSerializable(typeof(LinkStartResponse))]
[JsonSerializable(typeof(CreateAccountRequest))]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(AccountEntry))]
[JsonSerializable(typeof(SessionsPage))]
[JsonSerializable(typeof(RevokeRequest))]
[JsonSerializable(typeof(RenameRequest))]
[JsonSerializable(typeof(RenameResult))]
[JsonSerializable(typeof(RevokeResult))]
[JsonSerializable(typeof(StoredIdentity))]
[JsonSerializable(typeof(OidcDiscovery))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(OAuthError))]
[JsonSerializable(typeof(TokenExchangeResponse))]
[JsonSerializable(typeof(OAuthAccountError))]
[JsonSerializable(typeof(UserInfoResponse))]
[JsonSerializable(typeof(CredentialAnswer))]
[JsonSerializable(typeof(AuthorizeContext))]
[JsonSerializable(typeof(DeviceAuthorizationResponse))]
[JsonSerializable(typeof(DeviceContext))]
[JsonSerializable(typeof(DeviceDecisionRequest))]
[JsonSerializable(typeof(DeviceDecisionResult))]
[JsonSerializable(typeof(AccountView))]
[JsonSerializable(typeof(ApplicationRequest))]
[JsonSerializable(typeof(ApplicationChange))]
[JsonSerializable(typeof(ApplicationClientRequest))]
[JsonSerializable(typeof(ApplicationsPage))]
[JsonSerializable(typeof(ApplicationAnswer))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
