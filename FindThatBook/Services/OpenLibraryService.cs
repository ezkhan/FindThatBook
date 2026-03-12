using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FindThatBook.Models.OpenLibrary;

namespace FindThatBook.Services
{
    public class OpenLibraryService : IOpenLibraryService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OpenLibraryService> _logger;

        // Fields requested from /search.json — keeps payloads small
        private const string SearchFields = "key,title,author_name,author_key,first_publish_year,cover_i,subject";

        public OpenLibraryService(HttpClient httpClient, ILogger<OpenLibraryService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<OlSearchResponse> SearchAsync(
            string? title,
            string? author,
            IEnumerable<string>? keywords,
            int limit = 10,
            CancellationToken ct = default)
        {
            var queryParams = new List<string>();

            if (!string.IsNullOrWhiteSpace(title))
                queryParams.Add($"title={Uri.EscapeDataString(TextNormalizer.Normalize(title))}");

            if (!string.IsNullOrWhiteSpace(author))
                queryParams.Add($"author={Uri.EscapeDataString(TextNormalizer.Normalize(author))}");

            // Keywords are appended as q= alongside any structured fields.
            // OL treats q= as a free-text clause that combines with title=/author= filters.
            // Pure 4-digit year tokens (e.g. "1951") are excluded from q= only when a title
            // or author is already present, because OL's full-text index does not match
            // first_publish_year via q= and including a bare year alongside title= suppresses
            // results. When there is no title/author (e.g. the sole query is "1984"), the
            // token is kept so it reaches OL as the primary search signal.
            bool hasStructuredFields = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(author);
            var keywordList = keywords?
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(TextNormalizer.Normalize)
                .Where(k => !hasStructuredFields || !Regex.IsMatch(k, @"^\d{4}$"))
                .ToList() ?? [];

            if (keywordList.Count > 0)
                queryParams.Add($"q={Uri.EscapeDataString(string.Join(' ', keywordList))}");

            if (queryParams.Count == 0)
                return new OlSearchResponse();

            queryParams.Add($"limit={limit}");
            queryParams.Add($"fields={SearchFields}");

            var url = $"search.json?{string.Join("&", queryParams)}";

            try
            {
                var response = await _httpClient.GetFromJsonAsync<OlSearchResponse>(url, ct);
                return response ?? new OlSearchResponse();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Open Library search failed. URL: {Url}", url);
                return new OlSearchResponse();
            }
        }

        public async Task<OlWorkDetails?> GetWorkAsync(string workKey, CancellationToken ct = default)
        {
            // workKey arrives as "/works/OL27448W"; strip the leading slash for a relative URL
            var url = $"{workKey.TrimStart('/')}.json";

            try
            {
                return await _httpClient.GetFromJsonAsync<OlWorkDetails>(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to fetch work details for key {WorkKey}", workKey);
                return null;
            }
        }

        public async Task<OlAuthorDetails?> GetAuthorAsync(string authorKey, CancellationToken ct = default)
        {
            var url = $"{authorKey.TrimStart('/')}.json";

            try
            {
                return await _httpClient.GetFromJsonAsync<OlAuthorDetails>(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to fetch author details for key {AuthorKey}", authorKey);
                return null;
            }
        }

        public async Task<OlAuthorWorksResponse> GetAuthorWorksAsync(
            string authorKey,
            int limit = 10,
            CancellationToken ct = default)
        {
            var url = $"{authorKey.TrimStart('/')}/works.json?limit={limit}";

            try
            {
                var response = await _httpClient.GetFromJsonAsync<OlAuthorWorksResponse>(url, ct);
                return response ?? new OlAuthorWorksResponse();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to fetch works for author {AuthorKey}", authorKey);
                return new OlAuthorWorksResponse();
            }
        }

        public string GetCoverUrl(int coverId, string size = "M") =>
            $"https://covers.openlibrary.org/b/id/{coverId}-{size}.jpg";
    }
}
