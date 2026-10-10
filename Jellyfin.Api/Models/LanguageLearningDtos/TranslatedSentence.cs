#pragma warning disable CS1591

namespace Jellyfin.Api.Models.LanguageLearningDtos;

public sealed class TranslatedSentence
{
    public required string Text { get; set; }

    public required string Translation { get; set; }
}
