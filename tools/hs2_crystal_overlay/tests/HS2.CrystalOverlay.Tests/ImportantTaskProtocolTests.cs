using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class ImportantTaskProtocolTests
{
    [Fact]
    public void ParsesExplicitLongTaskProgress()
    {
        var update = ImportantTaskProtocol.Parse("""
            {
              "id": "copy-models",
              "title": "复制模型",
              "detail": "V: → G:",
              "progress_percent": 42,
              "remaining_seconds": 180,
              "lease_seconds": 300,
              "state": "active"
            }
            """);

        Assert.Equal("copy-models", update?.Id);
        Assert.Equal(0.42, update?.Progress);
        Assert.Equal(TimeSpan.FromMinutes(3), update?.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(5), update?.Lease);
        Assert.Equal(ImportantTaskState.Active, update?.State);
    }

    [Fact]
    public void ProgressUsesExplicitUnitsWithoutBoundaryFlip()
    {
        var onePercent = ImportantTaskProtocol.Parse("""
            {"id":"one","title":"任务","progress_percent":1,"state":"active"}
            """);
        var twoPercent = ImportantTaskProtocol.Parse("""
            {"id":"two","title":"任务","progress_percent":2,"state":"active"}
            """);
        var normalized = ImportantTaskProtocol.Parse("""
            {"id":"normalized","title":"任务","progress":0.42,"state":"active"}
            """);
        var ambiguous = ImportantTaskProtocol.Parse("""
            {"id":"ambiguous","title":"任务","progress":42,"state":"active"}
            """);

        Assert.Equal(0.01, onePercent?.Progress);
        Assert.Equal(0.02, twoPercent?.Progress);
        Assert.Equal(0.42, normalized?.Progress);
        Assert.Null(ambiguous);
        Assert.Null(ImportantTaskProtocol.Parse("""
            {"id":"both","title":"任务","progress":0.42,"progress_percent":42,"state":"active"}
            """));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{\"id\":\"ok\",\"title\":\"任务\",\"progress\":\"42\",\"state\":\"active\"}")]
    public void NonObjectOrWrongTypedMessagesAreRejectedWithoutThrowing(
        string json)
    {
        var exception = Record.Exception(() =>
            ImportantTaskProtocol.Parse(json));

        Assert.Null(exception);
        Assert.Null(ImportantTaskProtocol.Parse(json));
    }

    [Theory]
    [InlineData("""{"id":"bad id","title":"任务","state":"active"}""")]
    [InlineData("""{"id":"ok","state":"active"}""")]
    [InlineData("""{"id":"ok","title":"任务","state":"mystery"}""")]
    [InlineData("not-json")]
    public void RejectsAmbiguousOrUnsafeMessages(string json)
    {
        Assert.Null(ImportantTaskProtocol.Parse(json));
    }
}
