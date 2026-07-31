using System.Security.Cryptography;
using System.Text;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class MediaSessionSource : IDisposable
{
    private const int InactiveSamplesBeforeHide = 4;
    private const int MaximumArtworkBytes = 5 * 1024 * 1024;

    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly HttpClient ArtworkClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private readonly IOverlayPublisher publisher;
    private readonly NeteaseLocalMediaProbe local = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Timer timer;
    private string? lastTrackInstance;
    private string? currentArtworkPath;
    private string? lastDiagnosticState;
    private bool activePublished;
    private int inactiveSamples;
    private int polling;
    private bool disposed;

    internal MediaSessionSource(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        timer = new Timer(
            Poll,
            null,
            TimeSpan.Zero,
            PollInterval);
    }

    private async void Poll(object? state)
    {
        if (disposed || Interlocked.Exchange(ref polling, 1) != 0)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var observation = local.Read(now);
            if (observation is null)
            {
                LogState(local.LastReadState);
                HideAfterInactiveSamples(resetTrack: true);
                return;
            }

            if (!observation.IsPlaying)
            {
                LogState(
                    observation.AudioStateIsAuthoritative
                        ? "audio-inactive"
                        : "audio-state-unavailable");
                HideAfterInactiveSamples(resetTrack: false);
                return;
            }

            inactiveSamples = 0;
            LogState("playing-audio");
            var trackChanged = !string.Equals(
                observation.TrackInstance,
                lastTrackInstance,
                StringComparison.Ordinal);
            if (trackChanged)
            {
                currentArtworkPath = await CacheArtworkAsync(
                    observation.Track,
                    cancellation.Token);
            }

            PublishPlaying(
                observation,
                trackChanged);
            lastTrackInstance = observation.TrackInstance;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"NetEase local probe failed: {exception.GetType().Name}");
            HideAfterInactiveSamples(resetTrack: false);
        }
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private void PublishPlaying(
        NeteaseLocalMediaObservation observation,
        bool trackChanged)
    {
        var track = observation.Track;
        var visual = new OverlayVisualData(
            Subtitle: track.Artist,
            ArtworkPath: currentArtworkPath,
            AccentHex: "#89F7FF",
            TranslatedTitle: track.TranslatedTitle);
        _ = publisher.Publish(OverlayRequest.Active(
            "media-active",
            OverlayKind.MediaActive,
            OverlaySource.NetEase,
            track.Title,
            body: null,
            visual: visual));
        activePublished = true;

        if (!trackChanged)
        {
            return;
        }

        _ = publisher.Publish(OverlayRequest.Timed(
            "media-track-change",
            OverlayKind.MediaTrackChange,
            OverlaySource.NetEase,
            track.Title,
            body: null,
            dedupKey: $"netease:{observation.TrackInstance}",
            visual: visual with
            {
                Eyebrow = "开始播放 / NOW PLAYING",
            }));
    }

    private void HideAfterInactiveSamples(bool resetTrack)
    {
        inactiveSamples++;
        if (inactiveSamples < InactiveSamplesBeforeHide)
        {
            return;
        }

        EndMedia(resetTrack);
    }

    private void EndMedia(bool resetTrack)
    {
        if (activePublished)
        {
            _ = publisher.Publish(OverlayRequest.End(
                "media-active",
                OverlayKind.MediaActive,
                OverlaySource.NetEase));
            _ = publisher.Publish(OverlayRequest.End(
                "media-track-change",
                OverlayKind.MediaTrackChange,
                OverlaySource.NetEase));
            activePublished = false;
        }

        if (!resetTrack)
        {
            return;
        }

        lastTrackInstance = null;
        currentArtworkPath = null;
    }

    private static async Task<string?> CacheArtworkAsync(
        NeteaseTrackMetadata track,
        CancellationToken cancellationToken)
    {
        if (track.ArtworkUri is null)
        {
            return null;
        }

        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "HS2.CrystalOverlay",
                "Cache");
            Directory.CreateDirectory(folder);
            var hash = Convert.ToHexStringLower(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        track.ArtworkUri.AbsoluteUri)))[..20];
            var path = Path.Combine(folder, $"artwork-{hash}.img");
            if (new FileInfo(path) is { Exists: true, Length: > 256 })
            {
                return path;
            }

            using var response = await ArtworkClient.GetAsync(
                track.ArtworkUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength >
                MaximumArtworkBytes)
            {
                return null;
            }

            var temporary = path + ".tmp";
            await using var input =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                var total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(
                        buffer,
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaximumArtworkBytes)
                    {
                        return null;
                    }

                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);
                }
            }

            File.Move(temporary, path, true);
            return path;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LogState(string state)
    {
        if (string.Equals(
                state,
                lastDiagnosticState,
                StringComparison.Ordinal))
        {
            return;
        }

        lastDiagnosticState = state;
        RuntimeLog.Write($"NetEase source state: {state}");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        timer.Dispose();
        cancellation.Dispose();
    }
}
