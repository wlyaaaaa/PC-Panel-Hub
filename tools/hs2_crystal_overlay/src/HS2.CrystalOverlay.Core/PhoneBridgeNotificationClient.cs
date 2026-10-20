using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace HS2.CrystalOverlay.Core;

public sealed class PhoneBridgeNotificationClient(
    Action<OverlayRequest> publish,
    string pipeName = "pcconfig-phone-bridge",
    TimeSpan? retryDelay = null)
{
    private const string ConnectionEventId = "phone-bridge-connection";
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly PhoneNotificationSnapshotReconciler reconciler = new(Retention);
    private readonly Dictionary<string, PhoneNotificationSnapshotItem> recent =
        new(StringComparer.Ordinal);
    private uint nextId;
    private bool? connected;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // The bridge replays recent events; capture an empty baseline so they display.
        reconciler.Reconcile([], DateTimeOffset.UtcNow);
        SetConnected(false);
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(
                        ".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(1500, cancellationToken);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var helloReceived = false;
                    var pending = reader.ReadLineAsync(cancellationToken).AsTask();
                    while (true)
                    {
                        using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var delay = Task.Delay(TimeSpan.FromSeconds(1), tick.Token);
                        if (await Task.WhenAny(pending, delay) == delay)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Expire();
                            continue;
                        }
                        tick.Cancel();
                        var line = await pending;
                        if (line is null) break;
                        var message = Parse(line);
                        if (message?.Kind == "hello")
                        {
                            helloReceived = true;
                            failures = 0;
                            SetConnected(message.Status == "connected");
                        }
                        else if (helloReceived && message?.Kind == "status")
                        {
                            SetConnected(message.Status == "connected");
                        }
                        else if (helloReceived && message?.Kind == "notification")
                        {
                            Accept(message);
                        }
                        pending = reader.ReadLineAsync(cancellationToken).AsTask();
                    }
                }
                catch (Exception exception) when (exception is IOException or
                    TimeoutException or UnauthorizedAccessException)
                {
                    // No notification text or exception payload is logged or persisted.
                }
                SetConnected(false);
                Expire();
                var seconds = failures++ switch { 0 => 2, 1 => 10, _ => 30 };
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(seconds), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static BridgeMessage? Parse(string line)
    {
        try
        {
            var message = JsonSerializer.Deserialize<BridgeMessage>(line, JsonOptions);
            return message?.V == 1 ? message : null;
        }
        catch (JsonException) { return null; }
    }

    private void Accept(BridgeMessage message)
    {
        var now = DateTimeOffset.UtcNow;
        Expire();
        if (string.IsNullOrWhiteSpace(message.Id) || recent.ContainsKey(message.Id) ||
            message.PostedAt < now - Retention || message.PostedAt > now.AddMinutes(1)) return;
        recent.Add(message.Id, new PhoneNotificationSnapshotItem(
            ++nextId, message.PostedAt,
            string.IsNullOrWhiteSpace(message.App) ? message.Package ?? "手机" : message.App,
            message.Title ?? message.App ?? "手机通知", message.Text,
            OverlaySource.PhoneBridge, message.Code));
        Publish(reconciler.Reconcile(recent.Values, now));
    }

    private void Expire()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = recent.Where(pair => pair.Value.CreationTime < now - Retention)
            .Select(pair => pair.Key).ToArray();
        foreach (var id in stale) recent.Remove(id);
        if (stale.Length > 0) Publish(reconciler.Reconcile(recent.Values, now));
        Publish(reconciler.ExpireStale(now));
    }

    private void Publish(IEnumerable<OverlayRequest> requests)
    {
        foreach (var request in requests) publish(request);
    }

    private void SetConnected(bool value)
    {
        if (connected == value) return;
        connected = value;
        publish(value
            ? OverlayRequest.End(ConnectionEventId, OverlayKind.PhoneConnection, OverlaySource.PhoneBridge)
            : OverlayRequest.Active(ConnectionEventId, OverlayKind.PhoneConnection,
                OverlaySource.PhoneBridge, "手机桥未连接"));
    }

    private sealed record BridgeMessage(
        int V, string? Kind, string? Status, string? Id, string? Package,
        string? App, string? Title, string? Text, string? Code, DateTimeOffset PostedAt);
}
