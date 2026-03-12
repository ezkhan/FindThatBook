using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FindThatBook.Models;
using FindThatBook.Models.Gemini;
using Microsoft.Extensions.Options;

namespace FindThatBook.Services
{
    public class GeminiService : IGeminiService
    {
        private readonly HttpClient _httpClient;
        private readonly GeminiOptions _options;
        private readonly IPromptProvider _promptProvider;
        private readonly ILogger<GeminiService> _logger;

        /// <summary>
        /// Set to true when Gemini returns 429 (Too Many Requests). All further calls within
        /// the same service instance (i.e. the same HTTP request) are skipped immediately,
        /// avoiding redundant rate-limited round-trips for the remaining explanation calls.
        /// </summary>
        private volatile bool _rateLimited;

        private static readonly JsonSerializerOptions CaseInsensitive =
            new() { PropertyNameCaseInsensitive = true };

        public GeminiService(
            HttpClient httpClient,
            IOptions<GeminiOptions> options,
            IPromptProvider promptProvider,
            ILogger<GeminiService> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _promptProvider = promptProvider;
            _logger = logger;
        }

        public async Task<ExtractedFields> ExtractFieldsAsync(
            SearchQuery query,
            CancellationToken ct = default)
        {
            var userInputLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(query.Title))
                userInputLines.Add($"- Explicit title hint (may be partial or misspelled): \"{query.Title}\"");

            if (!string.IsNullOrWhiteSpace(query.Author))
                userInputLines.Add($"- Explicit author hint (may be partial or misspelled): \"{query.Author}\"");

            if (!string.IsNullOrWhiteSpace(query.FreeText))
                userInputLines.Add($"- Free-text input: \"{query.FreeText}\"");

            var prompt = _promptProvider.ExtractionTemplate
                .Replace("{{USER_INPUT}}", string.Join('\n', userInputLines));

            var text = await CallGeminiAsync(prompt, jsonMode: true, ct);

            if (string.IsNullOrWhiteSpace(text))
                return new ExtractedFields();

            try
            {
                var raw = JsonSerializer.Deserialize<GeminiExtractionDto>(text, CaseInsensitive);
                if (raw is null) return new ExtractedFields();

                return new ExtractedFields
                {
                    Title = NullIfBlank(raw.Title),
                    // If the user supplied an explicit title, the extracted value is user-input derived
                    // even if Gemini normalized it. If it came from free-text analysis it is AI-extracted.
                    TitleSource = !string.IsNullOrWhiteSpace(query.Title)
                        ? FieldSource.UserInput
                        : FieldSource.AiExtracted,
                    Author = NullIfBlank(raw.Author),
                    AuthorSource = !string.IsNullOrWhiteSpace(query.Author)
                        ? FieldSource.UserInput
                        : FieldSource.AiExtracted,
                    Keywords = raw.Keywords ?? [],
                    Suggestions = (raw.Suggestions ?? [])
                        .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                        .Select(s => new AiBookSuggestion
                        {
                            Title = s.Title!,
                            Author = NullIfBlank(s.Author),
                            Reason = s.Reason ?? string.Empty
                        })
                        .ToList()
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse Gemini extraction response: {Text}", text);
                return new ExtractedFields();
            }
        }

        public async Task<(string Text, bool IsAiGenerated)> GenerateExplanationAsync(
            BookCandidate candidate,
            string originalQuery,
            ExtractedFields extractedFields,
            CancellationToken ct = default)
        {
            var authors = candidate.PrimaryAuthors.Count > 0
                ? string.Join(", ", candidate.PrimaryAuthors)
                : "unknown author";

            var contributorsLine = candidate.Contributors.Count > 0
                ? $" (contributors: {string.Join(", ", candidate.Contributors)})"
                : string.Empty;

            var titleContext = !string.IsNullOrWhiteSpace(candidate.Title)
                ? $"The book title is \"{candidate.Title}\". "
                : string.Empty;
            var yearContext = candidate.FirstPublishYear.HasValue
                ? $"It was first published in {candidate.FirstPublishYear}. "
                : string.Empty;
            var matchContext = $"{titleContext}{yearContext}{DescribeMatchTier(candidate.MatchTier)}";

            // Build an AI suggestions line so the model knows what evidence drove the search,
            // especially for KeywordFallback candidates where no title/author token matched.
            var suggestionsLine = extractedFields.Suggestions.Count > 0
                ? "AI recognised the following from the query: "
                  + string.Join("; ", extractedFields.Suggestions.Select(
                      s => $"\"{s.Title}\"{(s.Author != null ? $" by {s.Author}" : "")} — {s.Reason}"))
                : string.Empty;

            var prompt = _promptProvider.ExplanationTemplate
                .Replace("{{ORIGINAL_QUERY}}", originalQuery)
                .Replace("{{BOOK_TITLE}}", candidate.Title)
                .Replace("{{AUTHORS}}", authors)
                .Replace("{{CONTRIBUTORS_LINE}}", contributorsLine)
                .Replace("{{FIRST_PUBLISHED}}", candidate.FirstPublishYear?.ToString() ?? "unknown")
                .Replace("{{MATCH_BASIS}}", matchContext)
                .Replace("{{AI_SUGGESTIONS}}", suggestionsLine);

            var text = await CallGeminiAsync(prompt, jsonMode: false, ct, temperature: 0.4f);

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning(
                    "Explanation fallback used for '{Title}' — Gemini returned no text",
                    candidate.Title);
                return ($"Matched via {DescribeMatchTier(candidate.MatchTier)}.", false);
            }

            return (text.Trim(), true);
        }

        // -------------------------------------------------------------------------
        // Core HTTP helper
        // -------------------------------------------------------------------------

        private async Task<string?> CallGeminiAsync(
            string prompt,
            bool jsonMode,
            CancellationToken ct,
            float temperature = 0.1f)
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                _logger.LogWarning("Gemini API key is not configured. Skipping AI call.");
                return null;
            }

            if (_rateLimited)
            {
                _logger.LogWarning("Gemini call skipped — rate limit was hit earlier in this request.");
                return null;
            }

            var request = new GeminiRequest
            {
                Contents =
                [
                    new GeminiContent { Parts = [new GeminiPart { Text = prompt }] }
                ],
                GenerationConfig = new GeminiGenerationConfig
                {
                    Temperature = temperature,
                    ResponseMimeType = jsonMode ? "application/json" : null
                }
            };

            var url = $"v1beta/models/{_options.Model}:generateContent?key={_options.ApiKey}";

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _httpClient.PostAsJsonAsync(url, request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Gemini API HTTP request failed");
                return null;
            }

            if (!httpResponse.IsSuccessStatusCode)
            {
                var error = await httpResponse.Content.ReadAsStringAsync(ct);

                if (httpResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _rateLimited = true;
                    var retryAfter = httpResponse.Headers.RetryAfter?.Delta
                        ?? httpResponse.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow;
                    _logger.LogWarning(
                        "Gemini rate limit hit (429). Further calls this request will be skipped. " +
                        "Retry-After: {RetryAfter}. Response: {Error}",
                        retryAfter.HasValue ? $"{retryAfter.Value.TotalSeconds:F0}s" : "not specified",
                        error);
                }
                else
                {
                    _logger.LogError(
                        "Gemini API returned {Status}: {Error}",
                        httpResponse.StatusCode, error);
                }

                return null;
            }

            var geminiResponse = await httpResponse.Content
                .ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);

            var candidate = geminiResponse?.Candidates.FirstOrDefault();

            if (candidate is null)
            {
                _logger.LogWarning("Gemini returned no candidates");
                return null;
            }

            if (candidate.FinishReason is not null
                && candidate.FinishReason != "STOP"
                && candidate.FinishReason != "MAX_TOKENS")
            {
                _logger.LogWarning("Gemini candidate finished with reason {Reason} — content may be null",
                    candidate.FinishReason);
            }

            var text = candidate.Content?.Parts.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
                _logger.LogWarning("Gemini returned an empty text response (finishReason={Reason})",
                    candidate.FinishReason);

            return text;
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private static string DescribeMatchTier(MatchTier tier) => tier switch
        {
            MatchTier.ExactTitlePrimaryAuthor     => "exact title and primary author match",
            MatchTier.ExactTitleContributorAuthor => "exact title match; author listed as contributor, not primary author",
            MatchTier.ExactTitleOnly              => "exact title match; author not confirmed",
            MatchTier.NearMatchTitleAuthor        => "near-match on title with author match",
            MatchTier.NearMatchTitleOnly          => "near-match on title; author not confirmed",
            MatchTier.AuthorFallback              => "author-only match; top work by this author",
            _                                     => "keyword or AI suggestion match; no exact title or author token was found in the query"
        };

        private static string? NullIfBlank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        // -------------------------------------------------------------------------
        // Private DTOs — only used for deserializing Gemini's JSON output
        // -------------------------------------------------------------------------

        private sealed class GeminiExtractionDto
        {
            public string? Title { get; set; }
            public string? Author { get; set; }
            public List<string>? Keywords { get; set; }
            public List<GeminiSuggestionDto>? Suggestions { get; set; }
        }

        private sealed class GeminiSuggestionDto
        {
            public string? Title { get; set; }
            public string? Author { get; set; }
            public string? Reason { get; set; }
        }
    }
}
