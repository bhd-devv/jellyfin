#pragma warning disable CS1591

using System;

namespace Jellyfin.Api.Models.LanguageLearningDtos;

public sealed class SaveVocabularyRequest
{
    public Guid LookupId { get; set; }
}
