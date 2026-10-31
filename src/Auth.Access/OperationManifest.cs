using System.Text.Json.Serialization;

namespace TheKrystalShip.Auth.Access;

/// <summary>
/// The operations a provider serves, and the action each one requires: what a client gates a control on.
/// </summary>
/// <remarks>
/// <para>
/// <b>A client names no action.</b> It knows the request it is about to make — the method, the path, the
/// body — because it has to make it, and it asks the provider that will answer that request which action
/// it needs and where. The provider builds this document from the same metadata it enforces with, so
/// nothing is enforced that is not published here, and a renamed action reaches every client the
/// moment the member serving it is deployed.
/// </para>
/// <para>
/// A caller's answer is still <c>GET /me/access</c>: this document says which action a request is, the
/// report says whether the caller holds it.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">The shape's version. A client that does not know it gates nothing open.</param>
/// <param name="Base">The path prefix every route is under, or empty.</param>
/// <param name="Operations">Every gated operation, in route order.</param>
public sealed record OperationManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("operations")] IReadOnlyList<Operation> Operations)
{
    /// <summary>The only schema version this shape is.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The document for <paramref name="operations"/>: methods upper-cased, routes with one leading slash
    /// and none trailing, duplicates dropped, sorted so two builds serve identical bytes.
    /// </summary>
    public static OperationManifest Of(string basePath, IEnumerable<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Operation[] normalized = [.. operations
            .Select(o => o with { Method = o.Method.ToUpperInvariant(), Route = NormalizeRoute(o.Route) })
            .Distinct()
            .OrderBy(o => o.Route, StringComparer.Ordinal)
            .ThenBy(o => o.Method, StringComparer.Ordinal)
            .ThenBy(o => o.Value ?? "", StringComparer.Ordinal)];
        string prefix = string.IsNullOrWhiteSpace(basePath) ? "" : NormalizeRoute(basePath);
        return new OperationManifest(CurrentSchemaVersion, prefix, normalized);
    }

    /// <summary>A route with exactly one leading slash and no trailing one.</summary>
    public static string NormalizeRoute(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        string trimmed = route.Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }
}

/// <summary>One request the provider serves, and what it takes to make it.</summary>
/// <param name="Method">The HTTP method, upper-case.</param>
/// <param name="Route">
/// The route template under the document's base, with parameters in braces (<c>/servers/{id}/backups</c>).
/// A client matches a concrete path against it segment by segment, and a literal segment is more
/// specific than a parameter.
/// </param>
/// <param name="Action">
/// The action the operation requires.
/// </param>
/// <param name="Field">
/// For a route that performs several actions, the body field that says which — an authority edit's
/// <c>kind</c>. Null when the route is one action.
/// </param>
/// <param name="Value">The value of <see cref="Field"/> this entry answers for.</param>
public sealed record Operation(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("field")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null,
    [property: JsonPropertyName("value")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null);
