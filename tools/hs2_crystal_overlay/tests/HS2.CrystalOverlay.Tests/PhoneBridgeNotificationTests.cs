using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneBridgeNotificationTests
{
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
            Assert.Equal(TimeSpan.FromSeconds(5), OverlayPolicies.For(OverlayKind.PhoneConnection, OverlaySource.AndroidAdb).Duration);
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
                await Send(writer, "fallback", "验证码 443322");
                await Until(() => Notifications().Length == 3);
                Assert.Equal("443322", Notifications()[2].Visual?.VerificationCode);
                await Send(writer, "live", "实时内容");
                await Until(() => Notifications().Length == 4);
            }
            await Until(() => events.Count(e => e.Kind == OverlayKind.PhoneConnection && e.IsActive) == 2);
            using (var server = Server())
            {
                await server.WaitForConnectionAsync(stop.Token);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("{\"v\":1,\"kind\":\"hello\",\"status\":\"different_network\"}");
                await Send(writer, "replay", "断开后重放不重复");
                await writer.WriteLineAsync("{\"v\":1,\"kind\":\"hello\",\"status\":\"connected\"}");
                await Send(writer, "reconnected-live", "重新连接后实时内容");
                await Until(() => Notifications().Length == 5);
                Assert.Equal("重新连接后实时内容", Notifications()[4].Body);
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
