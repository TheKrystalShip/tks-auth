using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Users;

/// <summary>The snapshot every evaluation reads, loaded once per change to what the store holds.</summary>
/// <remarks>
/// <b>Cached per generation, with no time-to-live.</b> Every write advances the store's generation in
/// the same transaction, so a cached snapshot is exact until something changes and is discarded the next
/// time anybody asks after it does. A revocation takes effect on the next request, never after a timer.
/// The generation is one read of the file's small metadata table per call.
/// </remarks>
public sealed class AuthoritySource(SqliteAuthorityStore store)
{
    private readonly SemaphoreSlim _load = new(1, 1);
    private (AuthoritySnapshot Snapshot, long Generation)? _cached;

    /// <summary>The store this reads.</summary>
    public SqliteAuthorityStore Store { get; } = store;

    /// <summary>The current snapshot.</summary>
    public async Task<AuthoritySnapshot> CurrentAsync(CancellationToken ct = default)
    {
        long generation = await Store.GenerationAsync(ct).ConfigureAwait(false);

        if (_cached is not { } held || held.Generation != generation)
        {
            await _load.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cached is not { } again || again.Generation != generation)
                    _cached = await Store.LoadWithGenerationAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _load.Release();
            }
        }

        return _cached!.Value.Snapshot;
    }

    /// <summary>An evaluator over the current snapshot.</summary>
    public async Task<AccessEvaluator> EvaluatorAsync(CancellationToken ct = default) =>
        new(await CurrentAsync(ct).ConfigureAwait(false));
}
