namespace HS2.CrystalOverlay.Core;

public sealed class AudioDeviceStateTracker
{
    private string? previousId;

    public OverlayRequest? Observe(string deviceId, string displayName)
    {
        var previous = previousId;
        previousId = deviceId;
        if (previous is null || string.Equals(previous, deviceId, StringComparison.Ordinal))
        {
            return null;
        }

        return OverlayRequest.Timed(
            "audio-device", OverlayKind.DeviceOrNetwork, OverlaySource.System,
            "音频输出已切换", displayName,
            dedupKey: $"audio-device:{deviceId}",
            visual: new OverlayVisualData(Eyebrow: "设备状态", AccentHex: "#8EF2C8"));
    }
}
