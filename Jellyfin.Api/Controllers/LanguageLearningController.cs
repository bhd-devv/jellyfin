using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.LanguageLearningDtos;
using Jellyfin.Api.Services.LanguageLearning;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Provides contextual subtitle translations and a personal vocabulary.
/// </summary>
[Authorize]
[Route("LanguageLearning")]
public sealed class LanguageLearningController(
    LanguageLearningService service,
    IDbContextFactory<JellyfinDbContext> contextFactory,
    ILogger<LanguageLearningController> logger) : BaseJellyfinApiController
{
    /// <summary>
    /// Explains the selected English subtitle word, optionally translating its context into Ukrainian.
    /// </summary>
    /// <param name="request">Selected subtitle word and expansion level.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The contextual explanation and translations.</returns>
    [HttpPost("Lookup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LookupResponse>> Lookup([FromBody] LookupRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var userId = User.GetUserId();
            var result = await service.LookupAsync(userId, request, cancellationToken).ConfigureAwait(false);
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            result.IsSaved = await context.UserVocabularyEntries.AnyAsync(entry => entry.Id.Equals(result.VocabularyId) && entry.UserId.Equals(userId), cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (LanguageLearningBusyException)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many language learning requests. Try again shortly.");
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Language learning is unavailable");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Configure the OpenRouter API key on the server to enable language learning.");
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Language learning provider failed: {ExceptionType}", exception.GetType().Name);
            return StatusCode(StatusCodes.Status502BadGateway, "Translation is temporarily unavailable. Try again.");
        }
    }

    /// <summary>
    /// Saves a recently explained word and its original sentence for the current user.
    /// </summary>
    /// <param name="request">Lookup to save.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The saved vocabulary entry.</returns>
    [HttpPost("Vocabulary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserVocabularyEntry>> Save([FromBody] SaveVocabularyRequest request, CancellationToken cancellationToken)
    {
        UserVocabularyEntry entry;
        try
        {
            entry = service.GetVocabularyEntry(User.GetUserId(), request.LookupId);
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(exception.Message);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.UserVocabularyEntries.FindAsync([entry.Id], cancellationToken).ConfigureAwait(false);
        if (entry.SentenceTranslation is null)
        {
            try
            {
                entry.SentenceTranslation = existing?.SentenceTranslation
                    ?? await service.TranslateVocabularySentenceAsync(entry.UserId, entry.Sentence, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTranslationError(exception, cancellationToken))
            {
                return TranslationError(exception);
            }
        }

        if (existing is not null)
        {
            if (entry.SentenceTranslation is not null)
            {
                existing.SentenceTranslation = entry.SentenceTranslation;
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return existing;
        }

        context.UserVocabularyEntries.Add(entry);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            context.Entry(entry).State = EntityState.Detached;
            var concurrent = await context.UserVocabularyEntries.AsNoTracking().FirstOrDefaultAsync(value => value.Id.Equals(entry.Id) && value.UserId.Equals(entry.UserId), cancellationToken).ConfigureAwait(false);
            if (concurrent is null)
            {
                throw;
            }

            return concurrent;
        }

        return entry;
    }

    /// <summary>
    /// Lists the current user's saved words, newest first.
    /// </summary>
    /// <param name="startIndex">Number of entries to skip.</param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <param name="searchTerm">Optional word, sentence, translation, or explanation search.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The saved entries.</returns>
    [HttpGet("Vocabulary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UserVocabularyEntry>>> List(
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, 100)] int limit = 50,
        [FromQuery, StringLength(200)] string? searchTerm = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.UserVocabularyEntries.AsNoTracking().Where(entry => entry.UserId.Equals(userId));
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var pattern = "%" + searchTerm.Trim().Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(entry => EF.Functions.Like(entry.Word, pattern, "\\")
                || EF.Functions.Like(entry.Lemma, pattern, "\\")
                || EF.Functions.Like(entry.Sentence, pattern, "\\")
                || EF.Functions.Like(entry.Translation, pattern, "\\")
                || EF.Functions.Like(entry.SentenceTranslation, pattern, "\\")
                || EF.Functions.Like(entry.Explanation, pattern, "\\"));
        }

        return await query
            .OrderByDescending(entry => entry.DateCreated).ThenBy(entry => entry.Id).Skip(startIndex).Take(limit)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Translates the original sentence of an existing vocabulary entry when needed.
    /// </summary>
    /// <param name="entryId">Saved entry id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The entry with its saved Ukrainian sentence translation.</returns>
    [HttpPost("Vocabulary/{entryId}/Translation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserVocabularyEntry>> TranslateSentence([FromRoute] Guid entryId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await context.UserVocabularyEntries.FirstOrDefaultAsync(value => value.Id.Equals(entryId) && value.UserId.Equals(userId), cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound();
        }

        if (entry.SentenceTranslation is null)
        {
            try
            {
                entry.SentenceTranslation = await service.TranslateVocabularySentenceAsync(userId, entry.Sentence, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTranslationError(exception, cancellationToken))
            {
                return TranslationError(exception);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return entry;
    }

    /// <summary>
    /// Removes a word from the current user's vocabulary.
    /// </summary>
    /// <param name="entryId">Saved entry id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Vocabulary/{entryId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Delete([FromRoute] Guid entryId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.UserVocabularyEntries.Where(entry => entry.Id.Equals(entryId) && entry.UserId.Equals(userId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private static bool IsTranslationError(Exception exception, CancellationToken cancellationToken)
        => exception is LanguageLearningBusyException or InvalidOperationException or HttpRequestException or InvalidDataException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private ObjectResult TranslationError(Exception exception)
    {
        if (exception is LanguageLearningBusyException)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many language learning requests. Try again shortly.");
        }

        if (exception is InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Configure the OpenRouter API key on the server to enable language learning.");
        }

        logger.LogWarning("Vocabulary translation provider failed: {ExceptionType}", exception.GetType().Name);
        return StatusCode(StatusCodes.Status502BadGateway, "Translation is temporarily unavailable. Try again.");
    }
}
