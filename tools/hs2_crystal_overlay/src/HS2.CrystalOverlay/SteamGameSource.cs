using System.Text;
using System.Text.RegularExpressions;
using HS2.CrystalOverlay.Core;
using Microsoft.Win32;

namespace HS2_CrystalOverlay;

internal sealed partial class SteamGameSourceCoordinator : IDisposable
{
    private const int TailBytes = 16 * 1024 * 1024;
    private const int MaximumArtworkBytes = 5 * 1024 * 1024;
    private const uint WallpaperEngineAppId = 431960;
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CatalogRefreshInterval =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CatalogRetryInterval =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumFutureStartSkew =
        TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ArtworkDownloadTimeout =
        TimeSpan.FromSeconds(8);

    private readonly IOverlayPublisher publisher;
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly TimeZoneInfo steamLogTimeZone = TimeZoneInfo.Local;
    private readonly string? steamPath;
    private readonly string? processLogPath;
    private readonly HttpClient artworkClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly CancellationTokenSource cancellation = new();
    private readonly PeriodicTimer timer;
    private readonly Task loop;
    private readonly object sync = new();
    private readonly ArtworkGenerationGate artworkGeneration = new();
    private readonly List<Task> artworkTasks = [];
    private readonly SteamCatalogRefreshState catalogState = new(
        CatalogRefreshInterval,
        CatalogRetryInterval);
    private CancellationTokenSource? artworkCancellation;
    private uint? activeAppId;
    private string? activeName;
    private string? activeSessionKey;
    private string? activeArtwork;
    private DateTimeOffset activeStartedAt;
    private string? lastCatalogDiagnostic;
    private string? lastTimingDiagnostic;

    internal SteamGameSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        steamPath = ReadSteamPath();
        processLogPath = string.IsNullOrWhiteSpace(steamPath)
            ? null
            : Path.Combine(steamPath, "logs", "gameprocess_log.txt");
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
            var now = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(processLogPath) ||
                !File.Exists(processLogPath))
            {
                lock (sync)
                {
                    if (!publicationGate.IsClosed)
                    {
                        EndActiveLocked(now);
                    }
                }

                return;
            }

            if (catalogState.ShouldRefresh(now))
            {
                var readResult = ReadCatalog(steamPath!);
                if (!catalogState.Apply(readResult, now))
                {
                    LogCatalogState(
                        $"read-failed:{readResult.FailureReason ?? "unknown"}");
                }
                else
                {
                    // An empty result is meaningful only after a complete scan.
                    LogCatalogState(
                        readResult.Catalog!.Count == 0
                            ? "confirmed-empty"
                            : $"confirmed:{readResult.Catalog.Count}");
                }
            }

            if (!catalogState.HasConfirmedCatalog)
            {
                // A failed first scan is not proof that a game has stopped.
                return;
            }

            var catalog = catalogState.Catalog;
            var text = ReadTail(processLogPath, TailBytes);
            var running = SteamGameProcessLogParser.Parse(
                text,
                steamLogTimeZone);
            var current = running.FirstOrDefault(game =>
                game.AppId != WallpaperEngineAppId &&
                catalog.TryGetValue(game.AppId, out var metadata) &&
                IsGame(metadata));
            if (current is null)
            {
                lastTimingDiagnostic = null;
                lock (sync)
                {
                    if (!publicationGate.IsClosed)
                    {
                        EndActiveLocked(now);
                    }
                }

                return;
            }

            if (current.StartedAt > now + MaximumFutureStartSkew)
            {
                var diagnostic =
                    $"{current.AppId}:{current.StartedAt.ToUnixTimeSeconds()}";
                if (!string.Equals(
                        diagnostic,
                        lastTimingDiagnostic,
                        StringComparison.Ordinal))
                {
                    RuntimeLog.Write(
                        $"Steam game start rejected as future: " +
                        $"appId={current.AppId}; " +
                        $"sourceZone={steamLogTimeZone.Id}; " +
                        $"startedAtUtc={current.StartedAt.UtcDateTime:O}.");
                    lastTimingDiagnostic = diagnostic;
                }

                lock (sync)
                {
                    if (!publicationGate.IsClosed)
                    {
                        EndActiveLocked(now, publishSummary: false);
                    }
                }

                return;
            }

            lastTimingDiagnostic = null;
            var metadata = catalog[current.AppId];
            var sessionKey = BuildSessionKey(current);
            lock (sync)
            {
                if (publicationGate.IsClosed)
                {
                    return;
                }

                if (!string.Equals(
                        activeSessionKey,
                        sessionKey,
                        StringComparison.Ordinal))
                {
                    EndActiveLocked(now, publishSummary: false);
                    activeAppId = current.AppId;
                    activeName = metadata.Name;
                    activeSessionKey = sessionKey;
                    activeStartedAt = current.StartedAt;
                    activeArtwork = null;

                    // Publish text immediately; cover art is optional background work.
                    PublishActiveLocked(now);
                    StartArtworkRefreshLocked(current.AppId, sessionKey);
                    RuntimeLog.Write(
                        $"Steam game active: appId={current.AppId}; " +
                        $"sourceZone={steamLogTimeZone.Id}; " +
                        $"startedAtUtc={current.StartedAt.UtcDateTime:O}; " +
                        $"display={SteamGameDisplay.FormatStartMeta(current.StartedAt)}.");
                    return;
                }

                PublishActiveLocked(now);
            }
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Steam game probe failed: {exception.GetType().Name}");
        }
    }

    private void StartArtworkRefreshLocked(uint appId, string sessionKey)
    {
        if (publicationGate.IsClosed)
        {
            return;
        }

        CancelArtworkLocked(invalidateGeneration: false);
        var generation = artworkGeneration.Begin();
        var refreshCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellation.Token);
        artworkCancellation = refreshCancellation;
        var refresh = RefreshArtworkAsync(
            appId,
            sessionKey,
            generation,
            refreshCancellation.Token);
        TrackArtworkTaskLocked(refresh, refreshCancellation);
    }

    private async Task RefreshArtworkAsync(
        uint appId,
        string sessionKey,
        ArtworkGeneration generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = await CacheArtworkAsync(appId, cancellationToken);
            if (path is null)
            {
                return;
            }

            lock (sync)
            {
                if (publicationGate.IsClosed ||
                    !artworkGeneration.IsCurrent(generation) ||
                    activeAppId != appId ||
                    !string.Equals(
                        activeSessionKey,
                        sessionKey,
                        StringComparison.Ordinal))
                {
                    return;
                }

                activeArtwork = path;
                PublishActiveLocked(DateTimeOffset.UtcNow);
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
                $"Steam artwork refresh failed: " +
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

    private void PublishActiveLocked(DateTimeOffset now)
    {
        if (activeAppId is null || string.IsNullOrWhiteSpace(activeName))
        {
            return;
        }

        var elapsed = NonNegative(now - activeStartedAt);
        _ = PublishIfActive(OverlayRequest.Active(
            "game-active",
            OverlayKind.GameActive,
            OverlaySource.Steam,
            activeName,
            $"已游玩 {FormatDuration(elapsed)}",
            dedupKey: $"steam:{activeAppId}",
            visual: new OverlayVisualData(
                Eyebrow: "STEAM 游戏",
                Meta: SteamGameDisplay.FormatStartMeta(activeStartedAt),
                ArtworkPath: activeArtwork,
                AccentHex: "#83CAFF")));
    }

    private void EndActiveLocked(
        DateTimeOffset now,
        bool publishSummary = true)
    {
        if (activeAppId is null)
        {
            return;
        }

        var endedAppId = activeAppId.Value;
        var endedName = activeName ?? "游戏已结束";
        var endedArtwork = activeArtwork;
        var endedStartedAt = activeStartedAt;
        CancelArtworkLocked(invalidateGeneration: true);
        _ = PublishIfActive(OverlayRequest.End(
            "game-active",
            OverlayKind.GameActive,
            OverlaySource.Steam));
        if (publishSummary)
        {
            var elapsed = NonNegative(now - endedStartedAt);
            _ = PublishIfActive(OverlayRequest.Timed(
                "game-summary",
                OverlayKind.GameSummary,
                OverlaySource.Steam,
                endedName,
                $"本次游玩 {FormatDuration(elapsed)}",
                dedupKey:
                    $"steam-summary:{endedAppId}:" +
                    endedStartedAt.ToUnixTimeMilliseconds(),
                visual: new OverlayVisualData(
                    Eyebrow: "本次游戏",
                    ArtworkPath: endedArtwork,
                    AccentHex: "#83CAFF")));
        }

        activeAppId = null;
        activeName = null;
        activeSessionKey = null;
        activeArtwork = null;
        activeStartedAt = default;
    }

    private void CancelArtworkLocked(bool invalidateGeneration)
    {
        if (invalidateGeneration)
        {
            artworkGeneration.Invalidate();
        }

        artworkCancellation?.Cancel();
        artworkCancellation = null;
    }

    private async Task<string?> CacheArtworkAsync(
        uint appId,
        CancellationToken cancellationToken)
    {
        string? temporary = null;
        try
        {
            var folder = Path.Combine(
                Path.GetTempPath(),
                "HS2.CrystalOverlay",
                "steam-artwork");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{appId}.jpg");
            if (File.Exists(path) &&
                new FileInfo(path).Length is > 1024 and < MaximumArtworkBytes)
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
                new Uri(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg"));
            using var response = await artworkClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length &&
                (length <= 1024 || length >= MaximumArtworkBytes))
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
                    if (total >= MaximumArtworkBytes)
                    {
                        return null;
                    }

                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        token);
                }
            }

            if (new FileInfo(temporary).Length <= 1024)
            {
                return null;
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

    private static SteamCatalogReadResult ReadCatalog(
        string steamPath)
    {
        try
        {
            var libraries = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                steamPath,
            };
            var libraryFile = Path.Combine(
                steamPath,
                "steamapps",
                "libraryfolders.vdf");
            if (!TryAddLibraryFolders(
                    libraryFile,
                    libraries,
                    out var libraryFailure))
            {
                return SteamCatalogReadResult.Failure(libraryFailure);
            }

            var result = new Dictionary<uint, SteamGameMetadata>();
            foreach (var library in libraries)
            {
                if (!TryReadLibrary(
                        library,
                        result,
                        out var readFailure))
                {
                    return SteamCatalogReadResult.Failure(readFailure);
                }
            }

            return SteamCatalogReadResult.Success(result);
        }
        catch (Exception exception)
        {
            return SteamCatalogReadResult.Failure(
                $"unexpected-{exception.GetType().Name}");
        }
    }

    private static bool TryAddLibraryFolders(
        string libraryFile,
        ISet<string> libraries,
        out string failure)
    {
        failure = string.Empty;
        try
        {
            try
            {
                _ = File.GetAttributes(libraryFile);
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }

            var text = File.ReadAllText(libraryFile);
            foreach (Match match in LibraryPath().Matches(text))
            {
                var path = match.Groups["path"].Value.Replace(
                    @"\\",
                    @"\",
                    StringComparison.Ordinal);
                if (string.IsNullOrWhiteSpace(path))
                {
                    failure = "library-path-empty";
                    return false;
                }

                libraries.Add(Path.GetFullPath(path));
            }

            return true;
        }
        catch (Exception exception)
        {
            failure = $"libraryfolders-{exception.GetType().Name}";
            return false;
        }
    }

    private static bool TryReadLibrary(
        string library,
        IDictionary<uint, SteamGameMetadata> catalog,
        out string failure)
    {
        failure = string.Empty;
        try
        {
            var steamApps = Path.Combine(library, "steamapps");
            foreach (var manifest in Directory.EnumerateFiles(
                         steamApps,
                         "appmanifest_*.acf",
                         SearchOption.TopDirectoryOnly))
            {
                var metadata = SteamManifestParser.Parse(
                    File.ReadAllText(manifest));
                if (metadata is null)
                {
                    failure = "manifest-invalid";
                    return false;
                }

                catalog[metadata.AppId] = metadata;
            }

            return true;
        }
        catch (Exception exception)
        {
            failure = $"library-{exception.GetType().Name}";
            return false;
        }
    }

    private void LogCatalogState(string state)
    {
        if (string.Equals(
                state,
                lastCatalogDiagnostic,
                StringComparison.Ordinal))
        {
            return;
        }

        lastCatalogDiagnostic = state;
        RuntimeLog.Write($"Steam catalog: {state}.");
    }

    private static bool IsGame(SteamGameMetadata metadata)
    {
        var name = metadata.Name;
        return !name.Contains(
                   "Wallpaper Engine",
                   StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(
                   "Redistributable",
                   StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(
                   "Dedicated Server",
                   StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(
                   "SteamVR",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTail(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(maximumBytes, stream.Length);
        if (length <= 0)
        {
            return string.Empty;
        }

        stream.Seek(-length, SeekOrigin.End);
        var bytes = new byte[length];
        var read = stream.Read(bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    private static string? ReadSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Valve\Steam");
            var value = key?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(value)
                ? null
                : Path.GetFullPath(value);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSessionKey(SteamRunningGame game) =>
        $"{game.AppId}:{game.StartedAt.UtcDateTime.Ticks}";

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours} 小时 {value.Minutes} 分钟";
        }

        return $"{Math.Max(1, (int)value.TotalMinutes)} 分钟";
    }

    private bool PublishIfActive(OverlayRequest request) =>
        publicationGate.TryPublish(() => publisher.Publish(request));

    [GeneratedRegex(
        @"""path""\s*""(?<path>(?:\\.|[^""])*)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

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
            artworkClient.Dispose();
            cancellation.Dispose();
            return;
        }

        _ = completion.ContinueWith(
            completedTasks =>
            {
                _ = completedTasks.Exception;
                artworkClient.Dispose();
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
