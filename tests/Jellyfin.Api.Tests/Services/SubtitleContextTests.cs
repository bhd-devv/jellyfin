using System;
using Jellyfin.Api.Services.LanguageLearning;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Api.Tests.Services;

public class SubtitleContextTests
{
    [Fact]
    public void Create_JoinsCuesAndSelectsExactOccurrence()
    {
        SubtitleTrackEvent[] cues =
        [
            Cue("Before that. I think", 0, 1),
            Cue("that this will work. Another sentence.", 1, 2)
        ];

        var result = SubtitleContext.Create(cues, 1, 5, "this");

        Assert.Equal("I think that this will work.", result.Sentence);
        Assert.Equal(13, result.WordOffset);
        Assert.Equal("Before that.", Assert.Single(result.PreviousSentences));
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, result.StartPositionTicks);
    }

    [Fact]
    public void Create_PreservesAbbreviationsAndContractions()
    {
        var result = SubtitleContext.Create([Cue("Dr. Smith said he can't go.", 0, 1)], 0, 18, "can't");

        Assert.Equal("Dr. Smith said he can't go.", result.Sentence);
        Assert.Equal(18, result.WordOffset);
    }

    [Fact]
    public void Create_SeparatesLongPausesAndLimitsPreviousSentences()
    {
        var result = SubtitleContext.Create(
            [Cue("One. Two. Three. Four. An unfinished line", 0, 1), Cue("Go now", 10, 11)], 1, 0, "Go");

        Assert.Equal("Go now", result.Sentence);
        Assert.Equal(["Three.", "Four.", "An unfinished line"], result.PreviousSentences);
    }

    [Theory]
    [InlineData(-1, 0, "word")]
    [InlineData(2, 0, "word")]
    [InlineData(0, 1, "ord")]
    [InlineData(0, 0, "other")]
    public void Create_RejectsInvalidSelection(int cue, int offset, string word)
    {
        Assert.Throws<ArgumentException>(() => SubtitleContext.Create([Cue("word", 0, 1)], cue, offset, word));
    }

    [Theory]
    [InlineData("{\\an8}<i>Hello</i>\\Nworld &amp; friends!", "Hello world & friends!")]
    [InlineData("<b>can't</b><br />go", "can't go")]
    [InlineData("\u200eHello\n\nworld", "Hello world")]
    public void Normalize_RemovesFormatting(string input, string expected)
    {
        Assert.Equal(expected, SubtitleContext.Normalize(input));
    }

    private static SubtitleTrackEvent Cue(string text, int start, int end)
        => new("cue", text) { StartPositionTicks = TimeSpan.FromSeconds(start).Ticks, EndPositionTicks = TimeSpan.FromSeconds(end).Ticks };
}
