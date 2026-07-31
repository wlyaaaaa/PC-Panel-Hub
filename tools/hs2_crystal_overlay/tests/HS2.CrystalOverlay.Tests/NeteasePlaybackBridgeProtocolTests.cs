using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class NeteasePlaybackBridgeProtocolTests
{
    [Fact]
    public void RoundTripAcceptsFreshExpectedProcess()
    {
        var now = DateTimeOffset.Parse("2026-07-31T03:00:00Z");
        var json = NeteasePlaybackBridgeProtocol.Serialize(
            new NeteasePlaybackMemorySample(
                TimeSpan.FromSeconds(171.25),
                42),
            now);

        var sample = NeteasePlaybackBridgeProtocol.Parse(
            json,
            new HashSet<int> { 42 },
            now.AddMilliseconds(200),
            TimeSpan.FromSeconds(2));

        Assert.NotNull(sample);
        Assert.Equal(42, sample.ProcessId);
        Assert.Equal(171.25, sample.Position.TotalSeconds, 3);
    }

    [Fact]
    public void RejectsStaleOrUnexpectedProcess()
    {
        var observed = DateTimeOffset.Parse(
            "2026-07-31T03:00:00Z");
        var json = NeteasePlaybackBridgeProtocol.Serialize(
            new NeteasePlaybackMemorySample(
                TimeSpan.FromSeconds(12),
                42),
            observed);

        Assert.Null(NeteasePlaybackBridgeProtocol.Parse(
            json,
            new HashSet<int> { 99 },
            observed.AddMilliseconds(100),
            TimeSpan.FromSeconds(2)));
        Assert.Null(NeteasePlaybackBridgeProtocol.Parse(
            json,
            new HashSet<int> { 42 },
            observed.AddSeconds(3),
            TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{not-json}")]
    [InlineData(
        """{"schema_version":1,"observed_unix_ms":0,"process_id":42,"position_ms":-1}""")]
    public void RejectsInvalidPayloads(string json)
    {
        Assert.Null(NeteasePlaybackBridgeProtocol.Parse(
            json,
            new HashSet<int> { 42 },
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(2)));
    }
}
