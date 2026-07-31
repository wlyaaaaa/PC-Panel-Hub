using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HS2.CrystalOverlay.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HS2_CrystalOverlay;

internal sealed record NeteaseLocalMediaObservation(
    NeteaseTrackMetadata Track,
    string TrackInstance,
    bool IsPlaying,
    bool AudioStateIsAuthoritative);

internal sealed class NeteaseLocalMediaProbe
{
    private readonly NeteaseLyricCacheReader lyricCache = new();
    private readonly NeteasePlayingListReader playingList = new();
    private readonly NeteaseWindowTitleReader windowTitle = new();
    private readonly NeteaseAudioSessionProbe audio = new();
    private IReadOnlySet<int> cachedProcessIds = new HashSet<int>();
    private DateTimeOffset processIdsReadAt = DateTimeOffset.MinValue;

    internal string LastReadState { get; private set; } = "not-sampled";

    internal NeteaseLocalMediaObservation? Read(DateTimeOffset now)
    {
        var processIds = ReadCloudMusicProcessIds(now);
        if (processIds.Count == 0)
        {
            LastReadState = "no-cloudmusic-process";
            return null;
        }

        var catalog = playingList.Read();
        if (catalog is null)
        {
            LastReadState = "no-playing-list";
            return null;
        }

        var trackCandidates = windowTitle.ReadTracks(
            processIds,
            catalog);
        if (trackCandidates.Count == 0)
        {
            LastReadState = "current-track-window-unmatched";
            return null;
        }

        var selectedTrack = SelectTrack(trackCandidates);
        if (selectedTrack is null)
        {
            LastReadState = "current-track-cache-ambiguous";
            return null;
        }

        var track = selectedTrack;

        var instance = track.CacheKey;
        var audioState = audio.Read(processIds);
        var isPlaying = audioState == NeteaseAudioState.Playing;
        LastReadState = audioState switch
        {
            NeteaseAudioState.Playing => "ready-audio",
            NeteaseAudioState.Inactive => "audio-inactive",
            _ => "audio-state-unavailable",
        };
        return new NeteaseLocalMediaObservation(
            track,
            instance,
            isPlaying,
            audioState != NeteaseAudioState.Unknown);
    }

    private NeteaseTrackMetadata? SelectTrack(
        IReadOnlyList<NeteaseTrackMetadata> candidates)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var withLyrics = candidates
            .Select(track => new
            {
                Track = track,
                Lyrics = lyricCache.Read(track.CacheKey),
            })
            .Where(candidate => candidate.Lyrics is not null)
            .OrderByDescending(candidate => candidate.Lyrics!.WrittenAt)
            .ToArray();
        if (withLyrics.Length == 0 ||
            withLyrics.Length > 1 &&
            withLyrics[0].Lyrics!.WrittenAt ==
            withLyrics[1].Lyrics!.WrittenAt)
        {
            return null;
        }

        return withLyrics[0].Track;
    }

    private IReadOnlySet<int> ReadCloudMusicProcessIds(
        DateTimeOffset now)
    {
        if (now - processIdsReadAt < TimeSpan.FromSeconds(2))
        {
            return cachedProcessIds;
        }

        cachedProcessIds = Process
            .GetProcessesByName("cloudmusic")
            .Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            })
            .ToHashSet();
        processIdsReadAt = now;
        return cachedProcessIds;
    }
}

internal sealed class NeteasePlayingListReader
{
    private readonly string path = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "NetEase",
        "CloudMusic",
        "webdata",
        "file",
        "playingList");
    private DateTime lastWriteTimeUtc;
    private long lastLength = -1;
    private NeteasePlayingList? cached;

    internal NeteasePlayingList? Read()
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                cached = null;
                return null;
            }

            if (cached is not null &&
                file.LastWriteTimeUtc == lastWriteTimeUtc &&
                file.Length == lastLength)
            {
                return cached;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            cached = NeteasePlayingList.Parse(reader.ReadToEnd());
            lastWriteTimeUtc = file.LastWriteTimeUtc;
            lastLength = file.Length;
            return cached;
        }
        catch (JsonException)
        {
            return cached;
        }
        catch (IOException)
        {
            return cached;
        }
        catch (UnauthorizedAccessException)
        {
            return cached;
        }
    }
}

internal enum NeteaseAudioState
{
    Unknown,
    Inactive,
    Playing,
}

internal sealed class NeteaseAudioSessionProbe
{
    private const long CacheMilliseconds = 750;
    private NeteaseAudioState cachedState;
    private long cachedAtTick;
    private bool hasCachedState;

    internal NeteaseAudioState Read(IReadOnlySet<int> processIds)
    {
        var now = Environment.TickCount64;
        if (hasCachedState &&
            now - cachedAtTick is >= 0 and < CacheMilliseconds)
        {
            return cachedState;
        }

        cachedState = ReadFresh(processIds);
        cachedAtTick = now;
        hasCachedState = true;
        return cachedState;
    }

    private static NeteaseAudioState ReadFresh(
        IReadOnlySet<int> processIds)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            var found = false;
            for (var index = 0; index < sessions.Count; index++)
            {
                using var session = sessions[index];
                int processId;
                try
                {
                    processId = (int)session.GetProcessID;
                }
                catch (COMException)
                {
                    continue;
                }

                if (!processIds.Contains(processId))
                {
                    continue;
                }

                found = true;
                if (session.State ==
                    AudioSessionState.AudioSessionStateActive)
                {
                    return NeteaseAudioState.Playing;
                }
            }

            return found
                ? NeteaseAudioState.Inactive
                : NeteaseAudioState.Unknown;
        }
        catch (COMException)
        {
            return NeteaseAudioState.Unknown;
        }
        catch (InvalidOperationException)
        {
            return NeteaseAudioState.Unknown;
        }
    }
}
