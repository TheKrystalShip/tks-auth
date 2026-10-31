using System.Text.Json.Serialization;

namespace TheKrystalShip.Auth.Access;

/// <summary>
/// What <c>GET /me/access</c> answers: the caller's effective actions, already evaluated.
/// </summary>
/// <remarks>
/// A surface gates a control by looking an action up here. It holds no copy of the rules, so it cannot
/// disagree with them, and a control for an action the report does not name is closed.
/// </remarks>
/// <param name="Version">The authority version the answer was evaluated at.</param>
/// <param name="Actions">The actions the caller may perform.</param>
/// <param name="Owner">
/// Whether the caller may perform every action, including actions nobody declares and so no list here
/// can name: an active Owner.
/// </param>
public sealed record AccessReport(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
    [property: JsonPropertyName("owner")] bool Owner = false)
{
    /// <summary>
    /// The report for <paramref name="accountId"/>, listing only the actions <paramref name="include"/>
    /// accepts.
    /// </summary>
    public static AccessReport For(AccessEvaluator evaluator, string accountId, Func<string, bool> include)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(include);

        return new AccessReport(
            evaluator.Snapshot.Version,
            [.. evaluator.EffectiveActions(accountId).Where(include).Order(StringComparer.Ordinal)],
            evaluator.HoldsEverything(accountId));
    }
}
