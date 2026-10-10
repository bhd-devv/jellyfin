using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models.LanguageLearningDtos;
using Jellyfin.Api.Services.LanguageLearning;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public sealed class LanguageLearningControllerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ProviderHandler _provider = new();
    private readonly LanguageLearningService _service;
    private readonly Mock<IDbContextFactory<JellyfinDbContext>> _factory = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();

    public LanguageLearningControllerTests()
    {
        _connection.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();
        context.Users.AddRange(
            new User("learner", "auth", "reset") { Id = _userId },
            new User("other", "auth", "reset") { Id = _otherUserId });
        context.SaveChanges();
        _factory.Setup(value => value.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateContext);

        var library = new Mock<ILibraryManager>();
        library.Setup(value => value.GetItemById<BaseItem>(_itemId, It.IsAny<Guid>())).Returns(new Movie { Id = _itemId });
        var sources = new Mock<IMediaSourceManager>();
        sources.Setup(value => value.GetPlaybackMediaSources(It.IsAny<BaseItem>(), It.IsAny<User>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaSourceInfo { Id = "source", MediaStreams = [new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "srt", Language = "eng" }] }]);
        var encoder = new Mock<ISubtitleEncoder>();
        var subtitles = JsonSerializer.SerializeToUtf8Bytes(new SubtitleTrackInfo
        {
            TrackEvents = [new SubtitleTrackEvent("1", "Before that. Go now.") { EndPositionTicks = TimeSpan.FromSeconds(2).Ticks }]
        });
        encoder.Setup(value => value.GetSubtitles(It.IsAny<BaseItem>(), "source", 2, "json", 0, 0, true, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<Stream>(new MemoryStream(subtitles)));
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(value => value.CreateClient(nameof(OpenRouterClient))).Returns(() => new HttpClient(_provider, false));
        var options = Options.Create(new LanguageLearningOptions { ApiKey = "test-key" });
        _service = new LanguageLearningService(library.Object, Mock.Of<IUserManager>(), sources.Object, encoder.Object, new OpenRouterClient(clients.Object, options), options);
    }

    [Fact]
    public async Task Lookup_UsesOpenRouterAndCachesContextualAnalysis()
    {
        var first = await _service.LookupAsync(_userId, Request(0), CancellationToken.None);
        var expanded = await _service.LookupAsync(_userId, Request(2), CancellationToken.None);

        Assert.Equal("Go now.", first.Sentence);
        Assert.Null(first.SentenceTranslation);
        Assert.Equal("verb", first.PartOfSpeech);
        Assert.Equal("Переклад: Go now.", expanded.SentenceTranslation);
        Assert.Equal("Before that.", Assert.Single(expanded.PreviousSentences).Text);
        Assert.Equal(first.VocabularyId, expanded.VocabularyId);
        Assert.Equal(3, _provider.RequestCount);
        Assert.Equal("openai/gpt-6-luna", _provider.Model);
        Assert.Equal("Bearer test-key", _provider.Authorization);
    }

    [Fact]
    public async Task Save_IsIdempotentAndPreservesVerifiedContext()
    {
        var lookup = await _service.LookupAsync(_userId, Request(1), CancellationToken.None);
        var controller = Controller(_userId);

        var first = await controller.Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);
        var second = await controller.Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);

        Assert.Equal(first.Value!.Id, second.Value!.Id);
        using var context = CreateContext();
        var entry = Assert.Single(context.UserVocabularyEntries);
        Assert.Equal(_userId, entry.UserId);
        Assert.Equal("Go", entry.Word);
        Assert.Equal("Go now.", entry.Sentence);
        Assert.Equal("Переклад: Go now.", entry.SentenceTranslation);
    }

    [Fact]
    public async Task Vocabulary_IsIsolatedBetweenUsers()
    {
        var lookup = await _service.LookupAsync(_userId, Request(0), CancellationToken.None);
        var owner = Controller(_userId);
        var other = Controller(_otherUserId);
        await owner.Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);

        var denied = await other.Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(denied.Result);
        Assert.Empty((await other.List(cancellationToken: CancellationToken.None)).Value!);
        await other.Delete(lookup.VocabularyId, CancellationToken.None);
        Assert.Single((await owner.List(cancellationToken: CancellationToken.None)).Value!);
        await owner.Delete(lookup.VocabularyId, CancellationToken.None);
        Assert.Empty((await owner.List(cancellationToken: CancellationToken.None)).Value!);
    }

    [Fact]
    public async Task Save_TranslatesSentenceWithoutRequiringPlayerExpansion()
    {
        var lookup = await _service.LookupAsync(_userId, Request(0), CancellationToken.None);
        Assert.Null(lookup.SentenceTranslation);
        var saved = await Controller(_userId).Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);
        Assert.Equal("Переклад: Go now.", saved.Value!.SentenceTranslation);
        Assert.Equal("Move away.", saved.Value.Explanation);
        Assert.Equal(2, _provider.RequestCount);
    }

    [Fact]
    public async Task List_SearchesSavedExplanationsAndEscapesWildcards()
    {
        var lookup = await _service.LookupAsync(_userId, Request(0), CancellationToken.None);
        var controller = Controller(_userId);
        await controller.Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);
        Assert.Single((await controller.List(searchTerm: "MOVE", cancellationToken: CancellationToken.None)).Value!);
        Assert.Single((await controller.List(searchTerm: "Переклад", cancellationToken: CancellationToken.None)).Value!);
        Assert.Empty((await controller.List(searchTerm: "%", cancellationToken: CancellationToken.None)).Value!);
        Assert.Empty((await controller.List(searchTerm: "_", cancellationToken: CancellationToken.None)).Value!);
        Assert.Empty((await Controller(_otherUserId).List(searchTerm: "Go", cancellationToken: CancellationToken.None)).Value!);
    }

    [Fact]
    public async Task TranslateSentence_BackfillsOwnEntryWithoutSubtitleAccess()
    {
        var lookup = await _service.LookupAsync(_userId, Request(0), CancellationToken.None);
        await Controller(_userId).Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);
        using (var context = CreateContext())
        {
            var entry = Assert.Single(context.UserVocabularyEntries);
            entry.SentenceTranslation = null;
            entry.ItemId = Guid.NewGuid();
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var forbidden = await Controller(_otherUserId).TranslateSentence(lookup.VocabularyId, CancellationToken.None);
        Assert.IsType<NotFoundResult>(forbidden.Result);
        var result = await Controller(_userId).TranslateSentence(lookup.VocabularyId, CancellationToken.None);
        Assert.Equal("Переклад: Go now.", result.Value!.SentenceTranslation);
        using var saved = CreateContext();
        Assert.Equal("Переклад: Go now.", Assert.Single(saved.UserVocabularyEntries).SentenceTranslation);
    }

    [Fact]
    public async Task Save_ProviderFailureKeepsEntryUnsavedAndAllowsRetry()
    {
        var lookup = await _service.LookupAsync(_userId, Request(0), CancellationToken.None);
        _provider.FailureStatus = HttpStatusCode.BadGateway;
        var controller = Controller(_userId);
        var failed = await controller.Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(failed.Result).StatusCode);
        using (var context = CreateContext())
        {
            Assert.Empty(context.UserVocabularyEntries);
        }

        _provider.FailureStatus = null;
        var saved = await controller.Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);
        Assert.NotNull(saved.Value!.SentenceTranslation);
    }

    [Fact]
    public async Task Lookup_RejectsUnverifiedWordBeforeCallingProvider()
    {
        var request = Request(0);
        request.Word = "injected";
        await Assert.ThrowsAsync<ArgumentException>(() => _service.LookupAsync(_userId, request, CancellationToken.None));
        Assert.Equal(0, _provider.RequestCount);
    }

    [Fact]
    public async Task DeletingUser_CascadesVocabulary()
    {
        var lookup = await _service.LookupAsync(_userId, Request(0), CancellationToken.None);
        await Controller(_userId).Save(new SaveVocabularyRequest { LookupId = lookup.LookupId }, CancellationToken.None);
        using var context = CreateContext();
        await context.Users.Where(user => user.Id.Equals(_userId)).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        Assert.Empty(context.UserVocabularyEntries);
    }

    public void Dispose()
    {
        _service.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private LookupRequest Request(int level) => new()
    {
        ItemId = _itemId,
        MediaSourceId = "source",
        SubtitleStreamIndex = 2,
        CueIndex = 0,
        WordOffset = 13,
        Word = "Go",
        Level = level
    };

    private LanguageLearningController Controller(Guid userId) => new(_service, _factory.Object, NullLogger<LanguageLearningController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(InternalClaimTypes.UserId, userId.ToString())], "test")) }
        }
    };

    private JellyfinDbContext CreateContext() => new(
        new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public HttpStatusCode? FailureStatus { get; set; }

        public int RequestCount { get; private set; }

        public string? Model { get; private set; }

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (FailureStatus.HasValue)
            {
                return new HttpResponseMessage(FailureStatus.Value);
            }

            Authorization = request.Headers.Authorization?.ToString();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Model = body.RootElement.GetProperty("model").GetString();
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            object output = input.RootElement.TryGetProperty("sentences", out var sentences)
                ? new { translations = sentences.EnumerateArray().Select(value => "Переклад: " + value.GetString()).ToArray() }
                : new { lemma = "go", translation = "іти", explanation = "Move away.", partOfSpeech = "verb" };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(output) } } } }), Encoding.UTF8, "application/json")
            };
        }
    }
}
