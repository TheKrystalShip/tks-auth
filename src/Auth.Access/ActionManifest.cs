using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Auth.Access;

/// <summary>
/// One component's action manifest, as an application serves it.
/// </summary>
/// <remarks>
/// The format's authority is <c>kgsm-docs/reference/action-manifest.md</c>. Ids here are local; the
/// wire id is <c>&lt;Component&gt;:&lt;id&gt;</c>.
/// </remarks>
public sealed record ActionManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("actions")] IReadOnlyList<ManifestAction>? Actions)
{
    /// <summary>The one schema version this build reads.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The actions this manifest declares, as catalog actions with their full ids.</summary>
    /// <remarks>An entry that is not a well-formed action is left out rather than guessed at.</remarks>
    public IEnumerable<CatalogAction> CatalogActions()
    {
        foreach (ManifestAction action in Actions ?? [])
        {
            string id = $"{Component}:{action.Id}";
            if (!ActionIds.IsValid(id) || string.IsNullOrWhiteSpace(action.Title))
                continue;

            yield return new CatalogAction(
                id, action.Title.Trim(), ActionEffects.Parse(action.Effect), action.Self ?? false);
        }
    }
}

/// <summary>One action a manifest declares.</summary>
/// <summary>
/// The document an application serves at its manifest address: one component's manifest, or several.
/// </summary>
/// <remarks>
/// <para>
/// The single form is an <see cref="ActionManifest"/> read as it stands: the application's actions in its
/// own namespace. The plural form carries <see cref="Manifests"/>, one manifest per component, for an
/// application whose actions span several namespaces; its own top-level component and actions are then
/// not read.
/// </para>
/// <para>
/// The namespaces an application owns are the components its manifests declare, and its id.
/// </para>
/// </remarks>
public sealed record ApplicationManifestDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("component")] string? Component,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("actions")] IReadOnlyList<ManifestAction>? Actions,
    [property: JsonPropertyName("manifests")] IReadOnlyList<ActionManifest>? Manifests)
{
    /// <summary>Whether this is the plural form.</summary>
    public bool IsPlural => Manifests is not null;

    /// <summary>The manifests the document carries: its entries, or itself as the one.</summary>
    public IReadOnlyList<ActionManifest> Parts() =>
        Manifests ?? (Component is null ? [] : [new ActionManifest(SchemaVersion, Component, Version, Actions)]);
}

public sealed record ManifestAction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("effect")] string? Effect,
    [property: JsonPropertyName("self")] bool? Self);

/// <summary>
/// What one source of actions declares: every manifest it holds.
/// </summary>
/// <remarks>
/// An application's report is the manifests its document carries; the provider's own is its
/// <c>auth</c> manifest. A report replaces its source's previous one wholesale, so a component missing
/// from it is a component that source no longer declares.
/// </remarks>
/// <param name="Manifests">Every manifest the source holds.</param>
/// <param name="Sequence">
/// When the source was read, in Unix milliseconds. A report older than the one held is ignored rather
/// than rolling the catalog back.
/// </param>
public sealed record CatalogReport(
    [property: JsonPropertyName("manifests")] IReadOnlyList<ActionManifest> Manifests,
    [property: JsonPropertyName("sequence")] long Sequence = 0);

/// <summary>How the catalog names the source an application's report is held under.</summary>
public static class ApplicationNamespaces
{
    /// <summary>What an application's report is held under in the catalog, before its id.</summary>
    public const string SourcePrefix = "application:";

    /// <summary>What the provider's own report is held under in the catalog.</summary>
    public const string ProviderSource = "provider";

    /// <summary>The application a catalog source names, or null when the source is not an application's.</summary>
    public static string? ApplicationOf(string source) =>
        source.StartsWith(SourcePrefix, StringComparison.Ordinal) ? source[SourcePrefix.Length..] : null;
}

/// <summary>Reads an action manifest from disk.</summary>
public static class ActionManifests
{
    /// <summary>
    /// The manifest at <paramref name="path"/>, or <see langword="null"/> with the reason when it cannot
    /// be read, does not parse, or is a schema version this build does not know.
    /// </summary>
    public static ActionManifest? TryRead(string path, out string? problem)
    {
        problem = null;
        try
        {
            ActionManifest? manifest = JsonSerializer.Deserialize(
                File.ReadAllText(path), AccessJsonContext.Default.ActionManifest);

            if (manifest is null)
                problem = "empty";
            else if (manifest.SchemaVersion != ActionManifest.SupportedSchemaVersion)
                problem = $"schema version {manifest.SchemaVersion}, and this build reads {ActionManifest.SupportedSchemaVersion}";
            else if (!ActionIds.IsValid(manifest.Component + ":x"))
                problem = $"'{manifest.Component}' is not a component namespace";
            else
                return manifest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = e.Message;
        }

        return null;
    }
}

/// <summary>Source-generated JSON for the action manifests, the stored reports and what the provider answers about a caller's actions.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ActionManifest))]
[JsonSerializable(typeof(ApplicationManifestDocument))]
[JsonSerializable(typeof(CatalogReport))]
[JsonSerializable(typeof(AccessReport))]
[JsonSerializable(typeof(OperationManifest))]
public sealed partial class AccessJsonContext : JsonSerializerContext;
