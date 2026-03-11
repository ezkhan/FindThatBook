using FindThatBook.Models;
using FindThatBook.Models.OpenLibrary;
using FindThatBook.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FindThatBook.Tests
{
    public class BookSearchServiceTests
    {
        private static readonly OlSearchDoc HobbitDoc = new()
        {
            Key = "/works/OL27448W",
            Title = "The Hobbit",
            AuthorName = ["J.R.R. Tolkien"],
            AuthorKey = ["/authors/OL26320A"],
            FirstPublishYear = 1937,
            CoverId = 12345
        };

        private static readonly OlWorkDetails HobbitWorkDetails = new()
        {
            Key = "/works/OL27448W",
            Title = "The Hobbit",
            Authors =
            [
                new OlWorkAuthorEntry { Author = new OlKeyRef { Key = "/authors/OL26320A" }, Role = null }
            ],
            FirstPublishDate = "September 21, 1937"
        };

        private static readonly OlAuthorDetails TolkienAuthor = new()
        {
            Key = "/authors/OL26320A",
            Name = "J.R.R. Tolkien"
        };

        private static BookSearchService CreateService(
            Mock<IOpenLibraryService>? openLibraryMock = null,
            Mock<IGeminiService>? geminiMock = null)
        {
            var ol = openLibraryMock ?? new Mock<IOpenLibraryService>();
            var gemini = geminiMock ?? new Mock<IGeminiService>();
            return new BookSearchService(ol.Object, gemini.Object, NullLogger<BookSearchService>.Instance);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static Mock<IOpenLibraryService> DefaultOpenLibraryMock(
            OlSearchDoc? doc = null,
            OlWorkDetails? workDetails = null,
            OlAuthorDetails? authorDetails = null)
        {
            var mock = new Mock<IOpenLibraryService>();
            var searchDoc = doc ?? HobbitDoc;
            var details = workDetails ?? HobbitWorkDetails;
            var author = authorDetails ?? TolkienAuthor;

            mock.Setup(m => m.SearchAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<IEnumerable<string>?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OlSearchResponse { NumFound = 1, Docs = [searchDoc] });

            mock.Setup(m => m.GetWorkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(details);

            mock.Setup(m => m.GetAuthorAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(author);

            mock.Setup(m => m.GetCoverUrl(It.IsAny<int>(), It.IsAny<string>()))
                .Returns((int id, string size) => $"https://covers.openlibrary.org/b/id/{id}-{size}.jpg");

            return mock;
        }

        private static Mock<IGeminiService> DefaultGeminiMock(
            ExtractedFields? fields = null,
            string explanation = "Matched based on title.")
        {
            var mock = new Mock<IGeminiService>();
            mock.Setup(m => m.ExtractFieldsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(fields ?? new ExtractedFields { Title = "The Hobbit" });
            mock.Setup(m => m.GenerateExplanationAsync(
                    It.IsAny<BookCandidate>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(explanation);
            return mock;
        }

        // ------------------------------------------------------------------
        // SearchAsync — happy path
        // ------------------------------------------------------------------

        [Fact]
        public async Task SearchAsync_ReturnsCandidates_WhenMatchFound()
        {
            var svc = CreateService(DefaultOpenLibraryMock(), DefaultGeminiMock());

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            Assert.Null(result.ErrorMessage);
            Assert.NotEmpty(result.Candidates);
            Assert.Equal("The Hobbit", result.Candidates[0].Title);
        }

        [Fact]
        public async Task SearchAsync_SetsOriginalQuery_FromQueryTitle()
        {
            var svc = CreateService(DefaultOpenLibraryMock(), DefaultGeminiMock());

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            Assert.Contains("The Hobbit", result.OriginalQuery);
        }

        [Fact]
        public async Task SearchAsync_SetsOriginalQuery_FromFreeText()
        {
            var svc = CreateService(DefaultOpenLibraryMock(), DefaultGeminiMock());

            var result = await svc.SearchAsync(new SearchQuery { FreeText = "dragon story" });

            Assert.Contains("dragon story", result.OriginalQuery);
        }

        [Fact]
        public async Task SearchAsync_PopulatesCoverUrl_WhenCoverIdPresent()
        {
            var olMock = DefaultOpenLibraryMock();
            var svc = CreateService(olMock, DefaultGeminiMock());

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            Assert.NotEmpty(result.Candidates);
            Assert.NotNull(result.Candidates[0].CoverUrl);
        }

        [Fact]
        public async Task SearchAsync_PopulatesExplanation_ForEachCandidate()
        {
            var svc = CreateService(DefaultOpenLibraryMock(), DefaultGeminiMock(explanation: "Great match."));

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            Assert.All(result.Candidates, c => Assert.Equal("Great match.", c.Explanation));
        }

        // ------------------------------------------------------------------
        // SearchAsync — Gemini fallback behaviour
        // ------------------------------------------------------------------

        [Fact]
        public async Task SearchAsync_FallsBackToRawTitle_WhenGeminiReturnsEmptyTitle()
        {
            var geminiMock = new Mock<IGeminiService>();
            geminiMock.Setup(m => m.ExtractFieldsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExtractedFields()); // title = null
            geminiMock.Setup(m => m.GenerateExplanationAsync(
                    It.IsAny<BookCandidate>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("fallback");

            var olMock = DefaultOpenLibraryMock();
            var svc = CreateService(olMock, geminiMock);

            await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            // SearchAsync should still call OL search (with the raw title as fallback)
            olMock.Verify(m => m.SearchAsync(
                It.Is<string?>(t => t == "The Hobbit"),
                It.IsAny<string?>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task SearchAsync_FallsBackToRawAuthor_WhenGeminiReturnsEmptyAuthor()
        {
            var geminiMock = new Mock<IGeminiService>();
            geminiMock.Setup(m => m.ExtractFieldsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExtractedFields()); // author = null
            geminiMock.Setup(m => m.GenerateExplanationAsync(
                    It.IsAny<BookCandidate>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("fallback");

            var olMock = DefaultOpenLibraryMock();
            var svc = CreateService(olMock, geminiMock);

            await svc.SearchAsync(new SearchQuery { Author = "Tolkien" });

            olMock.Verify(m => m.SearchAsync(
                It.IsAny<string?>(),
                It.Is<string?>(a => a == "Tolkien"),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }

        // ------------------------------------------------------------------
        // SearchAsync — deduplication
        // ------------------------------------------------------------------

        [Fact]
        public async Task SearchAsync_DeduplicatesCandidatesByWorkId()
        {
            // Both AI-suggested and primary search return the same work key
            var geminiMock = new Mock<IGeminiService>();
            geminiMock.Setup(m => m.ExtractFieldsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExtractedFields
                {
                    Title = "The Hobbit",
                    Suggestions = [new AiBookSuggestion { Title = "The Hobbit", Author = "Tolkien", Reason = "known" }]
                });
            geminiMock.Setup(m => m.GenerateExplanationAsync(
                    It.IsAny<BookCandidate>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("explanation");

            var olMock = DefaultOpenLibraryMock();
            var svc = CreateService(olMock, geminiMock);

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            // Same WorkId should appear only once after de-dup
            var workIds = result.Candidates.Select(c => c.WorkId).ToList();
            Assert.Equal(workIds.Distinct().Count(), workIds.Count);
        }

        // ------------------------------------------------------------------
        // SearchAsync — error handling
        // ------------------------------------------------------------------

        [Fact]
        public async Task SearchAsync_SetsErrorMessage_WhenGeminiThrows()
        {
            var geminiMock = new Mock<IGeminiService>();
            geminiMock.Setup(m => m.ExtractFieldsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Gemini exploded"));

            var svc = CreateService(new Mock<IOpenLibraryService>(), geminiMock);

            var result = await svc.SearchAsync(new SearchQuery { FreeText = "any query" });

            Assert.True(result.HasError);
            Assert.NotNull(result.ErrorMessage);
        }

        [Fact]
        public async Task SearchAsync_SetsErrorMessage_WhenOpenLibraryThrows()
        {
            var geminiMock = DefaultGeminiMock();
            var olMock = new Mock<IOpenLibraryService>();
            olMock.Setup(m => m.SearchAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<IEnumerable<string>?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("OL down"));

            var svc = CreateService(olMock, geminiMock);

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            Assert.True(result.HasError);
        }

        // ------------------------------------------------------------------
        // SearchAsync — candidate fields
        // ------------------------------------------------------------------

        [Fact]
        public async Task SearchAsync_SetsFirstPublishYear_FromSearchDoc()
        {
            var svc = CreateService(DefaultOpenLibraryMock(), DefaultGeminiMock());

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            Assert.NotEmpty(result.Candidates);
            Assert.Equal(1937, result.Candidates[0].FirstPublishYear);
        }

        [Fact]
        public async Task SearchAsync_SetsOpenLibraryUrl_ContainingWorkId()
        {
            var svc = CreateService(DefaultOpenLibraryMock(), DefaultGeminiMock());

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            Assert.NotEmpty(result.Candidates);
            Assert.Contains("/works/OL27448W", result.Candidates[0].OpenLibraryUrl);
        }

        [Fact]
        public async Task SearchAsync_SetsPrimaryAuthors_FromResolvedAuthorName()
        {
            var svc = CreateService(DefaultOpenLibraryMock(), DefaultGeminiMock());

            var result = await svc.SearchAsync(new SearchQuery { Title = "The Hobbit" });

            Assert.NotEmpty(result.Candidates);
            Assert.Contains("J.R.R. Tolkien", result.Candidates[0].PrimaryAuthors);
        }
    }
}
