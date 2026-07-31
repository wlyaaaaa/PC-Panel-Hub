using System.Runtime.InteropServices;
using HS2.CrystalOverlay.Core;
using NAudio.CoreAudioApi;

namespace HS2_CrystalOverlay;

internal sealed record AudioOutputState(
    string DeviceId,
    string DeviceName,
    int VolumePercent,
    bool IsMuted);

internal sealed class AudioOperationSourceCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(500);

    private readonly IOverlayPublisher publisher;
    private readonly MMDeviceEnumerator devices = new();
    private readonly Timer timer;
    private AudioOutputState? previous;
    private int polling;
    private bool disposed;

    internal AudioOperationSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        timer = new Timer(
            Poll,
            null,
            TimeSpan.Zero,
            PollInterval);
    }

    private void Poll(object? state)
    {
        if (disposed || Interlocked.Exchange(ref polling, 1) != 0)
        {
            return;
        }

        try
        {
            using var device = devices.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia);
            var current = new AudioOutputState(
                device.ID,
                device.FriendlyName,
                (int)Math.Round(
                    device.AudioEndpointVolume.MasterVolumeLevelScalar *
                    100),
                device.AudioEndpointVolume.Mute);
            if (previous is null)
            {
                previous = current;
                return;
            }

            if (!string.Equals(
                    previous.DeviceId,
                    current.DeviceId,
                    StringComparison.Ordinal))
            {
                Publish(
                    "audio-device",
                    "音频输出已切换",
                    current.DeviceName,
                    "输出设备");
            }
            else if (previous.IsMuted != current.IsMuted)
            {
                Publish(
                    "audio-mute",
                    current.IsMuted ? "已静音" : "已取消静音",
                    current.DeviceName,
                    "音频");
            }
            else if (previous.VolumePercent != current.VolumePercent)
            {
                Publish(
                    "audio-volume",
                    $"音量 {current.VolumePercent}%",
                    current.DeviceName,
                    "系统音量");
            }

            previous = current;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            RuntimeLog.Write(
                $"Audio operation probe failed: {exception.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private void Publish(
        string eventId,
        string title,
        string body,
        string eyebrow)
    {
        _ = publisher.Publish(OverlayRequest.Timed(
            eventId,
            OverlayKind.SystemOperation,
            OverlaySource.System,
            title,
            body,
            visual: new OverlayVisualData(
                Eyebrow: eyebrow,
                AccentHex: "#9CE7FF")));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Dispose();
        devices.Dispose();
    }
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
