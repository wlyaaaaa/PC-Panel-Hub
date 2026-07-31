using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class NeteaseLocalMediaTests
{
    [Fact]
    public void PlayingList_MapsLyricCacheHashToTrackMetadata()
    {
        const string json = """
            {
              "list": [
                {
                  "id": "300062",
                  "track": {
                    "id": "300062",
                    "duration": 285266,
                    "name": "人间",
                    "transNames": [
                      "Mortal World",
                      "人间"
                    ],
                    "artists": [
                      { "name": "王菲" }
                    ],
                    "album": {
                      "name": "王菲",
                      "picUrl": "https://example.invalid/cover.jpg"
                    }
                  }
                }
              ]
            }
            """;

        var catalog = NeteasePlayingList.Parse(json);
        var track = catalog.FindByCacheKey(
            "4cba64e825a81c8cd4f7fdd063719aa8");

        Assert.NotNull(track);
        Assert.Equal("300062", track.Id);
        Assert.Equal("人间", track.Title);
        Assert.Equal("Mortal World", track.TranslatedTitle);
        Assert.Equal("王菲", track.Artist);
        Assert.Equal("王菲", track.Album);
        Assert.Equal(
            TimeSpan.FromMilliseconds(285266),
            track.Duration);
        Assert.Equal(
            "https://example.invalid/cover.jpg",
            track.ArtworkUri?.AbsoluteUri);
    }

    [Fact]
    public void PlayingList_IgnoresMalformedEntries()
    {
        const string json = """
            {
              "list": [
                { "id": "", "track": { "name": "missing id" } },
                { "id": "42", "track": { "name": "" } }
              ]
            }
            """;

        var catalog = NeteasePlayingList.Parse(json);

        Assert.Null(catalog.FindByCacheKey(
            "a1d0c6e83f027327d8461063f4ac58a6"));
    }

    [Fact]
    public void PlayingList_UsesFirstDistinctTranslatedTitle()
    {
        const string json = """
            {
              "list": [
                {
                  "id": "22803908",
                  "track": {
                    "id": "22803908",
                    "duration": 281000,
                    "name": "天使にふれたよ!",
                    "transNames": [
                      "天使にふれたよ!",
                      "  ",
                      "相遇天使",
                      "第二候选"
                    ],
                    "artists": [{ "name": "放課後ティータイム" }]
                  }
                }
              ]
            }
            """;

        var track = NeteasePlayingList
            .Parse(json)
            .FindByCacheKey("f8e0b341706de83d8c48e5427962523b");

        Assert.NotNull(track);
        Assert.Equal("相遇天使", track.TranslatedTitle);
    }

    [Fact]
    public void PlayingList_MatchesTheExactPlayerWindowTitle()
    {
        const string json = """
            {
              "list": [
                {
                  "id": "101",
                  "track": {
                    "name": "Signal - Live",
                    "duration": 240000,
                    "artists": [{ "name": "Example Artist" }]
                  }
                },
                {
                  "id": "102",
                  "track": {
                    "name": "Signal",
                    "duration": 180000,
                    "artists": [{ "name": "Another Artist" }]
                  }
                }
              ]
            }
            """;

        var track = NeteasePlayingList
            .Parse(json)
            .FindByWindowTitle("Signal - Live - Example Artist");

        Assert.NotNull(track);
        Assert.Equal("101", track.Id);
    }

    [Fact]
    public void PlayingList_DoesNotGuessFromAnUnrelatedWindowTitle()
    {
        const string json = """
            {
              "list": [
                {
                  "id": "101",
                  "track": {
                    "name": "Signal",
                    "duration": 240000,
                    "artists": [{ "name": "Example Artist" }]
                  }
                }
              ]
            }
            """;

        var catalog = NeteasePlayingList.Parse(json);

        Assert.Null(catalog.FindByWindowTitle("Desktop Lyrics"));
        Assert.Null(catalog.FindByWindowTitle("Signal - Other Artist"));
    }

    [Fact]
    public void PlayingList_NormalizesOnlyArtistSlashSpacing()
    {
        const string json = """
            {
              "list": [
                {
                  "id": "101",
                  "track": {
                    "name": "Signal",
                    "duration": 240000,
                    "artists": [
                      { "name": "First Artist" },
                      { "name": "Second Artist" }
                    ]
                  }
                }
              ]
            }
            """;

        var track = NeteasePlayingList
            .Parse(json)
            .FindByWindowTitle(
                "Signal - First Artist/Second Artist");

        Assert.NotNull(track);
        Assert.Equal("101", track.Id);
    }

    [Fact]
    public void PlayingList_PreservesExactDuplicateCandidatesForDisambiguation()
    {
        const string json = """
            {
              "list": [
                {
                  "id": "101",
                  "track": {
                    "name": "Signal",
                    "duration": 240000,
                    "artists": [{ "name": "Example Artist" }]
                  }
                },
                {
                  "id": "102",
                  "track": {
                    "name": "Signal",
                    "duration": 240000,
                    "artists": [{ "name": "Example Artist" }]
                  }
                }
              ]
            }
            """;

        var catalog = NeteasePlayingList.Parse(json);
        var candidates = catalog.FindAllByWindowTitle(
            "Signal - Example Artist");

        Assert.Equal(2, candidates.Count);
        Assert.Null(catalog.FindByWindowTitle(
            "Signal - Example Artist"));
    }

    [Fact]
    public void DecodedAudioSnapshot_ProducesLivePlaybackPosition()
    {
        var snapshot = new byte[
            NeteasePlaybackPositionDecoder.SnapshotSize];
        BitConverter.TryWriteBytes(
            snapshot.AsSpan(0, sizeof(int)),
            96_000);
        snapshot[4] = 2;
        BitConverter.TryWriteBytes(
            snapshot.AsSpan(8, sizeof(ushort)),
            (ushort)4);
        BitConverter.TryWriteBytes(
            snapshot.AsSpan(24, sizeof(ulong)),
            111_546_640UL);

        var decoded = NeteasePlaybackPositionDecoder.TryDecode(
            snapshot,
            out var position);

        Assert.True(decoded);
        Assert.Equal(145.243, position.TotalSeconds, 3);
    }

    [Fact]
    public void DecodedAudioSnapshot_RejectsImplausibleFormat()
    {
        var snapshot = new byte[
            NeteasePlaybackPositionDecoder.SnapshotSize];
        BitConverter.TryWriteBytes(
            snapshot.AsSpan(0, sizeof(int)),
            96_000);
        snapshot[4] = 0;
        BitConverter.TryWriteBytes(
            snapshot.AsSpan(8, sizeof(ushort)),
            (ushort)4);

        Assert.False(NeteasePlaybackPositionDecoder.TryDecode(
            snapshot,
            out _));
    }
}
