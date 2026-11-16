using System.Text.Json;

namespace HS2.CrystalOverlay.Core;

public static class OverlayLiveSnapshot
{
    public static void Write(string path, IEnumerable<OverlayItem> visible, DateTimeOffset at)
    {
        var items = visible.ToArray();
        var cards = items.Select(item => new { kind = item.Request.Kind.ToString(), text = item.Request.Kind switch
        {
            OverlayKind.Glance => "时钟", OverlayKind.PhoneBattery => "手机电量",
            OverlayKind.MediaActive or OverlayKind.MediaTrackChange => "音乐",
            OverlayKind.GameActive or OverlayKind.GameSummary => "游戏",
            OverlayKind.DeviceOrNetwork => "系统事件", _ => "手机通知"
        }, state_text = "显示中" }).ToArray();
        var music = items.Where(item => item.Request.Kind is OverlayKind.MediaActive or OverlayKind.MediaTrackChange)
            .Select(item => new { text = item.Request.Title, state_text = "显示中" }).ToArray();
        var json = JsonSerializer.Serialize(new { v = 1, at, count = cards.Length, cards, music,
            scope = "最近一次屏幕浮层刷新" });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
