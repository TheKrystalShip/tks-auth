namespace TheKrystalShip.Auth.Access;

/// <summary>Why an action was refused.</summary>
public enum DenyReason
{
    /// <summary>Not refused.</summary>
    None = 0,

    /// <summary>The action id is not a well-formed action id.</summary>
    MalformedAction,

    /// <summary>No account has this id.</summary>
    NoAccount,

    /// <summary>The account is awaiting approval and holds nothing.</summary>
    AccountPending,

    /// <summary>The account is switched off.</summary>
    AccountDisabled,

    /// <summary>No application or provider has declared the action; only an Owner performs it.</summary>
    UnknownAction,

    /// <summary>No assignment or implicit role grants the action.</summary>
    NotGranted,
}

/// <summary>An answer to an access question: allowed, or refused with the reason.</summary>
/// <param name="Allowed">Whether the action may be performed.</param>
/// <param name="Reason">Why not, when it may not.</param>
/// <param name="Detail">The account or action the reason is about, for a message.</param>
public readonly record struct AccessDecision(bool Allowed, DenyReason Reason, string? Detail = null)
{
    /// <summary>Allowed.</summary>
    public static AccessDecision Allow { get; } = new(true, DenyReason.None);

    /// <summary>Refused, for <paramref name="reason"/>.</summary>
    public static AccessDecision Deny(DenyReason reason, string? detail = null) => new(false, reason, detail);
}

/// <summary>
/// The one function every access question goes through: may this account perform this action.
/// </summary>
/// <remarks>
/// <para>
/// The questions are asked in one order, and the order is the contract:
/// </para>
/// <list type="number">
/// <item>Is the account active? A pending account holds nothing; a disabled one does not authenticate.</item>
/// <item>Is the account an Owner? Allowed, including actions nobody has declared.</item>
/// <item>Is it a self action? Allowed.</item>
/// <item>Does any assignment grant it, or <c>everyone</c>? Allowed.</item>
/// </list>
/// <para>
/// There is no deny: every answer is a union of grants, so why somebody may do something is always
/// answered by listing the grants that allow it.
/// </para>
/// </remarks>
public sealed class AccessEvaluator(AuthoritySnapshot snapshot)
{
    /// <summary>The snapshot this evaluator answers from.</summary>
    public AuthoritySnapshot Snapshot { get; } = snapshot;

    /// <summary>Whether <paramref name="accountId"/> may perform <paramref name="action"/>.</summary>
    public AccessDecision Allows(string accountId, string action)
    {
        if (!ActionIds.IsValid(action))
            return AccessDecision.Deny(DenyReason.MalformedAction, action);

        // 1 · the account itself.
        if (!Snapshot.Accounts.TryGetValue(accountId, out AccessAccount? account))
            return AccessDecision.Deny(DenyReason.NoAccount, accountId);

        switch (account.Status)
        {
            case AccountStatus.Pending:
                return AccessDecision.Deny(DenyReason.AccountPending, accountId);
            case AccountStatus.Disabled:
                return AccessDecision.Deny(DenyReason.AccountDisabled, accountId);
        }

        // 2 · Owner holds everything, declared or not.
        if (Snapshot.IsOwner(accountId))
            return AccessDecision.Allow;

        if (!Snapshot.Catalog.TryGetValue(action, out CatalogAction? declared))
            return AccessDecision.Deny(DenyReason.UnknownAction, action);

        // 3 · a person's own records.
        if (declared.Self)
            return AccessDecision.Allow;

        // 4 · the union of what is granted.
        foreach (Assignment assignment in Snapshot.AssignmentsOf(accountId))
        {
            if (Snapshot.ActionsOf(assignment.RoleId).Contains(action))
                return AccessDecision.Allow;
        }

        return Snapshot.ActionsOf(BuiltInRoles.EveryoneId).Contains(action)
            ? AccessDecision.Allow
            : AccessDecision.Deny(DenyReason.NotGranted, action);
    }

    /// <summary>
    /// Whether <paramref name="accountId"/> may perform every action, including actions nobody has
    /// declared: an active Owner.
    /// </summary>
    /// <remarks>
    /// A catalog lists only declared actions, so this is the one answer a list of them cannot give.
    /// </remarks>
    public bool HoldsEverything(string accountId) =>
        Snapshot.Accounts.TryGetValue(accountId, out AccessAccount? account)
        && account.Status == AccountStatus.Active
        && Snapshot.IsOwner(accountId);

    /// <summary>Every catalog action <paramref name="accountId"/> may perform.</summary>
    public IReadOnlySet<string> EffectiveActions(string accountId)
    {
        HashSet<string> actions = new(StringComparer.Ordinal);

        foreach (string action in Snapshot.Catalog.Keys)
        {
            if (Allows(accountId, action).Allowed)
                actions.Add(action);
        }

        return actions;
    }
}
