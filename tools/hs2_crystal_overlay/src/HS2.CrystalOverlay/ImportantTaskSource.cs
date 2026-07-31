using System.IO.Pipes;
using System.Text;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class ImportantTaskSourceCoordinator : IDisposable
{
    internal const string PipeName = "HS2.CrystalOverlay.Tasks";

    private readonly IOverlayPublisher publisher;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task server;
    private bool disposed;

    internal ImportantTaskSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        server = Task.Run(ServerLoopAsync);
    }

    private async Task ServerLoopAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(false, true),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096,
                    leaveOpen: true);
                var line = await reader.ReadLineAsync(
                    cancellation.Token);
                if (line is null || line.Length > 64 * 1024)
                {
                    continue;
                }

                var update = ImportantTaskProtocol.Parse(line);
                if (update is not null)
                {
                    Publish(update);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client can disconnect midway through an update. The
                // next pipe instance remains available for a clean retry.
            }
            catch (Exception exception)
            {
                RuntimeLog.Write(
                    $"Important task pipe failed: {exception.GetType().Name}");
                await Task.Delay(
                    TimeSpan.FromSeconds(1),
                    cancellation.Token);
            }
        }
    }

    private void Publish(ImportantTaskUpdate update)
    {
        var eventId = $"important-task:{update.Id}";
        if (update.State == ImportantTaskState.Cancelled)
        {
            _ = publisher.Publish(OverlayRequest.End(
                eventId,
                OverlayKind.ImportantTask,
                OverlaySource.Task));
            return;
        }

        if (update.State == ImportantTaskState.Completed)
        {
            _ = publisher.Publish(OverlayRequest.End(
                eventId,
                OverlayKind.ImportantTask,
                OverlaySource.Task));
            _ = publisher.Publish(OverlayRequest.Timed(
                $"important-task-complete:{update.Id}",
                OverlayKind.ImportantTaskComplete,
                OverlaySource.Task,
                update.Title,
                update.Detail ?? "任务已经完成",
                dedupKey: $"task-complete:{update.Id}",
                visual: new OverlayVisualData(
                    Eyebrow: "任务完成 / DONE",
                    Progress: 1,
                    AccentHex: "#8EF2C8")));
            return;
        }

        _ = publisher.Publish(OverlayRequest.Active(
            eventId,
            OverlayKind.ImportantTask,
            OverlaySource.Task,
            update.Title,
            FormatDetail(update),
            dedupKey: $"task:{update.Id}",
            visual: new OverlayVisualData(
                Eyebrow: "重要任务 / TASK",
                Meta: FormatRemaining(update.Remaining),
                Progress: update.Progress,
                AccentHex: "#A9C7FF")));
    }

    private static string? FormatDetail(ImportantTaskUpdate update)
    {
        var progress = update.Progress is null
            ? null
            : $"完成 {update.Progress.Value * 100:0}%";
        return string.Join(
            "  ·  ",
            new[] { update.Detail, progress }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? FormatRemaining(TimeSpan? remaining)
    {
        if (remaining is null)
        {
            return null;
        }

        return remaining.Value.TotalHours >= 1
            ? $"预计剩余 {(int)remaining.Value.TotalHours} 小时 {remaining.Value.Minutes} 分钟"
            : $"预计剩余 {Math.Max(1, (int)Math.Ceiling(remaining.Value.TotalMinutes))} 分钟";
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        try
        {
            server.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        cancellation.Dispose();
    }
}
