#pragma warning disable CS1591

using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Database.Implementations.Entities;

public sealed class UserVocabularyEntry
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid ItemId { get; set; }

    [MaxLength(128)]
    public required string MediaSourceId { get; set; }

    public int SubtitleStreamIndex { get; set; }

    public int CueIndex { get; set; }

    public int WordOffset { get; set; }

    public long StartPositionTicks { get; set; }

    [MaxLength(100)]
    public required string Word { get; set; }

    [MaxLength(200)]
    public required string Lemma { get; set; }

    [MaxLength(100)]
    public required string PartOfSpeech { get; set; }

    [MaxLength(2000)]
    public required string Translation { get; set; }

    [MaxLength(4000)]
    public required string Explanation { get; set; }

    [MaxLength(4000)]
    public required string Sentence { get; set; }

    public string? SentenceTranslation { get; set; }

    public DateTime DateCreated { get; set; }
}
