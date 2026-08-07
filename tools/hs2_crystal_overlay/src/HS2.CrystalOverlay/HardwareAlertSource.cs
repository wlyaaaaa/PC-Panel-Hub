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
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly HttpClient client = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };
    private readonly CancellationTokenSource cancellation = new();
    private readonly PeriodicTimer timer;
    private readonly Task loop;
    private readonly NetworkConnectivityTracker networkTracker =
        new(reportInitialOffline: true);
    private readonly ConsecutiveEvidenceGate recoveryGate = new(3);
    private DateTimeOffset? networkDownSince;
    private HardwareAlertFinding? activeFinding;
    private string? activeSignature;
    private string? lastDiagnosticState;

    internal HardwareAlertSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
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
    }

    private async Task PollOnceAsync()
    {
        if (publicationGate.IsClosed)
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
            if (publicationGate.IsClosed)
            {
                return;
            }

            var networkStatus =
                SideScreenSnapshotParser.ParseNetworkLatencyStatus(
                    snapshotJson);
            UpdateNetworkState(
                NetworkConnectivityProbe.Classify(networkStatus),
                now);

            IReadOnlyList<double> pumpRpms = [];
            var pumpTelemetryAvailable = false;
            var diagnosticState = "healthy-snapshot";
            try
            {
                var lhmJson = await client.GetStringAsync(
                    LibreHardwareMonitorUri,
                    token);
                if (publicationGate.IsClosed)
                {
                    return;
                }

                pumpRpms =
                    SideScreenSnapshotParser.ParsePumpRpms(lhmJson);
                pumpTelemetryAvailable = pumpRpms.Count > 0;
            }
            catch (HttpRequestException)
            {
                diagnosticState = "lhm-unavailable";
            }
            catch (TaskCanceledException)
                when (!cancellation.IsCancellationRequested)
            {
                diagnosticState = "lhm-timeout";
            }

            var telemetry = SideScreenSnapshotParser.ParseHardware(
                snapshotJson,
                pumpRpms,
                networkDownSince is null
                    ? null
                    : now - networkDownSince.Value);
            var finding = HardwareAlertEvaluator.Evaluate(telemetry)
                .FirstOrDefault();
            var recoveryEvidenceAvailable =
                activeFinding is null ||
                HardwareAlertRecovery.HasRecoveryEvidence(
                    activeFinding,
                    telemetry,
                    pumpTelemetryAvailable);
            if (HardwareAlertContinuity.ShouldRetainActive(
                    activeFinding,
                    recoveryEvidenceAvailable,
                    finding))
            {
                recoveryGate.Reset();
            }
            else if (finding is not null)
            {
                recoveryGate.Reset();
                Publish(finding);
            }
            else if (activeFinding is null)
            {
                recoveryGate.Reset();
            }
            else if (recoveryGate.Observe(
                         recoveryEvidenceAvailable))
            {
                Publish(null);
            }

            LogState(diagnosticState);
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
            if (activeFinding is null)
            {
                return;
            }

            var resolvedKey = activeFinding.Key;
            _ = PublishIfActive(OverlayRequest.Timed(
                "hardware-alert",
                OverlayKind.HardwareResolved,
                OverlaySource.Hardware,
                "硬件状态已恢复",
                "监测值已经回到安全范围",
                dedupKey: $"resolved:{resolvedKey}",
                visual: new OverlayVisualData(
                    Eyebrow: "已恢复",
                    AccentHex: "#83F3C1")));
            activeFinding = null;
            activeSignature = null;
            return;
        }

        var signature =
            $"{finding.Key}\u001f{finding.EvidenceKey}\u001f{finding.Title}" +
            $"\u001f{finding.Body}\u001f{finding.SuggestedAction}";
        if (string.Equals(
                signature,
                activeSignature,
                StringComparison.Ordinal))
        {
            return;
        }

        _ = PublishIfActive(OverlayRequest.Active(
            "hardware-alert",
            OverlayKind.HardwareAlert,
            OverlaySource.Hardware,
            finding.Title,
            $"{finding.Body} · {finding.SuggestedAction}",
            dedupKey: finding.Key,
            visual: new OverlayVisualData(
                Eyebrow: "需要处理",
                AccentHex: "#FF8A7A")));
        activeFinding = finding;
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

    private bool PublishIfActive(OverlayRequest request) =>
        publicationGate.TryPublish(() => publisher.Publish(request));

    public void Dispose()
    {
        if (!publicationGate.Close())
        {
            return;
        }

        cancellation.Cancel();
        timer.Dispose();
        var completed = false;
        try
        {
            completed = loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        if (completed)
        {
            client.Dispose();
            cancellation.Dispose();
            return;
        }

        _ = loop.ContinueWith(
            completedLoop =>
            {
                _ = completedLoop.Exception;
                client.Dispose();
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
