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
    NeteaseLyricDocument Lyrics,
    TimeSpan Position,
    string TrackInstance,
    bool IsPlaying,
    bool AudioStateIsAuthoritative,
    bool UsesPlaybackBridge);

internal sealed class NeteaseLocalMediaProbe : IDisposable
{
    private readonly NeteaseLyricCacheReader lyricCache = new();
    private readonly NeteasePlayingListReader playingList = new();
    private readonly NeteasePlaybackMemoryReader playback = new();
    private readonly NeteasePlaybackBridgeReader playbackBridge = new();
    private readonly NeteaseAudioSessionProbe audio = new();
    private string? lastTrackInstance;
    private TimeSpan lastPosition;
    private DateTimeOffset lastPositionAt;
    private DateTimeOffset preferBridgeUntil;
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

        var lyric = lyricCache.ReadCurrent(now);
        if (lyric is null)
        {
            LastReadState = "no-current-lyric-cache";
            return null;
        }

        var track = playingList.Read()?.FindByCacheKey(lyric.CacheKey);
        if (track is null)
        {
            LastReadState = "lyric-not-in-playing-list";
            return null;
        }

        var usesPlaybackBridge = false;
        NeteasePlaybackMemorySample? playbackSample;
        if (now < preferBridgeUntil)
        {
            playbackSample = playbackBridge.Read(processIds, now);
            usesPlaybackBridge = playbackSample is not null;
            playbackSample ??= playback.Read(processIds);
        }
        else
        {
            playbackSample = playback.Read(processIds);
            if (playbackSample is null)
            {
                playbackSample = playbackBridge.Read(processIds, now);
                usesPlaybackBridge = playbackSample is not null;
                if (usesPlaybackBridge)
                {
                    preferBridgeUntil = now.AddMinutes(5);
                }
            }
        }

        if (playbackSample is null)
        {
            LastReadState = "playback-position-unavailable";
            return null;
        }

        if (track.Duration > TimeSpan.Zero &&
            playbackSample.Position >
            track.Duration + TimeSpan.FromSeconds(3))
        {
            LastReadState = "playback-position-out-of-range";
            return null;
        }

        var instance = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{lyric.CacheKey}@{lyric.WrittenAt.UtcTicks}");
        var audioState = audio.Read(processIds);
        var positionAdvanced =
            string.Equals(
                instance,
                lastTrackInstance,
                StringComparison.Ordinal) &&
            now > lastPositionAt &&
            playbackSample.Position >
            lastPosition + TimeSpan.FromMilliseconds(80);
        var isPlaying = audioState switch
        {
            NeteaseAudioState.Playing => true,
            NeteaseAudioState.Inactive => false,
            _ => positionAdvanced,
        };
        lastTrackInstance = instance;
        lastPosition = playbackSample.Position;
        lastPositionAt = now;
        LastReadState = usesPlaybackBridge
            ? "ready-via-bridge"
            : "ready-direct";
        return new NeteaseLocalMediaObservation(
            track,
            lyric.Lyrics,
            playbackSample.Position,
            instance,
            isPlaying,
            audioState != NeteaseAudioState.Unknown ||
            positionAdvanced,
            usesPlaybackBridge);
    }

    public void Dispose()
    {
        playbackBridge.Dispose();
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
