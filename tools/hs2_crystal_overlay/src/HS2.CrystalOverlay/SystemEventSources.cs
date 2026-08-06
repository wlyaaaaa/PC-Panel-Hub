using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HS2.CrystalOverlay.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HS2_CrystalOverlay;

internal sealed class AudioOperationSourceCoordinator : IDisposable
{
    private static readonly TimeSpan RecoveryInterval =
        TimeSpan.FromSeconds(2);

    private readonly IOverlayPublisher publisher;
    private readonly BlockingCollection<AudioCommand> commands = new();
    private readonly DefaultEndpointNotificationClient deviceNotifications;
    private readonly Thread worker;
    private int disposeState;

    internal AudioOperationSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        deviceNotifications = new DefaultEndpointNotificationClient(
            () => Enqueue(RebindAudioCommand.Instance));
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "HS2 audio endpoint worker",
        };
        worker.Start();
    }

    private void Run()
    {
        MMDeviceEnumerator? devices = null;
        MMDevice? currentDevice = null;
        AudioEndpointVolume? endpointVolume = null;
        AudioEndpointVolumeNotificationDelegate? volumeHandler = null;
        var notificationsRegistered = false;
        var tracker = new AudioHudStateTracker();
        long activeGeneration = 0;

        try
        {
            devices = new MMDeviceEnumerator();
            try
            {
                devices.RegisterEndpointNotificationCallback(
                    deviceNotifications);
                notificationsRegistered = true;
            }
            catch (COMException exception)
            {
                RuntimeLog.Write(
                    $"Audio endpoint notifications unavailable: " +
                    exception.GetType().Name);
            }

            BindDefaultEndpoint();
            while (Volatile.Read(ref disposeState) == 0)
            {
                if (commands.TryTake(
                        out var command,
                        RecoveryInterval))
                {
                    if (Volatile.Read(ref disposeState) != 0)
                    {
                        break;
                    }

                    switch (command)
                    {
                        case RebindAudioCommand:
                            BindDefaultEndpoint();
                            break;
                        case VolumeChangedAudioCommand changed
                            when changed.Generation == activeGeneration:
                            Publish(tracker.Observe(
                                changed.VolumePercent,
                                changed.IsMuted));
                            break;
                    }
                }
                else if (Volatile.Read(ref disposeState) == 0)
                {
                    Reconcile();
                }
            }
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            RuntimeLog.Write(
                $"Audio endpoint worker failed: " +
                exception.GetType().Name);
        }
        finally
        {
            activeGeneration++;
            DetachEndpoint();
            if (notificationsRegistered && devices is not null)
            {
                try
                {
                    devices.UnregisterEndpointNotificationCallback(
                        deviceNotifications);
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidOperationException)
                {
                    RuntimeLog.Write(
                        $"Audio endpoint notification cleanup failed: " +
                        exception.GetType().Name);
                }
            }

            if (devices is not null)
            {
                try
                {
                    devices.Dispose();
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidOperationException)
                {
                    RuntimeLog.Write(
                        $"Audio enumerator cleanup failed: " +
                        exception.GetType().Name);
                }
            }
        }

        void BindDefaultEndpoint()
        {
            if (devices is null || Volatile.Read(ref disposeState) != 0)
            {
                return;
            }

            activeGeneration++;
            DetachEndpoint();
            MMDevice? replacementDevice = null;
            AudioEndpointVolume? replacementVolume = null;
            AudioEndpointVolumeNotificationDelegate? replacementHandler =
                null;
            try
            {
                replacementDevice = devices.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Console);
                replacementVolume = replacementDevice.AudioEndpointVolume;
                var generation = activeGeneration;
                replacementHandler =
                    notification => Enqueue(
                        new VolumeChangedAudioCommand(
                            generation,
                            AudioHudProjection.ToPercent(
                                notification.MasterVolume),
                            notification.Muted));
                replacementVolume.OnVolumeNotification +=
                    replacementHandler;
                var initialPercent = AudioHudProjection.ToPercent(
                    replacementVolume.MasterVolumeLevelScalar);
                var initialMute = replacementVolume.Mute;
                currentDevice = replacementDevice;
                endpointVolume = replacementVolume;
                volumeHandler = replacementHandler;
                replacementDevice = null;
                replacementVolume = null;
                replacementHandler = null;
                Publish(tracker.Observe(
                    initialPercent,
                    initialMute));
            }
            catch (Exception exception) when (
                exception is COMException or InvalidOperationException)
            {
                RuntimeLog.Write(
                    $"Audio endpoint bind failed: " +
                    exception.GetType().Name);
            }
            finally
            {
                ReleaseEndpoint(
                    replacementVolume,
                    replacementHandler,
                    replacementDevice);
            }
        }

        void Reconcile()
        {
            if (devices is null || Volatile.Read(ref disposeState) != 0)
            {
                return;
            }

            try
            {
                using var defaultDevice =
                    devices.GetDefaultAudioEndpoint(
                        DataFlow.Render,
                        Role.Console);
                if (endpointVolume is null ||
                    !string.Equals(
                        currentDevice?.ID,
                        defaultDevice.ID,
                        StringComparison.Ordinal))
                {
                    BindDefaultEndpoint();
                    return;
                }

                Publish(tracker.Observe(
                    AudioHudProjection.ToPercent(
                        endpointVolume.MasterVolumeLevelScalar),
                    endpointVolume.Mute));
            }
            catch (Exception exception) when (
                exception is COMException or InvalidOperationException)
            {
                RuntimeLog.Write(
                    $"Audio endpoint recovery failed: " +
                    exception.GetType().Name);
            }
        }

        void DetachEndpoint()
        {
            var detachedVolume = endpointVolume;
            var detachedHandler = volumeHandler;
            var detachedDevice = currentDevice;
            endpointVolume = null;
            volumeHandler = null;
            currentDevice = null;
            ReleaseEndpoint(
                detachedVolume,
                detachedHandler,
                detachedDevice);
        }

        void ReleaseEndpoint(
            AudioEndpointVolume? volume,
            AudioEndpointVolumeNotificationDelegate? handler,
            MMDevice? device)
        {
            if (volume is not null && handler is not null)
            {
                try
                {
                    volume.OnVolumeNotification -= handler;
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidOperationException)
                {
                    RuntimeLog.Write(
                        $"Audio endpoint unsubscribe failed: " +
                        exception.GetType().Name);
                }
            }

            if (volume is not null)
            {
                try
                {
                    volume.Dispose();
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidOperationException)
                {
                    RuntimeLog.Write(
                        $"Audio endpoint cleanup failed: " +
                        exception.GetType().Name);
                }
            }

            if (device is not null)
            {
                try
                {
                    device.Dispose();
                }
                catch (Exception exception) when (
                    exception is COMException or InvalidOperationException)
                {
                    RuntimeLog.Write(
                        $"Audio device cleanup failed: " +
                        exception.GetType().Name);
                }
            }
        }
    }

    private void Publish(OverlayRequest? request)
    {
        if (request is not null &&
            Volatile.Read(ref disposeState) == 0)
        {
            _ = publisher.Publish(request);
        }
    }

    private void Enqueue(AudioCommand command)
    {
        if (Volatile.Read(ref disposeState) != 0)
        {
            return;
        }

        try
        {
            _ = commands.TryAdd(command);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        commands.CompleteAdding();
        if (Thread.CurrentThread != worker)
        {
            worker.Join();
        }

        commands.Dispose();
    }

    private sealed class DefaultEndpointNotificationClient(
        Action onDefaultDeviceChanged) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(
            string deviceId,
            DeviceState newState)
        {
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnDefaultDeviceChanged(
            DataFlow flow,
            Role role,
            string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Console)
            {
                onDefaultDeviceChanged();
            }
        }

        public void OnPropertyValueChanged(
            string pwstrDeviceId,
            PropertyKey key)
        {
        }
    }

    private abstract record AudioCommand;

    private sealed record RebindAudioCommand : AudioCommand
    {
        internal static RebindAudioCommand Instance { get; } = new();
    }

    private sealed record VolumeChangedAudioCommand(
        long Generation,
        int VolumePercent,
        bool IsMuted) : AudioCommand;
}

internal sealed class DeviceNetworkSourceCoordinator : IDisposable
{
    private static readonly Uri SnapshotUri =
        new("http://127.0.0.1:18765/snapshot");
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(1);

    private readonly IOverlayPublisher publisher;
    private readonly HttpClient client = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Timer timer;
    private readonly NetworkConnectivityTracker networkTracker = new();
    private IReadOnlyDictionary<string, UsbStorageDevice>? previousUsb;
    private int polling;
    private bool disposed;

    internal DeviceNetworkSourceCoordinator(IOverlayPublisher publisher)
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
            var json = await client.GetStringAsync(
                SnapshotUri,
                cancellation.Token);
            PublishUsbChanges(
                SideScreenSnapshotParser.ParseUsbDevices(json));
            PublishNetworkChange(
                NetworkConnectivityProbe.Classify(
                    SideScreenSnapshotParser.ParseNetworkLatencyStatus(
                        json)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException)
        {
            // A missing local telemetry service is not proof of a network
            // outage, so it must not create a false user-facing event.
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Device/network probe failed: {exception.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private void PublishUsbChanges(
        IReadOnlyList<UsbStorageDevice> devices)
    {
        var current = devices.ToDictionary(
            device => device.Key,
            StringComparer.OrdinalIgnoreCase);
        if (previousUsb is null)
        {
            previousUsb = current;
            return;
        }

        foreach (var device in current.Values.Where(device =>
                     !previousUsb.ContainsKey(device.Key)))
        {
            PublishDevice(
                $"usb-connected:{device.Key}",
                "USB 存储已接入",
                DeviceText(device),
                "#8EF2C8");
        }

        foreach (var device in previousUsb.Values.Where(device =>
                     !current.ContainsKey(device.Key)))
        {
            PublishDevice(
                $"usb-disconnected:{device.Key}",
                "USB 存储已断开",
                DeviceText(device),
                "#FFD08A");
        }

        previousUsb = current;
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
            online ? "network-restored" : "network-disconnected",
            online ? "网络已恢复" : "网络已断开",
            online
                ? "互联网连通性已经恢复"
                : "正在等待网络重新连接",
            online ? "#8EF2C8" : "#FFD08A");
    }

    private void PublishDevice(
        string eventId,
        string title,
        string body,
        string accent)
    {
        _ = publisher.Publish(OverlayRequest.Timed(
            eventId,
            OverlayKind.DeviceOrNetwork,
            OverlaySource.System,
            title,
            body,
            dedupKey: eventId,
            visual: new OverlayVisualData(
                Eyebrow: "设备状态",
                AccentHex: accent)));
    }

    private static string DeviceText(UsbStorageDevice device)
    {
        var volumes = device.VolumeDrives.Count == 0
            ? null
            : string.Join(
                " / ",
                device.VolumeDrives.Select(volume =>
                    volume.TrimEnd('\\')));
        return string.IsNullOrWhiteSpace(volumes)
            ? device.DisplayName
            : $"{device.DisplayName}  ·  {volumes}";
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

internal static class NetworkConnectivityProbe
{
    internal static NetworkConnectivityState Classify(
        string? latencyStatus)
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
            latencyStatus,
            hasInternetAccess,
            hasNetworkInterface);
    }
}
