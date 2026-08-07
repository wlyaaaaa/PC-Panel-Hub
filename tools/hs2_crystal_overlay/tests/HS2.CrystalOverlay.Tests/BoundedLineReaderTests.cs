using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class BoundedLineReaderTests
{
    [Fact]
    public async Task OversizedLineIsRejectedWithoutReadingUnboundedInput()
    {
        using var reader = new StringReader(new string('x', 65) + "\n");

        var line = await BoundedLineReader.ReadAsync(
            reader,
            maxCharacters: 64,
            timeout: TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Null(line);
    }

    [Fact]
    public async Task SilentClientTimesOutInsteadOfOwningServerForever()
    {
        using var reader = new BlockingReader();
        var stopwatch = Stopwatch.StartNew();

        var line = await BoundedLineReader.ReadAsync(
            reader,
            maxCharacters: 64,
            timeout: TimeSpan.FromMilliseconds(40),
            CancellationToken.None);

        Assert.Null(line);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SilentPipeClientTimesOutAndNextClientCanPublish()
    {
        var pipeName = $"HS2.CrystalOverlay.Tests.{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<ImportantTaskUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ImportantTaskPipeServer.RunAsync(
            pipeName,
            TimeSpan.FromMilliseconds(60),
            update =>
            {
                received.TrySetResult(update);
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        await using (var silent = new NamedPipeClientStream(
                         ".",
                         pipeName,
                         PipeDirection.Out,
                         PipeOptions.Asynchronous))
        {
            await silent.ConnectAsync(2_000, cancellation.Token);
            await Task.Delay(150, cancellation.Token);
            await using (var valid = new NamedPipeClientStream(
                             ".",
                             pipeName,
                             PipeDirection.Out,
                             PipeOptions.Asynchronous))
            {
                await valid.ConnectAsync(2_000, cancellation.Token);
                await using var writer = new StreamWriter(
                    valid,
                    new UTF8Encoding(false),
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };
                await writer.WriteLineAsync(
                    "{\"id\":\"copy-1\",\"state\":\"active\"," +
                    "\"title\":\"复制照片\",\"progress_percent\":42}");
            }

            var update = await received.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.Equal("copy-1", update.Id);
            Assert.Equal(0.42, update.Progress);
        }

        cancellation.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class BlockingReader : TextReader
    {
        public override async ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
