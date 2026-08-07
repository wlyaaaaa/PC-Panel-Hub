namespace HS2.CrystalOverlay.Core;

public static class ImportantTaskProjection
{
    public static IReadOnlyList<OverlayRequest> Project(
        ImportantTaskUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var activeId = ActiveEventId(update.Id);
        var completionId = CompletionEventId(update.Id);
        if (update.State == ImportantTaskState.Cancelled)
        {
            return
            [
                OverlayRequest.End(
                    activeId,
                    OverlayKind.ImportantTask,
                    OverlaySource.Task),
                OverlayRequest.End(
                    completionId,
                    OverlayKind.ImportantTaskComplete,
                    OverlaySource.Task),
            ];
        }

        if (update.State == ImportantTaskState.Completed)
        {
            return
            [
                OverlayRequest.End(
                    activeId,
                    OverlayKind.ImportantTask,
                    OverlaySource.Task),
                OverlayRequest.Timed(
                    completionId,
                    OverlayKind.ImportantTaskComplete,
                    OverlaySource.Task,
                    update.Title,
                    update.Detail ?? "任务已经完成",
                    dedupKey: $"task-complete:{update.Id}",
                    visual: new OverlayVisualData(
                        Eyebrow: "任务完成 / DONE",
                        Progress: 1,
                        AccentHex: "#8EF2C8")),
            ];
        }

        return
        [
            OverlayRequest.End(
                completionId,
                OverlayKind.ImportantTaskComplete,
                OverlaySource.Task),
            OverlayRequest.Active(
                activeId,
                OverlayKind.ImportantTask,
                OverlaySource.Task,
                update.Title,
                FormatDetail(update),
                dedupKey: $"task:{update.Id}",
                visual: new OverlayVisualData(
                    Eyebrow: "重要任务 / TASK",
                    Meta: FormatRemaining(update.Remaining),
                    Progress: update.Progress,
                    AccentHex: "#A9C7FF")),
        ];
    }

    public static string ActiveEventId(string id) =>
        $"important-task:{id}";

    public static string CompletionEventId(string id) =>
        $"important-task-complete:{id}";

    private static string? FormatDetail(ImportantTaskUpdate update)
    {
        var progress = update.Progress is null
            ? null
            : $"完成 {update.Progress.Value * 100:0}%";
        var value = string.Join(
            "  ·  ",
            new[] { update.Detail, progress }
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate)));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? FormatRemaining(TimeSpan? remaining)
    {
        if (remaining is null)
        {
            return null;
        }

        if (remaining <= TimeSpan.Zero)
        {
            return "即将完成";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return "预计剩余不足 1 分钟";
        }

        return remaining.Value.TotalHours >= 1
            ? $"预计剩余 {(int)remaining.Value.TotalHours} 小时 " +
              $"{remaining.Value.Minutes} 分钟"
            : $"预计剩余 {(int)Math.Ceiling(remaining.Value.TotalMinutes)} 分钟";
    }
}

public sealed class ImportantTaskLeaseRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, DateTimeOffset> expiries =
        new(StringComparer.Ordinal);
    private readonly TimeSpan defaultLease;

    public ImportantTaskLeaseRegistry(TimeSpan defaultLease)
    {
        if (defaultLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultLease));
        }

        this.defaultLease = defaultLease;
    }

    public void Refresh(
        string id,
        DateTimeOffset now,
        TimeSpan? requestedLease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var lease = requestedLease is { } requested &&
                    requested > TimeSpan.Zero
            ? requested
            : defaultLease;
        lock (sync)
        {
            expiries[id] = now + lease;
        }
    }

    public void Remove(string id)
    {
        lock (sync)
        {
            expiries.Remove(id);
        }
    }

    public IReadOnlyList<string> CollectExpired(DateTimeOffset now)
    {
        lock (sync)
        {
            return RemoveExpired(now);
        }
    }

    public void Expire(
        DateTimeOffset now,
        Action<string> onExpired)
    {
        ArgumentNullException.ThrowIfNull(onExpired);
        lock (sync)
        {
            foreach (var id in RemoveExpired(now))
            {
                onExpired(id);
            }
        }
    }

    private string[] RemoveExpired(DateTimeOffset now)
    {
        var expired = expiries
            .Where(pair => pair.Value <= now)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var id in expired)
        {
            expiries.Remove(id);
        }

        return expired;
    }
}
