using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class LyricTimelineTests
{
    [Fact]
    public void NeteaseJsonLines_AreJoinedAndTimed()
    {
        const string lyric = """
            {"t":1000,"c":[{"tx":"河流"},{"tx":"穿过"},{"tx":"光影"}]}
            {"t":5000,"c":[{"tx":"下一句"}]}
            """;

        var timeline = LyricTimeline.Parse(lyric);
        var frame = timeline.At(TimeSpan.FromMilliseconds(3000));

        Assert.NotNull(frame);
        Assert.Equal("河流穿过光影", frame.Text);
        Assert.Equal(0.5, frame.LineProgress, 2);
    }

    [Fact]
    public void TraditionalLrc_ParsesMultipleTimestampForms()
    {
        const string lyric = """
            [00:01.50]第一句
            [00:05:250]第二句
            """;

        var timeline = LyricTimeline.Parse(lyric);

        Assert.Equal(
            "第一句",
            timeline.At(TimeSpan.FromSeconds(2))?.Text);
        Assert.Equal(
            "第二句",
            timeline.At(TimeSpan.FromSeconds(6))?.Text);
    }

    [Fact]
    public void OffsetAndBounds_AreAppliedWithoutInventingAFrame()
    {
        const string lyric = """
            {"t":2000,"c":[{"tx":"第一句"}]}
            {"t":4000,"c":[{"tx":"第二句"}]}
            """;

        var timeline = LyricTimeline.Parse(lyric, offsetMilliseconds: 500);

        Assert.Null(timeline.At(TimeSpan.FromSeconds(2)));
        Assert.Equal(
            "第一句",
            timeline.At(TimeSpan.FromSeconds(2.5))?.Text);
        Assert.Equal(
            1,
            timeline.At(TimeSpan.FromSeconds(9))?.LineProgress);
    }

    [Fact]
    public void BilingualDocument_AlignsTranslationByTimestamp()
    {
        const string original = """
            [00:20.860]あと一度だけ奇跡は起こるだろう
            [00:27.070]優しい声で描く歪んだ未来
            """;
        const string translated = """
            [by:translator]
            [00:20.860]奇迹还会最后降临一次吧
            [00:27.070]在那被温婉声线描摹的扭曲的未来
            """;
        var document = NeteaseLyricDocument.Parse(
            original,
            translated);

        var frame = document.At(TimeSpan.FromSeconds(22));

        Assert.NotNull(frame.Original);
        Assert.NotNull(frame.Translation);
        Assert.Equal(
            "あと一度だけ奇跡は起こるだろう",
            frame.Original.Text);
        Assert.Equal(
            "奇迹还会最后降临一次吧",
            frame.Translation.Text);
    }

    [Fact]
    public void BilingualDocument_DoesNotReuseTranslationForAnotherLine()
    {
        const string original = """
            [00:10.000]第一句
            [00:15.000]第二句
            """;
        const string translated = """
            [00:10.000]First line
            """;
        var document = NeteaseLyricDocument.Parse(
            original,
            translated);

        var frame = document.At(TimeSpan.FromSeconds(16));

        Assert.Equal("第二句", frame.Original?.Text);
        Assert.Null(frame.Translation);
    }
}
