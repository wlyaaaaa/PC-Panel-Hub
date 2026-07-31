using System.IO.Pipes;
using System.Text;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class NeteasePlaybackBridgeReader : IDisposable
{
    private const string PipeName =
        "HS2.NeteasePlaybackBridge";
    private static readonly TimeSpan MaximumSampleAge =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExchangeTimeout =
        TimeSpan.FromMilliseconds(200);

    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private bool disposed;

    internal NeteasePlaybackMemorySample? Read(
        IReadOnlySet<int> expectedProcessIds,
        DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            EnsureConnected();
            using var timeout = new CancellationTokenSource(
                ExchangeTimeout);
            writer!
                .WriteLineAsync(
                    "read".AsMemory(),
                    timeout.Token)
                .GetAwaiter()
                .GetResult();
            writer
                .FlushAsync(timeout.Token)
                .GetAwaiter()
                .GetResult();
            var response = reader!
                .ReadLineAsync(timeout.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return NeteasePlaybackBridgeProtocol.Parse(
                response,
                expectedProcessIds,
                now,
                MaximumSampleAge);
        }
        catch (IOException)
        {
            Reset();
            return null;
        }
        catch (TimeoutException)
        {
            Reset();
            return null;
        }
        catch (OperationCanceledException)
        {
            Reset();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            Reset();
            return null;
        }
        catch (InvalidOperationException)
        {
            Reset();
            return null;
        }
    }

    private void EnsureConnected()
    {
        if (pipe?.IsConnected == true)
        {
            return;
        }

        Reset();
        pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        pipe.Connect(timeout: 80);
        reader = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            bufferSize: 4096,
            leaveOpen: true);
    }

    private void Reset()
    {
        var previousWriter = writer;
        var previousReader = reader;
        var previousPipe = pipe;
        writer = null;
        reader = null;
        pipe = null;
        TryDispose(previousWriter);
        TryDispose(previousReader);
        TryDispose(previousPipe);
    }

    private static void TryDispose(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Reset();
    }
}
