using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class HardwareAlertSourceCoordinator : IDisposable
{
    private static readonly Uri SnapshotUri =
        new("http://127.0.0.1:18765/snapshot");
    private static readonly Uri LibreHardwareMonitorUri =
        new("http://127.0.0.1:18085/data.json");
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(2);

    private readonly IOverlayPublisher publisher;
    private readonly HttpClient client = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Timer timer;
    private readonly NetworkConnectivityTracker networkTracker =
        new(reportInitialOffline: true);
    private DateTimeOffset? networkDownSince;
    private string? activeFindingKey;
    private string? activeSignature;
    private string? lastDiagnosticState;
    private int polling;
    private bool disposed;

    internal HardwareAlertSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        timer = new Timer(
            Poll,
            null,
            TimeSpan.Zero,
            PollInterval);
    }

    private async void Poll(object? state)
    {
        if (disposed || Interlocked.Exchange(ref polling, 1) != 0)
        {
            return;
        }

        try
        {
            var token = cancellation.Token;
            var now = DateTimeOffset.Now;
            var snapshotJson = await client.GetStringAsync(
                SnapshotUri,
                token);
            var networkStatus =
                SideScreenSnapshotParser.ParseNetworkLatencyStatus(
                    snapshotJson);
            UpdateNetworkState(
                NetworkConnectivityProbe.Classify(networkStatus),
                now);

            IReadOnlyList<double> pumpRpms = [];
            try
            {
                var lhmJson = await client.GetStringAsync(
                    LibreHardwareMonitorUri,
                    token);
                pumpRpms =
                    SideScreenSnapshotParser.ParsePumpRpms(lhmJson);
            }
            catch (HttpRequestException)
            {
                LogState("lhm-unavailable");
            }
            catch (TaskCanceledException)
            {
                LogState("lhm-timeout");
            }

            var telemetry = SideScreenSnapshotParser.ParseHardware(
                snapshotJson,
                pumpRpms,
                networkDownSince is null
                    ? null
                    : now - networkDownSince.Value);
            Publish(
                HardwareAlertEvaluator.Evaluate(telemetry)
                    .FirstOrDefault());
            LogState("healthy-snapshot");
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException)
        {
            LogState("snapshot-unavailable");
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Hardware alert probe failed: {exception.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private void UpdateNetworkState(
        NetworkConnectivityState state,
        DateTimeOffset now)
    {
        var transition = networkTracker.Observe(state);
        if (transition == NetworkConnectivityTransition.Restored)
        {
            networkDownSince = null;
        }
        else if (transition ==
                 NetworkConnectivityTransition.Disconnected)
        {
            networkDownSince ??= now;
        }
    }

    private void Publish(HardwareAlertFinding? finding)
    {
        if (finding is null)
        {
            if (activeFindingKey is null)
            {
                return;
            }

            var resolvedKey = activeFindingKey;
            _ = publisher.Publish(OverlayRequest.End(
                "hardware-alert",
                OverlayKind.HardwareAlert,
                OverlaySource.Hardware));
            _ = publisher.Publish(OverlayRequest.Timed(
                "hardware-resolved",
                OverlayKind.HardwareResolved,
                OverlaySource.Hardware,
                "硬件状态已恢复",
                "监测值已经回到安全范围",
                dedupKey: $"resolved:{resolvedKey}",
                visual: new OverlayVisualData(
                    Eyebrow: "已恢复",
                    AccentHex: "#83F3C1")));
            activeFindingKey = null;
            activeSignature = null;
            return;
        }

        var signature =
            $"{finding.Key}\u001f{finding.Body}\u001f{finding.SuggestedAction}";
        if (string.Equals(
                signature,
                activeSignature,
                StringComparison.Ordinal))
        {
            return;
        }

        _ = publisher.Publish(OverlayRequest.Active(
            "hardware-alert",
            OverlayKind.HardwareAlert,
            OverlaySource.Hardware,
            finding.Title,
            $"{finding.Body} · {finding.SuggestedAction}",
            dedupKey: finding.Key,
            visual: new OverlayVisualData(
                Eyebrow: "需要处理",
                AccentHex: "#FF8A7A")));
        activeFindingKey = finding.Key;
        activeSignature = signature;
    }

    private void LogState(string state)
    {
        if (string.Equals(
                state,
                lastDiagnosticState,
                StringComparison.Ordinal))
        {
            return;
        }

        lastDiagnosticState = state;
        RuntimeLog.Write($"Hardware alerts: {state}.");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        timer.Dispose();
        client.Dispose();
        cancellation.Dispose();
    }
}
