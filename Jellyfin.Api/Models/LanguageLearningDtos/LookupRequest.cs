#pragma warning disable CS1591

using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.LanguageLearningDtos;

public sealed class LookupRequest
{
    public Guid ItemId { get; set; }

    [Required]
    [StringLength(128)]
    public required string MediaSourceId { get; set; }

    [Range(0, int.MaxValue)]
    public int SubtitleStreamIndex { get; set; }

    [Range(0, int.MaxValue)]
    public int CueIndex { get; set; }

    [Range(0, 10000)]
    public int WordOffset { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string Word { get; set; }

    [Range(0, 2)]
    public int Level { get; set; }
}
