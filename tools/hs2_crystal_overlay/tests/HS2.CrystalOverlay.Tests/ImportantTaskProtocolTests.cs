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
              "progress": 42,
              "remaining_seconds": 180,
              "state": "active"
            }
            """);

        Assert.Equal("copy-models", update?.Id);
        Assert.Equal(0.42, update?.Progress);
        Assert.Equal(TimeSpan.FromMinutes(3), update?.Remaining);
        Assert.Equal(ImportantTaskState.Active, update?.State);
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
