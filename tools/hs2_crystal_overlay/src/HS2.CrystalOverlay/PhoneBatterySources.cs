using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal interface IPhoneBatteryProbe
{
    Task<PhoneBatteryReading?> ReadAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed class PhoneBatterySourceCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SourceTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumReadingAge =
        TimeSpan.FromSeconds(15);

    private readonly IOverlayPublisher publisher;
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly IPhoneBatteryProbe? probe;
    private readonly PhoneBatteryProbeSingleFlight<
        PhoneBatteryReading?> probeFlight = new();
    private readonly PhoneBatteryProbeReadingCache probeReadingCache =
        new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly PeriodicTimer timer;
    private readonly Task loop;
    private PhoneBatteryReading? published;
    private string? lastSourceState;
    private bool baselineCaptured;

    internal PhoneBatterySourceCoordinator(
        IOverlayPublisher publisher,
        IPhoneBatteryProbe? probe)
    {
        this.publisher = publisher;
        this.probe = probe;
        timer = new PeriodicTimer(PollInterval);
        loop = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        try
        {
            await PollOnceAsync();
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                await PollOnceAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Phone battery poll loop failed: " +
                $"{exception.GetType().Name}");
        }
    }

    private async Task PollOnceAsync()
    {
        if (publicationGate.IsClosed)
        {
            return;
        }

        var token = cancellation.Token;
        var probeAttemptTask = probeFlight.ObserveAsync(
            sourceToken => probe?.ReadAsync(
                DateTimeOffset.Now,
                sourceToken) ?? Task.FromResult<PhoneBatteryReading?>(null),
            SourceTimeout,
            token);
        var probeAttempt = await probeAttemptTask;
        if (publicationGate.IsClosed)
        {
            return;
        }

        // The cache preserves each source's own evidence timestamp. It retains
        // a recent confirmed value through timeout/fault jitter and only clears
        // it when a completed successful probe explicitly returns no phone.
        var probeReading = probeReadingCache.Apply(probeAttempt);
        var now = DateTimeOffset.Now;
        var selected = PhoneBatterySelection.Select(
            probeReading,
            now,
            MaximumReadingAge);
        LogSourceState(
            probeAttempt,
            probeReading,
            selected);
        PublishSelection(selected);
    }

    private void LogSourceState(
        PhoneBatteryProbeAttempt<PhoneBatteryReading?> probeAttempt,
        PhoneBatteryReading? probeReading,
        PhoneBatteryReading? selected)
    {
        var state =
            $"probe={DescribeAttempt(probeAttempt, probeReading)}; " +
            $"selected={selected?.Provider.ToString() ?? "none"}";
        if (string.Equals(
                state,
                lastSourceState,
                StringComparison.Ordinal))
        {
            return;
        }

        lastSourceState = state;
        RuntimeLog.Write($"Phone battery sources: {state}.");
    }

    private static string DescribeAttempt(
        PhoneBatteryProbeAttempt<PhoneBatteryReading?> attempt,
        PhoneBatteryReading? reading) => attempt.Status switch
    {
        PhoneBatteryProbeAttemptStatus.Succeeded =>
            DescribeReading(reading),
        PhoneBatteryProbeAttemptStatus.TimedOut => "timeout",
        PhoneBatteryProbeAttemptStatus.Faulted =>
            $"failed:{attempt.Error?.GetType().Name ?? "unknown"}",
        PhoneBatteryProbeAttemptStatus.Canceled => "canceled",
        PhoneBatteryProbeAttemptStatus.Stopped => "stopped",
        _ => "unknown",
    };

    private static string DescribeReading(PhoneBatteryReading? reading) =>
        reading is null
            ? "none"
            : $"{reading.Percentage}%/" +
              $"{reading.Evidence ?? "unknown"}/" +
              $"charging={reading.IsCharging?.ToString() ?? "unknown"}";

    private void PublishSelection(PhoneBatteryReading? selected)
    {
        if (selected is null)
        {
            if (published is not null)
            {
                var previous = published;
                _ = PublishIfActive(OverlayRequest.End(
                    "phone-battery",
                    OverlayKind.PhoneBattery,
                    SourceFor(previous.Provider)));
                if (baselineCaptured)
                {
                    PublishConnection(previous.Provider, connected: false);
                }

                published = null;
            }

            baselineCaptured = true;
            return;
        }

        if (published is not null &&
            published.Provider == selected.Provider &&
            published.Percentage == selected.Percentage &&
            published.IsCharging == selected.IsCharging)
        {
            baselineCaptured = true;
            return;
        }

        if (published is null && baselineCaptured)
        {
            PublishConnection(selected.Provider, connected: true);
        }

        _ = PublishIfActive(OverlayRequest.Active(
            "phone-battery",
            OverlayKind.PhoneBattery,
            SourceFor(selected.Provider),
            $"{selected.Percentage}%",
            visual: new OverlayVisualData(
                IsCharging: selected.IsCharging)));
        published = selected;
        baselineCaptured = true;
    }

    private void PublishConnection(
        PhoneBatteryProvider provider,
        bool connected)
    {
        var sourceName = provider switch
        {
            PhoneBatteryProvider.AndroidAdb => "Android ADB",
            _ => "手机",
        };
        _ = PublishIfActive(OverlayRequest.Timed(
            connected ? "phone-connected" : "phone-disconnected",
            OverlayKind.PhoneConnection,
            SourceFor(provider),
            connected ? "手机已连接" : "手机已断开",
            sourceName,
            dedupKey:
                $"{provider}:{(connected ? "connected" : "disconnected")}",
            visual: new OverlayVisualData(
                Eyebrow: connected
                    ? "连接成功 / CONNECTED"
                    : "连接断开 / DISCONNECTED",
                AccentHex: connected ? "#8EF2C8" : "#FFD08A")));
    }

    private bool PublishIfActive(OverlayRequest request) =>
        publicationGate.TryPublish(() => publisher.Publish(request));

    private static OverlaySource SourceFor(
        PhoneBatteryProvider provider) => provider switch
        {
            PhoneBatteryProvider.AndroidAdb => OverlaySource.AndroidAdb,
            _ => OverlaySource.System,
        };

    public void Dispose()
    {
        if (!publicationGate.Close())
        {
            return;
        }

        cancellation.Cancel();
        timer.Dispose();
        var termination = WaitForTerminationAsync(
            probeFlight.StopAsync());
        var completed = false;
        try
        {
            completed = termination.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        if (completed)
        {
            cancellation.Dispose();
            return;
        }

        _ = termination.ContinueWith(
            completedTermination =>
            {
                _ = completedTermination.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForTerminationAsync(Task probeStop)
    {
        try
        {
            await loop;
        }
        catch
        {
        }

        try
        {
            await probeStop;
        }
        catch
        {
        }

    }
}
