using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneBridgeNotificationTests
{
    [Fact]
    public async Task StandardBatteryRefreshIgnoresTransportAndPreservesCharging()
    {
        var name = "hs2-battery-" + Guid.NewGuid().ToString("N");
        var events = new ConcurrentQueue<OverlayRequest>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var loop = new PhoneBridgeNotificationClient(events.Enqueue, name).RunAsync(stop.Token);
        async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(10, stop.Token); }
        try
        {
            await server.WaitForConnectionAsync(stop.Token);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            async Task Send(string kind, DateTimeOffset readAt, string transport) =>
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    v = 1, kind, status = "connected", transport,
                    battery = new { level = 42, charging = true, read_at = readAt.ToOffset(TimeSpan.FromHours(8)) },
                }));
            await Send("hello", DateTimeOffset.UtcNow.AddSeconds(-14), "unknown-source");
            await Until(() => events.Any(e => e.Kind == OverlayKind.PhoneBattery));
            var battery = Assert.Single(events, e => e.Kind == OverlayKind.PhoneBattery);
            Assert.Equal("42%", battery.Title);
            Assert.True(battery.Visual?.IsCharging);
            Assert.Equal(OverlaySource.PhoneBridge, battery.Source);
            await Send("status", DateTimeOffset.UtcNow, "future-source");
            // The first sample would expire here; a same-value sample must refresh its evidence time.
            await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
            Assert.Single(events, e => e.Kind == OverlayKind.PhoneBattery);
            Assert.True(events.Last(e => e.Kind == OverlayKind.PhoneBattery).IsActive);
            await writer.WriteLineAsync("{\"v\":1,\"kind\":\"status\",\"status\":\"connected\",\"battery\":null}");
            await Until(() => events.Any(e => e.Kind == OverlayKind.PhoneBattery && !e.IsActive));
        }
        finally { stop.Cancel(); await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task InvalidMissingAndDisconnectedBatteryAlwaysClearTheCard()
    {
        var name = "hs2-battery-clear-" + Guid.NewGuid().ToString("N");
        var events = new ConcurrentQueue<OverlayRequest>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var loop = new PhoneBridgeNotificationClient(events.Enqueue, name).RunAsync(stop.Token);
        async Task Until(int count) { while (events.Count(e => e.Kind == OverlayKind.PhoneBattery) < count) await Task.Delay(10, stop.Token); }
        try
        {
            await server.WaitForConnectionAsync(stop.Token);
            using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync("{\"v\":1,\"kind\":\"hello\",\"status\":\"connected\"}");
            var count = 0;
            foreach (var invalid in new[]
            {
                "null", "{}",
                JsonSerializer.Serialize(new { level = 101, charging = false, read_at = DateTimeOffset.UtcNow }),
                JsonSerializer.Serialize(new { level = 42, charging = "bad", read_at = DateTimeOffset.UtcNow }),
                "{\"level\":\"bad\",\"charging\":false,\"read_at\":\"bad\"}",
                "{\"level\":42,\"charging\":false,\"read_at\":\"bad\"}",
                "{\"level\":42,\"charging\":false,\"read_at\":\"2026-01-01T00:00:00+08:00\"}",
            })
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    v = 1, kind = "status", status = "connected",
                    battery = new { level = 42, charging = false, read_at = DateTimeOffset.UtcNow },
                }));
                await Until(++count);
                Assert.True(events.Last(e => e.Kind == OverlayKind.PhoneBattery).IsActive);
                await writer.WriteLineAsync("{\"v\":1,\"kind\":\"status\",\"status\":\"connected\",\"battery\":" + invalid + "}");
                await Until(++count);
                Assert.False(events.Last(e => e.Kind == OverlayKind.PhoneBattery).IsActive);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                v = 1, kind = "status", status = "connected",
                battery = new { level = 42, charging = false, read_at = DateTimeOffset.UtcNow },
            }));
            await Until(++count);
            await writer.WriteLineAsync("{\"v\":1,\"kind\":\"status\",\"status\":\"disconnected\",\"battery\":null}");
            await Until(++count);
            Assert.False(events.Last(e => e.Kind == OverlayKind.PhoneBattery).IsActive);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                v = 1, kind = "status", status = "connected",
                battery = new { level = 42, charging = false, read_at = DateTimeOffset.UtcNow },
            }));
            await Until(++count);
            writer.Dispose();
            await Until(++count);
            Assert.False(events.Last(e => e.Kind == OverlayKind.PhoneBattery).IsActive);
        }
        finally { stop.Cancel(); await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task BatteryExpiresFromReadTimeWhilePipeStaysConnected()
    {
        var name = "hs2-battery-expiry-" + Guid.NewGuid().ToString("N");
        var events = new ConcurrentQueue<OverlayRequest>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var loop = new PhoneBridgeNotificationClient(events.Enqueue, name).RunAsync(stop.Token);
        try
        {
            await server.WaitForConnectionAsync(stop.Token);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                v = 1, kind = "hello", status = "connected",
                battery = new { level = 99, charging = false, read_at = DateTimeOffset.UtcNow.AddSeconds(-14) },
            }));
            while (!events.Any(e => e.Kind == OverlayKind.PhoneBattery && !e.IsActive))
                await Task.Delay(10, stop.Token);
            Assert.Equal(2, events.Count(e => e.Kind == OverlayKind.PhoneBattery));
            Assert.True(server.IsConnected);
        }
        finally { stop.Cancel(); await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task StatusAfterConnectingHelloRemovesThenRestoresDisconnectedCard()
    {
        var name = "hs2-status-" + Guid.NewGuid().ToString("N");
        var events = new ConcurrentQueue<OverlayRequest>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var loop = new PhoneBridgeNotificationClient(events.Enqueue, name).RunAsync(stop.Token);
        async Task Until(int count) { while (events.Count < count) await Task.Delay(10, stop.Token); }
        try
        {
            await server.WaitForConnectionAsync(stop.Token);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("{\"v\":1,\"kind\":\"hello\",\"status\":\"connecting\"}");
            await Until(1);
            Assert.True(events.Last().IsActive);
            Assert.Equal("手机桥未连接", events.Last().Title);
            await writer.WriteLineAsync("{\"v\":1,\"kind\":\"status\",\"status\":\"connected\",\"battery\":null}");
            await Until(2);
            Assert.False(events.Last().IsActive);
            await writer.WriteLineAsync("{\"v\":1,\"kind\":\"status\",\"status\":\"disconnected\",\"battery\":null}");
            await Until(3);
            Assert.True(events.Last().IsActive);
            Assert.Equal("手机桥未连接", events.Last().Title);
        }
        finally { stop.Cancel(); await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task PipeReplayLiveDedupCodesAndReconnectUseExistingCards()
    {
        var name = "hs2-test-" + Guid.NewGuid().ToString("N");
        var events = new ConcurrentQueue<OverlayRequest>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new PhoneBridgeNotificationClient(events.Enqueue, name, TimeSpan.FromMilliseconds(20));
        var loop = client.RunAsync(stop.Token);
        OverlayRequest[] Notifications() => events.Where(e => e.IsActive && e.Kind != OverlayKind.PhoneConnection).ToArray();
        async Task Until(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(10, stop.Token);
        }
        NamedPipeServerStream Server() => new(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        async Task Send(StreamWriter writer, string id, string text, string? code = null,
            int version = 1, DateTimeOffset? posted = null) => await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                v = version, kind = "notification", id, package = "com.android.mms",
                app = "信息", title = "合成通知", text, code, posted_at = posted ?? DateTimeOffset.Now,
            }));
        try
        {
            await Until(() => events.Any(e => e.Title == "手机桥未连接"));
            var scheduler = new OverlayScheduler();
            var now = DateTimeOffset.UtcNow;
            scheduler.Publish(events.First(), now);
            Assert.Single(scheduler.GetFrame(now.AddMinutes(1)).NotificationCards);
            using (var server = Server())
            {
                await server.WaitForConnectionAsync(stop.Token);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("{\"v\":1,\"kind\":\"hello\",\"status\":\"connected\",\"battery\":null}");
                await writer.WriteLineAsync("{broken");
                await Send(writer, "bad-version", "不应出现", version: 2);
                await Send(writer, "stale", "过期重放", posted: DateTimeOffset.Now.AddMinutes(-6));
                await Send(writer, "replay", "重放内容", posted: DateTimeOffset.Now.AddMinutes(-1));
                await Send(writer, "replay", "同一 ID 不重复");
                await Until(() => Notifications().Length == 1);
                Assert.Equal("重放内容", Notifications()[0].Body);
                Assert.Equal("信息", Notifications()[0].Visual?.Subtitle);
                Assert.Equal(OverlaySource.PhoneBridge, Notifications()[0].Source);
                await Send(writer, "explicit", "验证码 123456", "654321");
                await Until(() => Notifications().Length == 2);
                Assert.Equal("654321", Notifications()[1].Visual?.VerificationCode);
                Assert.Equal("654321", Notifications()[1].Title);
                Assert.Null(Notifications()[1].Body);
                Assert.Null(Notifications()[1].Visual?.Eyebrow);
                Assert.Null(Notifications()[1].Visual?.Subtitle);
                Assert.Null(Notifications()[1].Visual?.Meta);
                await Send(writer, "fallback", "验证码 443322");
                await Until(() => Notifications().Length == 3);
                Assert.Equal("443322", Notifications()[2].Visual?.VerificationCode);
                await Send(writer, "live", "实时内容");
                await Until(() => Notifications().Length == 4);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    v = 1, kind = "notification", id = "synthetic-low-battery",
                    package = "phone.bridge.battery", app = "手机互联",
                    title = "手机电量低", text = "手机电量剩余 15%，请充电。",
                    posted_at = DateTimeOffset.Now,
                }));
                await Until(() => Notifications().Length == 5);
                var lowBattery = Notifications()[4];
                Assert.Equal(OverlayKind.PhoneNotification, lowBattery.Kind);
                Assert.Equal("手机电量剩余 15%，请充电。", lowBattery.Body);
                Assert.Equal("手机互联", lowBattery.Visual?.Subtitle);
                scheduler.Publish(events.First(e => e.Kind == OverlayKind.PhoneConnection && !e.IsActive), now);
                scheduler.Publish(lowBattery, now);
                Assert.Equal(lowBattery, Assert.Single(scheduler.GetFrame(now).NotificationCards).Request);
            }
            await Until(() => events.Count(e => e.Kind == OverlayKind.PhoneConnection && e.IsActive) == 2);
            using (var server = Server())
            {
                await server.WaitForConnectionAsync(stop.Token);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("{\"v\":1,\"kind\":\"hello\",\"status\":\"disconnected\"}");
                await Send(writer, "replay", "断开后重放不重复");
                await writer.WriteLineAsync("{\"v\":1,\"kind\":\"hello\",\"status\":\"connected\"}");
                await Send(writer, "reconnected-live", "重新连接后实时内容");
                await Until(() => Notifications().Length == 6);
                Assert.Equal("重新连接后实时内容", Notifications()[5].Body);
                Assert.Equal(2, events.Count(e => e.Kind == OverlayKind.PhoneConnection && !e.IsActive));
            }
        }
        finally
        {
            stop.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}
