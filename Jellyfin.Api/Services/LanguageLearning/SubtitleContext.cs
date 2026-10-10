#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Api.Services.LanguageLearning;

internal sealed record SubtitleContext(string Word, string Sentence, int WordOffset, long StartPositionTicks, IReadOnlyList<string> PreviousSentences)
{
    private static readonly Regex _words = new("[A-Za-z]+(?:['’][A-Za-z]+)*(?:-[A-Za-z]+(?:['’][A-Za-z]+)*)*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex _tags = new("<[^>]*>|\\{\\\\[^}]*\\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex _spaces = new("\\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex _ends = new("[.!?]+[\"”’')]*(?=\\s|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex _abbreviation = new("(?:\\b(?:Mr|Mrs|Ms|Dr|Prof|Sr|Jr|St|vs|etc)|\\b[A-Za-z]|\\b(?:[A-Za-z]\\.)+[A-Za-z])\\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Normalize(string text)
        => _spaces.Replace(WebUtility.HtmlDecode(_tags.Replace(text.Replace("\\N", " ", StringComparison.OrdinalIgnoreCase).Replace("<br>", " ", StringComparison.OrdinalIgnoreCase).Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase).Replace("<br />", " ", StringComparison.OrdinalIgnoreCase).Replace("\u200e", string.Empty, StringComparison.Ordinal).Replace("\u200f", string.Empty, StringComparison.Ordinal), string.Empty)), " ").Trim();

    public static SubtitleContext Create(IReadOnlyList<SubtitleTrackEvent> cues, int cueIndex, int wordOffset, string word)
    {
        if (cueIndex < 0 || cueIndex >= cues.Count)
        {
            throw new ArgumentException("Subtitle cue does not exist.");
        }

        var cueText = Normalize(cues[cueIndex].Text);
        if (!_words.Matches(cueText).Any(match => match.Index == wordOffset && string.Equals(match.Value, word, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The selected word no longer matches the subtitle.");
        }

        var text = new StringBuilder();
        var forcedBreaks = new HashSet<int>();
        var selectedOffset = 0;
        for (var index = 0; index < cues.Count; index++)
        {
            var normalized = Normalize(cues[index].Text);
            if (index > 0)
            {
                text.Append(' ');
                if (cues[index].StartPositionTicks - cues[index - 1].EndPositionTicks > TimeSpan.FromSeconds(4).Ticks
                    || normalized.StartsWith("- ", StringComparison.Ordinal))
                {
                    forcedBreaks.Add(text.Length);
                }
            }

            if (index == cueIndex)
            {
                selectedOffset = text.Length + wordOffset;
            }

            text.Append(normalized);
        }

        var fullText = text.ToString();
        foreach (Match match in _ends.Matches(fullText))
        {
            var end = match.Index + match.Length;
            if (match.Value[0] != '.' || !_abbreviation.IsMatch(fullText.AsSpan(Math.Max(0, end - 32), Math.Min(end, 32))))
            {
                forcedBreaks.Add(end);
            }
        }

        forcedBreaks.Add(fullText.Length);
        var previous = new List<string>();
        var start = 0;
        foreach (var end in forcedBreaks.Order())
        {
            while (start < end && char.IsWhiteSpace(fullText[start]))
            {
                start++;
            }

            if (selectedOffset >= start && selectedOffset < end)
            {
                var sentence = fullText[start..end].TrimEnd();
                if (sentence.Length > 4000)
                {
                    throw new ArgumentException("Subtitle sentence is too long.");
                }

                return new SubtitleContext(word, sentence, selectedOffset - start, cues[cueIndex].StartPositionTicks, previous.TakeLast(3).ToArray());
            }

            if (end > start)
            {
                previous.Add(fullText[start..end].TrimEnd());
            }

            start = end;
        }

        throw new ArgumentException("Unable to locate subtitle sentence.");
    }
}
