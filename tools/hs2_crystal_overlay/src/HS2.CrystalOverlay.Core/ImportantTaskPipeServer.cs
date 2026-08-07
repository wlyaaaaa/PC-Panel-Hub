using System.IO.Pipes;
using System.Text;

namespace HS2.CrystalOverlay.Core;

public static class ImportantTaskPipeServer
{
    public static async Task RunAsync(
        string pipeName,
        TimeSpan readTimeout,
        Func<ImportantTaskUpdate, ValueTask> onUpdate,
        CancellationToken cancellationToken,
        Action<Exception>? onError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(onUpdate);
        if (readTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readTimeout));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(false, true),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 4096,
                    leaveOpen: true);
                var line = await BoundedLineReader.ReadAsync(
                    reader,
                    maxCharacters: 64 * 1024,
                    readTimeout,
                    cancellationToken);
                if (line is null)
                {
                    continue;
                }

                var update = ImportantTaskProtocol.Parse(line);
                if (update is not null)
                {
                    await onUpdate(update);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A client can disconnect midway through an update. A new
                // pipe instance is opened immediately for a clean retry.
            }
            catch (Exception exception)
            {
                onError?.Invoke(exception);
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
