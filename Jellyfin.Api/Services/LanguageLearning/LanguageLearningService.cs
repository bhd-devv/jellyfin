#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.LanguageLearningDtos;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jellyfin.Api.Services.LanguageLearning;

public sealed class LanguageLearningService(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IMediaSourceManager mediaSourceManager,
    ISubtitleEncoder subtitleEncoder,
    OpenRouterClient openRouter,
    IOptions<LanguageLearningOptions> options) : IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly SemaphoreSlim _requests = new(4);
    private readonly object _rateLock = new();

    public async Task<LookupResponse> LookupAsync(Guid userId, LookupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            throw new InvalidOperationException("Language learning requires an OpenRouter API key on the server.");
        }

        var item = libraryManager.GetItemById<BaseItem>(request.ItemId, userId)
            ?? throw new KeyNotFoundException("Media item not found.");
        CheckRequestLimit(userId);

        if (!await _requests.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new LanguageLearningBusyException();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            return await LookupInternalAsync(item, userId, request, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _requests.Release();
        }
    }

    public UserVocabularyEntry GetVocabularyEntry(Guid userId, Guid lookupId)
    {
        if (!_cache.TryGetValue<LookupSnapshot>($"lookup:{userId}:{lookupId}", out var snapshot) || snapshot is null)
        {
            throw new KeyNotFoundException("This lookup has expired. Select the word again to save it.");
        }

        var request = snapshot.Request;
        var response = snapshot.Response;
        return new UserVocabularyEntry
        {
            Id = response.VocabularyId,
            UserId = userId,
            ItemId = request.ItemId,
            MediaSourceId = request.MediaSourceId,
            SubtitleStreamIndex = request.SubtitleStreamIndex,
            CueIndex = request.CueIndex,
            WordOffset = response.WordOffset,
            StartPositionTicks = response.StartPositionTicks,
            Word = response.Word,
            Lemma = response.Lemma,
            PartOfSpeech = response.PartOfSpeech,
            Translation = response.Translation,
            Explanation = response.Explanation,
            Sentence = response.Sentence,
            SentenceTranslation = response.SentenceTranslation,
            DateCreated = DateTime.UtcNow
        };
    }

    public async Task<string> TranslateVocabularySentenceAsync(Guid userId, string sentence, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);
        if (sentence.Length > 4000)
        {
            throw new ArgumentException("Subtitle sentence is too long.", nameof(sentence));
        }

        CheckRequestLimit(userId);
        if (!await _requests.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new LanguageLearningBusyException();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            return (await TranslateAsync([sentence], timeout.Token).ConfigureAwait(false))[0];
        }
        finally
        {
            _requests.Release();
        }
    }

    public void Dispose()
    {
        _requests.Dispose();
        _cache.Dispose();
    }

    private async Task<LookupResponse> LookupInternalAsync(BaseItem item, Guid userId, LookupRequest request, CancellationToken cancellationToken)
    {
        var sources = await mediaSourceManager.GetPlaybackMediaSources(item, userManager.GetUserById(userId), false, false, cancellationToken).ConfigureAwait(false);
        var source = sources.FirstOrDefault(value => string.Equals(value.Id, request.MediaSourceId, StringComparison.OrdinalIgnoreCase));
        var track = source?.MediaStreams.FirstOrDefault(value => value.Type == MediaStreamType.Subtitle && value.Index == request.SubtitleStreamIndex);
        if (track is null || !track.IsTextSubtitleStream)
        {
            throw new ArgumentException("Language learning requires a text subtitle track.");
        }

        if (!string.Equals(track.Language, "eng", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(track.Language, "en", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select an English subtitle track for language learning.");
        }

        var trackKey = $"track:{item.Id}:{source!.Id}:{request.SubtitleStreamIndex}";
        if (!_cache.TryGetValue<SubtitleTrackInfo>(trackKey, out var subtitles) || subtitles is null)
        {
            await using var stream = await subtitleEncoder.GetSubtitles(item, source.Id, request.SubtitleStreamIndex, "json", 0, 0, true, cancellationToken).ConfigureAwait(false);
            subtitles = await JsonSerializer.DeserializeAsync<SubtitleTrackInfo>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Unable to read subtitle track.");
            if (subtitles.TrackEvents.Count > 20000 || subtitles.TrackEvents.Any(cue => cue.Text.Length > 10000)
                || subtitles.TrackEvents.Sum(cue => (long)cue.Text.Length) > 2000000)
            {
                throw new ArgumentException("Subtitle track is too large for language learning.");
            }

            _cache.Set(trackKey, subtitles, CacheOptions(TimeSpan.FromMinutes(5), 10));
        }

        var context = SubtitleContext.Create(subtitles.TrackEvents, request.CueIndex, request.WordOffset, request.Word);
        if (context.PreviousSentences.Any(value => value.Length > 4000))
        {
            throw new ArgumentException("Previous subtitle sentence is too long.");
        }

        var analysisKey = "analysis:" + Hash(JsonSerializer.Serialize(new { options.Value.Model, context, Version = 1 }));
        if (!_cache.TryGetValue<OpenRouterClient.WordAnalysis>(analysisKey, out var analysis) || analysis is null)
        {
            analysis = await openRouter.AnalyzeAsync(context, cancellationToken).ConfigureAwait(false);
            _cache.Set(analysisKey, analysis, CacheOptions(TimeSpan.FromHours(6)));
        }

        var identity = Hash(JsonSerializer.Serialize(new { userId, request.ItemId, request.MediaSourceId, request.SubtitleStreamIndex, request.CueIndex, request.WordOffset, context.Word, context.Sentence }));
        var result = new LookupResponse
        {
            LookupId = Guid.NewGuid(),
            VocabularyId = new Guid(Convert.FromHexString(identity)[..16]),
            Word = context.Word,
            Lemma = analysis.Lemma,
            Translation = analysis.Translation,
            Explanation = analysis.Explanation,
            PartOfSpeech = analysis.PartOfSpeech,
            Sentence = context.Sentence,
            WordOffset = context.WordOffset,
            StartPositionTicks = context.StartPositionTicks
        };
        if (request.Level >= 1)
        {
            result.SentenceTranslation = (await TranslateAsync([context.Sentence], cancellationToken).ConfigureAwait(false))[0];
        }

        if (request.Level >= 2 && context.PreviousSentences.Count > 0)
        {
            var translations = await TranslateAsync(context.PreviousSentences, cancellationToken).ConfigureAwait(false);
            result.PreviousSentences = context.PreviousSentences.Select((text, index) => new TranslatedSentence { Text = text, Translation = translations[index] }).ToArray();
        }

        _cache.Set($"lookup:{userId}:{result.LookupId}", new LookupSnapshot(request, result), CacheOptions(TimeSpan.FromMinutes(30)));
        return result;
    }

    private async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> sentences, CancellationToken cancellationToken)
    {
        var key = "translation:" + Hash(JsonSerializer.Serialize(new { options.Value.Model, sentences, Version = 1 }));
        if (_cache.TryGetValue<IReadOnlyList<string>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var result = await openRouter.TranslateAsync(sentences, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, result, CacheOptions(TimeSpan.FromHours(6)));
        return result;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private void CheckRequestLimit(Guid userId)
    {
        lock (_rateLock)
        {
            var window = _cache.GetOrCreate("rate:" + userId, entry =>
            {
                entry.SetSize(1).SetAbsoluteExpiration(TimeSpan.FromMinutes(1));
                return new RequestWindow();
            })!;
            if (++window.Count > 30)
            {
                throw new LanguageLearningBusyException();
            }
        }
    }

    private static MemoryCacheEntryOptions CacheOptions(TimeSpan duration, int size = 1)
        => new MemoryCacheEntryOptions().SetSize(size).SetAbsoluteExpiration(duration);

    private sealed class RequestWindow
    {
        public int Count { get; set; }
    }

    private sealed record LookupSnapshot(LookupRequest Request, LookupResponse Response);
}
