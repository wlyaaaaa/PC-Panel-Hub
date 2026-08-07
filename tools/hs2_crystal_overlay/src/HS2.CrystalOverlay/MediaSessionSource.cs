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
    private static readonly TimeSpan ArtworkDownloadTimeout =
        TimeSpan.FromSeconds(8);
    private static readonly HttpClient ArtworkClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly IOverlayPublisher publisher;
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly NeteaseLocalMediaProbe local = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly PeriodicTimer timer;
    private readonly Task loop;
    private readonly object sync = new();
    private readonly ArtworkGenerationGate artworkGeneration = new();
    private readonly List<Task> artworkTasks = [];
    private CancellationTokenSource? artworkCancellation;
    private ArtworkGeneration currentArtworkGeneration;
    private string? lastTrackInstance;
    private string? currentArtworkPath;
    private string? lastDiagnosticState;
    private bool activePublished;
    private int inactiveSamples;

    internal MediaSessionSource(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        timer = new PeriodicTimer(PollInterval);
        loop = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        try
        {
            PollOnce();
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                PollOnce();
            }
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void PollOnce()
    {
        if (publicationGate.IsClosed)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var observation = local.Read(now);
            lock (sync)
            {
                if (publicationGate.IsClosed)
                {
                    return;
                }

                if (observation is null)
                {
                    LogState(local.LastReadState);
                    HideAfterInactiveSamplesLocked(resetTrack: true);
                    return;
                }

                if (!observation.IsPlaying)
                {
                    LogState(
                        observation.AudioStateIsAuthoritative
                            ? "audio-inactive"
                            : "audio-state-unavailable");
                    HideAfterInactiveSamplesLocked(resetTrack: false);
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
                    lastTrackInstance = observation.TrackInstance;
                    currentArtworkPath = null;
                }

                PublishPlayingLocked(observation, trackChanged);
                if (trackChanged ||
                    (currentArtworkPath is null &&
                     !artworkGeneration.IsCurrent(
                         currentArtworkGeneration)))
                {
                    StartArtworkRefreshLocked(observation);
                }
            }
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"NetEase local probe failed: {exception.GetType().Name}");
            lock (sync)
            {
                if (!publicationGate.IsClosed)
                {
                    HideAfterInactiveSamplesLocked(resetTrack: false);
                }
            }
        }
    }

    private void StartArtworkRefreshLocked(
        NeteaseLocalMediaObservation observation)
    {
        if (publicationGate.IsClosed)
        {
            return;
        }

        CancelArtworkLocked(invalidateGeneration: false);
        var generation = artworkGeneration.Begin();
        currentArtworkGeneration = generation;
        var refreshCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellation.Token);
        artworkCancellation = refreshCancellation;
        var refresh = RefreshArtworkAsync(
            observation,
            generation,
            refreshCancellation.Token);
        TrackArtworkTaskLocked(refresh, refreshCancellation);
    }

    private async Task RefreshArtworkAsync(
        NeteaseLocalMediaObservation observation,
        ArtworkGeneration generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = await CacheArtworkAsync(
                observation.Track,
                cancellationToken);
            if (path is null)
            {
                return;
            }

            lock (sync)
            {
                if (publicationGate.IsClosed ||
                    !artworkGeneration.IsCurrent(generation) ||
                    !activePublished ||
                    !string.Equals(
                        observation.TrackInstance,
                        lastTrackInstance,
                        StringComparison.Ordinal))
                {
                    return;
                }

                currentArtworkPath = path;
                PublishPlayingLocked(observation, trackChanged: false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested ||
                  cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"NetEase artwork refresh failed: " +
                $"{exception.GetType().Name}");
        }
    }

    private void TrackArtworkTaskLocked(
        Task refresh,
        CancellationTokenSource refreshCancellation)
    {
        artworkTasks.Add(refresh);
        _ = refresh.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (sync)
                {
                    artworkTasks.Remove(completed);
                    if (ReferenceEquals(
                            artworkCancellation,
                            refreshCancellation))
                    {
                        artworkCancellation = null;
                    }
                }

                refreshCancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishPlayingLocked(
        NeteaseLocalMediaObservation observation,
        bool trackChanged)
    {
        var track = observation.Track;
        var visual = new OverlayVisualData(
            Subtitle: track.Artist,
            ArtworkPath: currentArtworkPath,
            AccentHex: "#89F7FF",
            TranslatedTitle: track.TranslatedTitle);
        activePublished |= PublishIfActive(OverlayRequest.Active(
            "media-active",
            OverlayKind.MediaActive,
            OverlaySource.NetEase,
            track.Title,
            body: null,
            visual: visual));

        if (!trackChanged)
        {
            return;
        }

        _ = PublishIfActive(OverlayRequest.Timed(
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

    private void HideAfterInactiveSamplesLocked(bool resetTrack)
    {
        inactiveSamples++;
        if (inactiveSamples < InactiveSamplesBeforeHide)
        {
            return;
        }

        EndMediaLocked(resetTrack);
    }

    private void EndMediaLocked(bool resetTrack)
    {
        CancelArtworkLocked(invalidateGeneration: true);
        if (activePublished)
        {
            _ = PublishIfActive(OverlayRequest.End(
                "media-active",
                OverlayKind.MediaActive,
                OverlaySource.NetEase));
            _ = PublishIfActive(OverlayRequest.End(
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

    private void CancelArtworkLocked(bool invalidateGeneration)
    {
        if (invalidateGeneration)
        {
            artworkGeneration.Invalidate();
            currentArtworkGeneration = default;
        }

        artworkCancellation?.Cancel();
        artworkCancellation = null;
    }

    private static async Task<string?> CacheArtworkAsync(
        NeteaseTrackMetadata track,
        CancellationToken cancellationToken)
    {
        if (track.ArtworkUri is null)
        {
            return null;
        }

        string? temporary = null;
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

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeout.CancelAfter(ArtworkDownloadTimeout);
            var token = timeout.Token;
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                track.ArtworkUri);
            using var response = await ArtworkClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength >
                MaximumArtworkBytes)
            {
                return null;
            }

            temporary = path + "." + Guid.NewGuid().ToString("N") +
                        ".tmp";
            await using var input =
                await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
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
                        buffer.AsMemory(),
                        token);
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
                        token);
                }
            }

            File.Move(temporary, path, true);
            temporary = null;
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
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
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
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

    private bool PublishIfActive(OverlayRequest request) =>
        publicationGate.TryPublish(() => publisher.Publish(request));

    public void Dispose()
    {
        if (!publicationGate.Close())
        {
            return;
        }

        cancellation.Cancel();
        timer.Dispose();
        Task[] pendingArtwork;
        lock (sync)
        {
            CancelArtworkLocked(invalidateGeneration: true);
            pendingArtwork = artworkTasks.ToArray();
        }

        var pending = new List<Task>(pendingArtwork.Length + 1)
        {
            loop,
        };
        pending.AddRange(pendingArtwork);
        var completion = Task.WhenAll(pending);
        var completed = false;
        try
        {
            completed = completion.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            completed = completion.IsCompleted;
        }

        if (completed)
        {
            _ = completion.Exception;
            cancellation.Dispose();
            return;
        }

        _ = completion.ContinueWith(
            completedTasks =>
            {
                _ = completedTasks.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
