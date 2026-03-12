using System.Text.Json;
using FindThatBook.Models.OpenLibrary;
using FindThatBook.Services;
using FindThatBook.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FindThatBook.Tests
{
    public class OpenLibraryServiceTests
    {
        private static OpenLibraryService CreateService(TestHttpMessageHandler handler)
        {
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://openlibrary.org/") };
            return new OpenLibraryService(client, NullLogger<OpenLibraryService>.Instance);
        }

        // ------------------------------------------------------------------
        // SearchAsync
        // ------------------------------------------------------------------

        [Fact]
        public async Task SearchAsync_ReturnsEmptyResponse_WhenNoParametersProvided()
        {
            var handler = TestHttpMessageHandler.ReturnJson("{\"numFound\":0,\"docs\":[]}");
            var svc = CreateService(handler);

            var result = await svc.SearchAsync(null, null, null);

            Assert.Equal(0, result.NumFound);
            Assert.Empty(result.Docs);
            Assert.Null(handler.LastRequest); // no HTTP call should be made
        }

        [Fact]
        public async Task SearchAsync_IncludesTitleInUrl_WhenTitleProvided()
        {
            var json = JsonSerializer.Serialize(new OlSearchResponse
            {
                NumFound = 1,
                Docs = [new OlSearchDoc { Key = "/works/OL1W", Title = "The Hobbit" }]
            });
            var handler = TestHttpMessageHandler.ReturnJson(json);
            var svc = CreateService(handler);

            await svc.SearchAsync("The Hobbit", null, null);

            var url = handler.LastRequest?.RequestUri?.ToString();
            Assert.NotNull(url);
            Assert.Contains("title=", url);
        }

        [Fact]
        public async Task SearchAsync_IncludesAuthorInUrl_WhenAuthorProvided()
        {
            var json = JsonSerializer.Serialize(new OlSearchResponse());
            var handler = TestHttpMessageHandler.ReturnJson(json);
            var svc = CreateService(handler);

            await svc.SearchAsync(null, "Tolkien", null);

            var url = handler.LastRequest?.RequestUri?.ToString();
            Assert.NotNull(url);
            Assert.Contains("author=", url);
        }

        [Fact]
        public async Task SearchAsync_UsesKeywordsAsFreeText_WhenTitleAndAuthorAbsent()
        {
            var json = JsonSerializer.Serialize(new OlSearchResponse());
            var handler = TestHttpMessageHandler.ReturnJson(json);
            var svc = CreateService(handler);

            await svc.SearchAsync(null, null, ["hobbit", "dragon"]);

            var url = handler.LastRequest?.RequestUri?.ToString();
            Assert.NotNull(url);
            Assert.Contains("q=", url);
        }

        [Fact]
        public async Task SearchAsync_IncludesKeywordsAsQ_WhenTitleIsAlsoPresent()
        {
            var json = JsonSerializer.Serialize(new OlSearchResponse());
            var handler = TestHttpMessageHandler.ReturnJson(json);
            var svc = CreateService(handler);

            await svc.SearchAsync("The Hobbit", null, ["hobbit", "dragon"]);

            var url = handler.LastRequest?.RequestUri?.ToString();
            Assert.NotNull(url);
            Assert.Contains("title=", url);
            Assert.Contains("q=", url);
        }

        [Fact]
        public async Task SearchAsync_ReturnsEmptyResponse_OnHttpException()
        {
            var svc = CreateService(TestHttpMessageHandler.Throws());

            var result = await svc.SearchAsync("The Hobbit", null, null);

            Assert.Equal(0, result.NumFound);
            Assert.Empty(result.Docs);
        }

        [Fact]
        public async Task SearchAsync_ReturnsDeserializedDocs_WhenSuccessful()
        {
            var json = JsonSerializer.Serialize(new OlSearchResponse
            {
                NumFound = 1,
                Docs = [new OlSearchDoc { Key = "/works/OL27448W", Title = "The Hobbit", FirstPublishYear = 1937 }]
            });
            var handler = TestHttpMessageHandler.ReturnJson(json);
            var svc = CreateService(handler);

            var result = await svc.SearchAsync("The Hobbit", null, null);

            Assert.Single(result.Docs);
            Assert.Equal("/works/OL27448W", result.Docs[0].Key);
            Assert.Equal("The Hobbit", result.Docs[0].Title);
            Assert.Equal(1937, result.Docs[0].FirstPublishYear);
        }

        // ------------------------------------------------------------------
        // GetWorkAsync
        // ------------------------------------------------------------------

        [Fact]
        public async Task GetWorkAsync_ReturnsWorkDetails_WhenSuccessful()
        {
            var expected = new OlWorkDetails
            {
                Key = "/works/OL27448W",
                Title = "The Hobbit",
                FirstPublishDate = "September 21, 1937"
            };
            var handler = TestHttpMessageHandler.ReturnJson(JsonSerializer.Serialize(expected));
            var svc = CreateService(handler);

            var result = await svc.GetWorkAsync("/works/OL27448W");

            Assert.NotNull(result);
            Assert.Equal("The Hobbit", result.Title);
            Assert.Equal("September 21, 1937", result.FirstPublishDate);
        }

        [Fact]
        public async Task GetWorkAsync_StripsLeadingSlash_FromWorkKey()
        {
            var handler = TestHttpMessageHandler.ReturnJson(JsonSerializer.Serialize(new OlWorkDetails()));
            var svc = CreateService(handler);

            await svc.GetWorkAsync("/works/OL27448W");

            var url = handler.LastRequest?.RequestUri?.ToString();
            Assert.NotNull(url);
            Assert.DoesNotContain("//", url.Replace("https://", string.Empty));
        }

        [Fact]
        public async Task GetWorkAsync_ReturnsNull_OnHttpException()
        {
            var svc = CreateService(TestHttpMessageHandler.Throws());

            var result = await svc.GetWorkAsync("/works/OL27448W");

            Assert.Null(result);
        }

        // ------------------------------------------------------------------
        // GetAuthorAsync
        // ------------------------------------------------------------------

        [Fact]
        public async Task GetAuthorAsync_ReturnsAuthorDetails_WhenSuccessful()
        {
            var expected = new OlAuthorDetails { Key = "/authors/OL26320A", Name = "J.R.R. Tolkien" };
            var handler = TestHttpMessageHandler.ReturnJson(JsonSerializer.Serialize(expected));
            var svc = CreateService(handler);

            var result = await svc.GetAuthorAsync("/authors/OL26320A");

            Assert.NotNull(result);
            Assert.Equal("J.R.R. Tolkien", result.Name);
        }

        [Fact]
        public async Task GetAuthorAsync_ReturnsNull_OnHttpException()
        {
            var svc = CreateService(TestHttpMessageHandler.Throws());

            var result = await svc.GetAuthorAsync("/authors/OL26320A");

            Assert.Null(result);
        }

        // ------------------------------------------------------------------
        // GetAuthorWorksAsync
        // ------------------------------------------------------------------

        [Fact]
        public async Task GetAuthorWorksAsync_ReturnsWorks_WhenSuccessful()
        {
            var expected = new OlAuthorWorksResponse
            {
                Entries =
                [
                    new OlAuthorWorkEntry { Key = "/works/OL27448W", Title = "The Hobbit" },
                    new OlAuthorWorkEntry { Key = "/works/OL27516W", Title = "The Lord of the Rings" }
                ]
            };
            var handler = TestHttpMessageHandler.ReturnJson(JsonSerializer.Serialize(expected));
            var svc = CreateService(handler);

            var result = await svc.GetAuthorWorksAsync("/authors/OL26320A", limit: 5);

            Assert.Equal(2, result.Entries.Count);
            Assert.Equal("The Hobbit", result.Entries[0].Title);
        }

        [Fact]
        public async Task GetAuthorWorksAsync_IncludesLimitInUrl()
        {
            var handler = TestHttpMessageHandler.ReturnJson(JsonSerializer.Serialize(new OlAuthorWorksResponse()));
            var svc = CreateService(handler);

            await svc.GetAuthorWorksAsync("/authors/OL26320A", limit: 7);

            var url = handler.LastRequest?.RequestUri?.ToString();
            Assert.Contains("limit=7", url);
        }

        [Fact]
        public async Task GetAuthorWorksAsync_ReturnsEmpty_OnHttpException()
        {
            var svc = CreateService(TestHttpMessageHandler.Throws());

            var result = await svc.GetAuthorWorksAsync("/authors/OL26320A");

            Assert.Empty(result.Entries);
        }

        // ------------------------------------------------------------------
        // GetCoverUrl
        // ------------------------------------------------------------------

        [Fact]
        public void GetCoverUrl_ReturnsExpectedUrl_ForDefaultSizeMedium()
        {
            var svc = CreateService(TestHttpMessageHandler.ReturnJson("{}"));

            var url = svc.GetCoverUrl(12345);

            Assert.Equal("https://covers.openlibrary.org/b/id/12345-M.jpg", url);
        }

        [Fact]
        public void GetCoverUrl_RespectsCustomSize()
        {
            var svc = CreateService(TestHttpMessageHandler.ReturnJson("{}"));

            var url = svc.GetCoverUrl(12345, "L");

            Assert.Equal("https://covers.openlibrary.org/b/id/12345-L.jpg", url);
        }
    }
}
