#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace Jellyfin.Api.Models.LanguageLearningDtos;

public sealed class LookupResponse
{
    public Guid LookupId { get; set; }

    public Guid VocabularyId { get; set; }

    public bool IsSaved { get; set; }

    public required string Word { get; set; }

    public required string Lemma { get; set; }

    public required string Translation { get; set; }

    public required string Explanation { get; set; }

    public required string PartOfSpeech { get; set; }

    public required string Sentence { get; set; }

    public int WordOffset { get; set; }

    public long StartPositionTicks { get; set; }

    public string? SentenceTranslation { get; set; }

    public IReadOnlyList<TranslatedSentence> PreviousSentences { get; set; } = [];
}
