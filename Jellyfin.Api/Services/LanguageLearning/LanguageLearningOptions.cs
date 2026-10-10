#pragma warning disable CS1591

namespace Jellyfin.Api.Services.LanguageLearning;

public sealed class LanguageLearningOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "openai/gpt-6-luna";
}
