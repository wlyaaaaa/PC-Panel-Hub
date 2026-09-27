using System.Runtime.InteropServices;
using HS2.CrystalOverlay.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HS2_CrystalOverlay;

internal sealed class AudioDeviceSourceCoordinator : IDisposable
{
    private readonly IOverlayPublisher publisher;
    private readonly AutoResetEvent changed = new(false);
    private readonly Thread worker;
    private int disposeState;

    internal AudioDeviceSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        worker = new Thread(Run) { IsBackground = true, Name = "HS2 audio device worker" };
        worker.Start();
    }

    private void Run()
    {
        try { RunCore(); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            RuntimeLog.Write($"Audio device worker failed: {exception.GetType().Name}");
        }
    }

    private void RunCore()
    {
        var tracker = new AudioDeviceStateTracker();
        using var devices = new MMDeviceEnumerator();
        var notifications = new DefaultEndpointNotificationClient(() =>
        {
            if (Volatile.Read(ref disposeState) != 0) { return; }
            try { changed.Set(); }
            catch (ObjectDisposedException) { }
        });
        var registered = false;
        try
        {
            try
            {
                devices.RegisterEndpointNotificationCallback(notifications);
                registered = true;
            }
            catch (COMException exception)
            {
                RuntimeLog.Write($"Audio device notifications unavailable: {exception.GetType().Name}");
            }

            while (Volatile.Read(ref disposeState) == 0)
            {
                try
                {
                    using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    var request = tracker.Observe(device.ID, device.FriendlyName);
                    if (request is not null && Volatile.Read(ref disposeState) == 0)
                    {
                        _ = publisher.Publish(request);
                    }
                }
                catch (Exception exception) when (exception is COMException or InvalidOperationException)
                {
                    RuntimeLog.Write($"Audio device probe failed: {exception.GetType().Name}");
                }

                // Recover a missed endpoint callback without volume polling.
                changed.WaitOne(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            if (registered)
            {
                try { devices.UnregisterEndpointNotificationCallback(notifications); }
                catch (COMException exception)
                {
                    RuntimeLog.Write($"Audio device cleanup failed: {exception.GetType().Name}");
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0) { return; }
        changed.Set();
        worker.Join();
        changed.Dispose();
    }

    private sealed class DefaultEndpointNotificationClient(Action onChanged) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string deviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
        {
            if (flow == DataFlow.Render && role == Role.Console) { onChanged(); }
        }
    }
}

internal sealed class NetworkSourceCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(1);

    private readonly IOverlayPublisher publisher;
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly PeriodicTimer timer;
    private readonly Task loop;
    private readonly NetworkConnectivityTracker networkTracker = new();

    internal NetworkSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        timer = new PeriodicTimer(PollInterval);
        loop = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        try
        {
            PollOnce();
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                PollOnce();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PollOnce()
    {
        if (!publicationGate.IsClosed)
        {
            PublishNetworkChange(NetworkConnectivityProbe.Classify());
        }
    }

    private void PublishNetworkChange(NetworkConnectivityState state)
    {
        var transition = networkTracker.Observe(state);
        if (transition == NetworkConnectivityTransition.None)
        {
            return;
        }

        var online =
            transition == NetworkConnectivityTransition.Restored;

        PublishDevice(
            "network-state",
            online ? "network-restored" : "network-disconnected",
            online ? "网络已恢复" : "网络已断开",
            online
                ? "互联网连通性已经恢复"
                : "正在等待网络重新连接",
            online ? "#8EF2C8" : "#FFD08A");
    }

    private void PublishDevice(
        string eventId,
        string occurrenceKey,
        string title,
        string body,
        string accent)
    {
        _ = publicationGate.TryPublish(() => publisher.Publish(
            OverlayRequest.Timed(
            eventId,
            OverlayKind.DeviceOrNetwork,
            OverlaySource.System,
            title,
            body,
            dedupKey: occurrenceKey,
            visual: new OverlayVisualData(
                Eyebrow: "设备状态",
                AccentHex: accent))));
    }

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
            cancellation.Dispose();
            return;
        }

        _ = loop.ContinueWith(
            completedLoop =>
            {
                _ = completedLoop.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal static class NetworkConnectivityProbe
{
    internal static NetworkConnectivityState Classify()
    {
        bool? hasInternetAccess = null;
        bool? hasNetworkInterface = null;
        try
        {
            hasNetworkInterface =
                System.Net.NetworkInformation.NetworkInterface
                    .GetIsNetworkAvailable();
        }
        catch
        {
        }

        try
        {
            var profile =
                Windows.Networking.Connectivity.NetworkInformation
                    .GetInternetConnectionProfile();
            if (profile is null)
            {
                hasInternetAccess = false;
            }
            else
            {
                var level = profile.GetNetworkConnectivityLevel();
                hasInternetAccess =
                    level ==
                    Windows.Networking.Connectivity
                        .NetworkConnectivityLevel.InternetAccess;
            }
        }
        catch
        {
        }

        return NetworkConnectivityClassifier.Classify(
            hasInternetAccess,
            hasNetworkInterface);
    }
}
