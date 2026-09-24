using System.IO.Pipes;
using System.Text;
using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class ImportantTaskPipeServerTests
{
    [Fact]
    public async Task CurrentUserCanPublishAnImportantTask()
    {
        var pipeName = "hs2-important-task-test-" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<ImportantTaskUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ImportantTaskPipeServer.RunAsync(
            pipeName,
            TimeSpan.FromSeconds(3),
            update =>
            {
                received.TrySetResult(update);
                return ValueTask.CompletedTask;
            },
            cancellation.Token,
            exception => received.TrySetException(exception));

        try
        {
            await using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(cancellation.Token);
            await using var writer = new StreamWriter(
                client, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync("""{"id":"task-one","title":"Task","state":"active"}""");

            var update = await received.Task.WaitAsync(cancellation.Token);
            Assert.Equal("task-one", update.Id);
        }
        finally
        {
            await cancellation.CancelAsync();
            await server;
        }
    }
}
