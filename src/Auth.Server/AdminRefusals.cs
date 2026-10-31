using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// Records refusals at the administrator-only surfaces, one line per (surface, account, address) per
/// minute.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first refusal in a window is written at once with a count of 1; the rest are counted.</b> A
/// scanner repeats itself, and a row per attempt would bury the record it is made of. Writing the first
/// immediately means a crash or a restart loses nothing that was already known. When the window has
/// closed, a sweep writes one follow-up for that key carrying the number of refusals the first line did
/// not, so the counts of a key's lines add up to the refusals it made. A key that made one refusal has
/// no follow-up.
/// </para>
/// <para>
/// The window opens at the first refusal and does not slide: a client retrying every ten seconds is
/// summarised every minute rather than never. Time comes from <see cref="TimeProvider"/>, and the sweep
/// runs on a timer of the same provider, so a test advances both together.
/// </para>
/// <para>
/// Best-effort, as every line this provider writes: the refusal has already been answered.
/// </para>
/// </remarks>
internal sealed class AdminRefusals : IDisposable
{
    /// <summary>How long a key's refusals are folded into one line.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(15);

    private readonly record struct Key(string Surface, string? Account, string? Address);

    private sealed class Open(DateTimeOffset since, string route)
    {
        public DateTimeOffset Since { get; } = since;
        public string Route { get; } = route;
        public int Suppressed { get; set; }
    }

    private readonly AuditLog _journal;
    private readonly TimeProvider _time;
    private readonly Dictionary<Key, Open> _windows = [];
    private readonly Lock _gate = new();
    private readonly ITimer _sweep;

    public AdminRefusals(AuditLog journal, TimeProvider time)
    {
        _journal = journal;
        _time = time;
        _sweep = time.CreateTimer(_ => _ = SweepAsync(), null, SweepEvery, SweepEvery);
    }

    /// <summary>Record that <paramref name="account"/> at <paramref name="address"/> was refused.</summary>
    /// <param name="surface">One of <see cref="AdminRefusalSurfaces"/>.</param>
    /// <param name="route">The path asked for.</param>
    /// <param name="account">The username refused, or null when nobody was identified.</param>
    /// <param name="address">Where the request came from, or null when that is not known.</param>
    /// <param name="ct">Cancellation.</param>
    internal async Task RecordAsync(
        string surface, string route, string? account, string? address, CancellationToken ct = default)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Key key = new(surface, account, address);
        Key? summarise = null;
        Open? closed = null;

        lock (_gate)
        {
            if (_windows.TryGetValue(key, out Open? open) && now - open.Since < Window)
            {
                open.Suppressed++;
                return;
            }

            if (open is { Suppressed: > 0 })
            {
                summarise = key;
                closed = open;
            }

            _windows[key] = new Open(now, route);
        }

        if (summarise is { } k && closed is not null)
            await _journal.AdminRefusedAsync(k.Surface, closed.Route, k.Account, k.Address, closed.Suppressed, ct).ConfigureAwait(false);

        await _journal.AdminRefusedAsync(surface, route, account, address, 1, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Close the windows that have run out, writing the follow-up of each that counted refusals past its
    /// first line.
    /// </summary>
    internal async Task SweepAsync()
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<(Key Key, Open Window)> due = [];

        lock (_gate)
        {
            foreach ((Key key, Open open) in _windows)
            {
                if (now - open.Since >= Window)
                    due.Add((key, open));
            }

            foreach ((Key key, _) in due)
                _windows.Remove(key);
        }

        foreach ((Key key, Open open) in due.Where(d => d.Window.Suppressed > 0))
            await _journal.AdminRefusedAsync(key.Surface, open.Route, key.Account, key.Address, open.Suppressed).ConfigureAwait(false);
    }

    /// <summary>
    /// Record the refusal just decided for <paramref name="accountId"/> when the request is at an
    /// administrator-only route, and do nothing for any other. The one place a route's refusal reaches
    /// this log: the routes themselves are unchanged.
    /// </summary>
    internal static async Task RecordIfAdminOnlyAsync(HttpContext ctx, string accountId)
    {
        PathString path = ctx.Request.Path;
        if (!path.StartsWithSegments(ApplicationEndpoints.Route) && !path.Equals(AdminOperations.ChecksRoute))
            return;

        string? account = null;
        try
        {
            account = (await ctx.RequestServices.GetRequiredService<IUserStore>()
                .FindByIdAsync(accountId, ctx.RequestAborted))?.Username;
        }
        catch (AuthProviderException)
        {
        }

        await ctx.RequestServices.GetRequiredService<AdminRefusals>().RecordAsync(
            AdminRefusalSurfaces.Route, path.Value!, account, AddressOf(ctx), ctx.RequestAborted);
    }

    /// <summary>
    /// Where a request came from: the address the reverse proxy saw when the connection is the proxy's
    /// own on loopback, the connection's peer otherwise. Null when neither is known.
    /// </summary>
    internal static string? AddressOf(HttpContext ctx)
    {
        System.Net.IPAddress? peer = ctx.Connection.RemoteIpAddress;
        if (peer is not null && System.Net.IPAddress.IsLoopback(peer)
            && ctx.Request.Headers["X-Real-IP"].ToString() is { Length: > 0 } real
            && System.Net.IPAddress.TryParse(real, out System.Net.IPAddress? parsed))
            return parsed.ToString();

        return peer?.ToString();
    }

    public void Dispose() => _sweep.Dispose();
}
