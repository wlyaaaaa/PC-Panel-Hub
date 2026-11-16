using HS2.CrystalOverlay.Core;
using System.Text.Json;

namespace HS2.CrystalOverlay.Tests;
public sealed class OverlayLiveSnapshotTests
{
    [Fact]
    public void SnapshotCountsVisibleItemsWithoutNotificationContent()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "snapshot.json");
        var at = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active("clock", OverlayKind.Glance, OverlaySource.System, "clock"), at);
        scheduler.Publish(OverlayRequest.Active("music", OverlayKind.MediaActive, OverlaySource.NetEase, "OWNER_SONG"), at);
        scheduler.Publish(OverlayRequest.Timed("phone", OverlayKind.PhoneNotification, OverlaySource.PhoneBridge, "SECRET_TITLE", "SECRET_BODY"), at);
        var frame = scheduler.GetFrame(at);
        try
        {
            OverlayLiveSnapshot.Write(path, frame.VisibleCards.Concat(frame.DirectItems), at);
            var raw = File.ReadAllText(path);using var json = JsonDocument.Parse(raw);
            Assert.Equal(3, json.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(at, json.RootElement.GetProperty("at").GetDateTimeOffset());
            Assert.Contains("OWNER_SONG", raw);Assert.DoesNotContain("SECRET_", raw);
            Assert.Equal(3, json.RootElement.GetProperty("cards").GetArrayLength());
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }
}
