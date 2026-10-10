using System;

namespace Jellyfin.Api.Services.LanguageLearning;

/// <summary>
/// Indicates that language learning request limits have been reached.
/// </summary>
public sealed class LanguageLearningBusyException : Exception;
