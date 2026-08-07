using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class ImportantTaskProjectionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveRunClearsOldCompletionBeforePublishingProgress()
    {
        var requests = ImportantTaskProjection.Project(new(
            "copy",
            "复制文件",
            "V: → G:",
            0.42,
            TimeSpan.FromMinutes(3),
            ImportantTaskState.Active));

        Assert.Collection(
            requests,
            request =>
            {
                Assert.False(request.IsActive);
                Assert.Equal(
                    "important-task-complete:copy",
                    request.EventId);
            },
            request =>
            {
                Assert.True(request.IsActive);
                Assert.Equal("important-task:copy", request.EventId);
                Assert.Equal(0.42, request.Visual?.Progress);
            });
    }

    [Fact]
    public void CancellationClearsBothActiveAndOldCompletionCards()
    {
        var requests = ImportantTaskProjection.Project(new(
            "copy",
            "复制文件",
            null,
            null,
            null,
            ImportantTaskState.Cancelled));

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.False(request.IsActive));
        Assert.Contains(requests, request =>
            request.EventId == "important-task:copy");
        Assert.Contains(requests, request =>
            request.EventId == "important-task-complete:copy");
    }

    [Fact]
    public void ZeroEtaDoesNotClaimOneMinuteRemaining()
    {
        var request = ImportantTaskProjection.Project(new(
            "copy",
            "复制文件",
            null,
            1,
            TimeSpan.Zero,
            ImportantTaskState.Active))[1];

        Assert.Equal("即将完成", request.Visual?.Meta);
    }

    [Fact]
    public void LeaseRegistryExpiresSilentProducerAndRefreshesHeartbeat()
    {
        var leases = new ImportantTaskLeaseRegistry(
            TimeSpan.FromMinutes(5));
        leases.Refresh("copy", Now, requestedLease: null);

        Assert.Empty(leases.CollectExpired(
            Now.AddMinutes(4).AddSeconds(59)));
        leases.Refresh("copy", Now.AddMinutes(4), requestedLease: null);
        Assert.Empty(leases.CollectExpired(Now.AddMinutes(8)));
        Assert.Equal(
            ["copy"],
            leases.CollectExpired(Now.AddMinutes(9)));
        Assert.Empty(leases.CollectExpired(Now.AddMinutes(10)));
    }

    [Fact]
    public async Task ExpiryCallbackSerializesWithConcurrentHeartbeat()
    {
        var leases = new ImportantTaskLeaseRegistry(
            TimeSpan.FromMinutes(5));
        leases.Refresh("copy", Now, requestedLease: null);
        using var expiryEntered = new ManualResetEventSlim();
        using var releaseExpiry = new ManualResetEventSlim();
        using var heartbeatStarted = new ManualResetEventSlim();

        var expiry = Task.Run(() => leases.Expire(
            Now.AddMinutes(5),
            _ =>
            {
                expiryEntered.Set();
                releaseExpiry.Wait();
            }));
        Assert.True(expiryEntered.Wait(TimeSpan.FromSeconds(1)));
        var heartbeat = Task.Run(() =>
        {
            heartbeatStarted.Set();
            leases.Refresh("copy", Now.AddMinutes(5), requestedLease: null);
        });
        Assert.True(heartbeatStarted.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(heartbeat.IsCompleted);

        releaseExpiry.Set();
        await Task.WhenAll(expiry, heartbeat);
        Assert.Empty(leases.CollectExpired(
            Now.AddMinutes(9).AddSeconds(59)));
        Assert.Equal(
            ["copy"],
            leases.CollectExpired(Now.AddMinutes(10)));
    }
}
