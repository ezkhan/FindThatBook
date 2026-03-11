using System.Net;
using System.Text.Json;
using FindThatBook.Models;
using FindThatBook.Models.Gemini;
using FindThatBook.Services;
using FindThatBook.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FindThatBook.Tests
{
    public class GeminiServiceTests
    {
        private static GeminiService CreateService(
            TestHttpMessageHandler handler,
            string apiKey = "test-api-key",
            string model = "gemini-2.5-flash")
        {
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
            var options = Options.Create(new GeminiOptions { ApiKey = apiKey, Model = model });
            var promptProvider = new Mock<IPromptProvider>();
            promptProvider.Setup(p => p.ExtractionTemplate).Returns("{{USER_INPUT}}");
            promptProvider.Setup(p => p.ExplanationTemplate)
                .Returns("{{ORIGINAL_QUERY}} {{BOOK_TITLE}} {{AUTHORS}}{{CONTRIBUTORS_LINE}} {{FIRST_PUBLISHED}} {{MATCH_BASIS}}");
            return new GeminiService(client, options, promptProvider.Object, NullLogger<GeminiService>.Instance);
        }

        private static string BuildGeminiResponse(string text) =>
            JsonSerializer.Serialize(new GeminiResponse
            {
                Candidates =
                [
                    new GeminiCandidate
                    {
                        Content = new GeminiContent { Parts = [new GeminiPart { Text = text }] }
                    }
                ]
            });

        // ------------------------------------------------------------------
        // ExtractFieldsAsync
        // ------------------------------------------------------------------

        [Fact]
        public async Task ExtractFieldsAsync_ReturnsEmpty_WhenApiKeyIsNotConfigured()
        {
            var handler = TestHttpMessageHandler.ReturnJson("{}");
            var svc = CreateService(handler, apiKey: "");
            var query = new SearchQuery { FreeText = "a book about dragons" };

            var result = await svc.ExtractFieldsAsync(query);

            Assert.Null(result.Title);
            Assert.Null(result.Author);
            Assert.Empty(result.Keywords);
            Assert.Empty(result.Suggestions);
            // No HTTP call should have been made
            Assert.Null(handler.LastRequest);
        }

        [Fact]
        public async Task ExtractFieldsAsync_ReturnsExtractedFields_WhenGeminiRespondsSuccessfully()
        {
            var extractionJson = JsonSerializer.Serialize(new
            {
                title = "The Hobbit",
                author = "J.R.R. Tolkien",
                keywords = new[] { "fantasy", "dragon" },
                suggestions = new[]
                {
                    new { title = "The Hobbit", author = "J.R.R. Tolkien", reason = "Matches hobbit query" }
                }
            });
            var handler = TestHttpMessageHandler.ReturnJson(BuildGeminiResponse(extractionJson));
            var svc = CreateService(handler);

            var result = await svc.ExtractFieldsAsync(new SearchQuery { FreeText = "hobbit tolkien" });

            Assert.Equal("The Hobbit", result.Title);
            Assert.Equal("J.R.R. Tolkien", result.Author);
            Assert.Contains("fantasy", result.Keywords);
            Assert.Single(result.Suggestions);
            Assert.Equal("The Hobbit", result.Suggestions[0].Title);
        }

        [Fact]
        public async Task ExtractFieldsAsync_ReturnsEmpty_WhenGeminiResponseJsonIsInvalid()
        {
            var handler = TestHttpMessageHandler.ReturnJson(BuildGeminiResponse("not valid json {{"));
            var svc = CreateService(handler);

            var result = await svc.ExtractFieldsAsync(new SearchQuery { FreeText = "something" });

            Assert.Null(result.Title);
            Assert.Null(result.Author);
        }

        [Fact]
        public async Task ExtractFieldsAsync_ReturnsEmpty_OnHttpException()
        {
            var svc = CreateService(TestHttpMessageHandler.Throws());

            var result = await svc.ExtractFieldsAsync(new SearchQuery { FreeText = "something" });

            Assert.Null(result.Title);
            Assert.Null(result.Author);
        }

        [Fact]
        public async Task ExtractFieldsAsync_ReturnsEmpty_WhenHttpStatusIsNotSuccess()
        {
            var handler = TestHttpMessageHandler.ReturnJson("{\"error\":\"bad request\"}", HttpStatusCode.BadRequest);
            var svc = CreateService(handler);

            var result = await svc.ExtractFieldsAsync(new SearchQuery { FreeText = "something" });

            Assert.Null(result.Title);
            Assert.Null(result.Author);
        }

        [Fact]
        public async Task ExtractFieldsAsync_SetsTitleSource_UserInput_WhenQueryHasExplicitTitle()
        {
            var extractionJson = JsonSerializer.Serialize(new
            {
                title = "The Hobbit",
                author = "J.R.R. Tolkien",
                keywords = Array.Empty<string>(),
                suggestions = Array.Empty<object>()
            });
            var handler = TestHttpMessageHandler.ReturnJson(BuildGeminiResponse(extractionJson));
            var svc = CreateService(handler);

            var result = await svc.ExtractFieldsAsync(new SearchQuery { Title = "Hobbit", Author = "Tolkien" });

            Assert.Equal(FieldSource.UserInput, result.TitleSource);
            Assert.Equal(FieldSource.UserInput, result.AuthorSource);
        }

        [Fact]
        public async Task ExtractFieldsAsync_SetsTitleSource_AiExtracted_WhenQueryHasOnlyFreeText()
        {
            var extractionJson = JsonSerializer.Serialize(new
            {
                title = "The Hobbit",
                author = "J.R.R. Tolkien",
                keywords = Array.Empty<string>(),
                suggestions = Array.Empty<object>()
            });
            var handler = TestHttpMessageHandler.ReturnJson(BuildGeminiResponse(extractionJson));
            var svc = CreateService(handler);

            var result = await svc.ExtractFieldsAsync(new SearchQuery { FreeText = "hobbit tolkien" });

            Assert.Equal(FieldSource.AiExtracted, result.TitleSource);
            Assert.Equal(FieldSource.AiExtracted, result.AuthorSource);
        }

        [Fact]
        public async Task ExtractFieldsAsync_FiltersSuggestionsWithBlankTitles()
        {
            var extractionJson = JsonSerializer.Serialize(new
            {
                title = (string?)null,
                author = (string?)null,
                keywords = Array.Empty<string>(),
                suggestions = new[]
                {
                    new { title = "Valid Book", author = "Some Author", reason = "reason" },
                    new { title = "", author = "Other", reason = "reason2" },
                    new { title = "   ", author = (string?)null, reason = "reason3" }
                }
            });
            var handler = TestHttpMessageHandler.ReturnJson(BuildGeminiResponse(extractionJson));
            var svc = CreateService(handler);

            var result = await svc.ExtractFieldsAsync(new SearchQuery { FreeText = "query" });

            Assert.Single(result.Suggestions);
            Assert.Equal("Valid Book", result.Suggestions[0].Title);
        }

        // ------------------------------------------------------------------
        // GenerateExplanationAsync
        // ------------------------------------------------------------------

        [Fact]
        public async Task GenerateExplanationAsync_ReturnsExplanation_WhenGeminiRespondsSuccessfully()
        {
            var explanation = "This book matches your query about hobbits.";
            var handler = TestHttpMessageHandler.ReturnJson(BuildGeminiResponse(explanation));
            var svc = CreateService(handler);
            var candidate = new BookCandidate
            {
                Title = "The Hobbit",
                PrimaryAuthors = ["J.R.R. Tolkien"],
                MatchTier = MatchTier.ExactTitlePrimaryAuthor
            };

            var result = await svc.GenerateExplanationAsync(candidate, "hobbit tolkien");

            Assert.Equal(explanation, result);
        }

        [Fact]
        public async Task GenerateExplanationAsync_ReturnsFallback_WhenGeminiReturnsEmptyText()
        {
            var handler = TestHttpMessageHandler.ReturnJson(BuildGeminiResponse(string.Empty));
            var svc = CreateService(handler);
            var candidate = new BookCandidate
            {
                Title = "The Hobbit",
                PrimaryAuthors = ["J.R.R. Tolkien"],
                MatchTier = MatchTier.ExactTitlePrimaryAuthor
            };

            var result = await svc.GenerateExplanationAsync(candidate, "hobbit tolkien");

            Assert.False(string.IsNullOrWhiteSpace(result));
            Assert.Contains("exact title", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GenerateExplanationAsync_ReturnsFallback_OnHttpException()
        {
            var svc = CreateService(TestHttpMessageHandler.Throws());
            var candidate = new BookCandidate
            {
                Title = "The Hobbit",
                PrimaryAuthors = ["J.R.R. Tolkien"],
                MatchTier = MatchTier.KeywordFallback
            };

            var result = await svc.GenerateExplanationAsync(candidate, "hobbit");

            Assert.False(string.IsNullOrWhiteSpace(result));
        }
    }
}
