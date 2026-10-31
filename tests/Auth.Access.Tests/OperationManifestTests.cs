using System.Text.Json;

namespace TheKrystalShip.Auth.Access.Tests;

/// <summary>The operations document: one spelling of every route, whatever order they were found in.</summary>
public class OperationManifestTests
{
    [Fact]
    public void RoutesAndMethodsAreNormalizedAndDuplicatesDropped()
    {
        OperationManifest m = OperationManifest.Of("api/v1/", [
            new Operation("post", "servers/{id}/backups/", "kgsm:server.backups.create"),
            new Operation("POST", "/servers/{id}/backups", "kgsm:server.backups.create"),
            new Operation("get", "/alerts", "api:alerts.read"),
        ]);

        Assert.Equal("/api/v1", m.Base);
        Assert.Equal(2, m.Operations.Count);
        Assert.Equal(new Operation("GET", "/alerts", "api:alerts.read"), m.Operations[0]);
        Assert.Equal("POST", m.Operations[1].Method);
        Assert.Equal("/servers/{id}/backups", m.Operations[1].Route);
    }

    [Fact]
    public void ARouteOfSeveralActionsIsOneEntryPerValue_InValueOrder()
    {
        OperationManifest m = OperationManifest.Of("", [
            new Operation("POST", "/servers/{id}/commands", "kgsm:server.stop", "verb", "stop"),
            new Operation("POST", "/servers/{id}/commands", "kgsm:server.restart", "verb", "restart"),
        ]);

        Assert.Equal(["restart", "stop"], m.Operations.Select(o => o.Value));
        Assert.Equal("", m.Base);
    }

    [Fact]
    public void TheWireShapeIsCamelCaseAndOmitsWhatIsAbsent()
    {
        string json = JsonSerializer.Serialize(
            OperationManifest.Of("/api/v1", [new Operation("GET", "/alerts", "api:alerts.read")]),
            AccessJsonContext.Default.OperationManifest);

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"route\":\"/alerts\"", json);
        Assert.DoesNotContain("\"field\"", json);
    }
}
