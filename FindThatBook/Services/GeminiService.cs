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
        private readonly ILogger<GeminiService> _logger;

        private static readonly JsonSerializerOptions CaseInsensitive =
            new() { PropertyNameCaseInsensitive = true };

        public GeminiService(
            HttpClient httpClient,
            IOptions<GeminiOptions> options,
            ILogger<GeminiService> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<ExtractedFields> ExtractFieldsAsync(
            SearchQuery query,
            CancellationToken ct = default)
        {
            var promptLines = new List<string>
            {
                "You are a book identification assistant. Analyze the user query and return a JSON object.",
                "",
                "User input:"
            };

            if (!string.IsNullOrWhiteSpace(query.Title))
                promptLines.Add($"- Explicit title hint (may be partial or misspelled): \"{query.Title}\"");

            if (!string.IsNullOrWhiteSpace(query.Author))
                promptLines.Add($"- Explicit author hint (may be partial or misspelled): \"{query.Author}\"");

            if (!string.IsNullOrWhiteSpace(query.FreeText))
                promptLines.Add($"- Free-text input: \"{query.FreeText}\"");

            promptLines.AddRange([
                "",
                "Return ONLY a valid JSON object with this exact structure:",
                "{",
                "  \"title\": \"<extracted/normalized book title, or null>\",",
                "  \"author\": \"<extracted/normalized author name, or null>\",",
                "  \"keywords\": [\"<keyword1>\", \"<keyword2>\"],",
                "  \"suggestions\": [",
                "    {",
                "      \"title\": \"<book title>\",",
                "      \"author\": \"<author name, or null>\",",
                "      \"reason\": \"<one sentence citing specific evidence from the query>\"",
                "    }",
                "  ]",
                "}",
                "",
                "Rules:",
                "- title: normalize and extract a book title from the query tokens, or null if none present",
                "- author: normalize and extract an author name from the query tokens, or null if none present",
                "- keywords: 2-5 relevant search terms from the query, excluding extracted title/author tokens",
                "- suggestions: books you recognize from your knowledge that match the query — including matches",
                "  via plot hints, character names, quotes, themes, or described scenarios. Up to 3, ordered by",
                "  confidence. Each MUST include a reason grounded in specific query evidence.",
                "- Do not wrap the JSON in markdown code blocks."
            ]);

            var prompt = string.Join('\n', promptLines);
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
                    Author = NullIfBlank(raw.Author),
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

        public async Task<string> GenerateExplanationAsync(
            BookCandidate candidate,
            string originalQuery,
            CancellationToken ct = default)
        {
            var authors = candidate.PrimaryAuthors.Count > 0
                ? string.Join(", ", candidate.PrimaryAuthors)
                : "unknown author";

            var contributors = candidate.Contributors.Count > 0
                ? $" (contributors: {string.Join(", ", candidate.Contributors)})"
                : string.Empty;

            var prompt = $"""
                You are a book identification assistant. Write a 1-2 sentence explanation of why this book matches the user's query.

                User query: "{originalQuery}"
                Book: "{candidate.Title}" by {authors}{contributors}
                First published: {candidate.FirstPublishYear?.ToString() ?? "unknown"}
                Match basis: {DescribeMatchTier(candidate.MatchTier)}

                Requirements:
                - Cite specific evidence from the query (matched title tokens, author name, plot or theme recognition, etc.)
                - If the match came from AI knowledge rather than keyword parsing, say so briefly
                - If contributors (illustrators, editors) were relevant to the match, mention the distinction
                - Plain text only — no JSON, no markdown, no bullet points
                """;

            var text = await CallGeminiAsync(prompt, jsonMode: false, ct);

            return string.IsNullOrWhiteSpace(text)
                ? $"Matched based on {DescribeMatchTier(candidate.MatchTier)}."
                : text.Trim();
        }

        // -------------------------------------------------------------------------
        // Core HTTP helper
        // -------------------------------------------------------------------------

        private async Task<string?> CallGeminiAsync(
            string prompt,
            bool jsonMode,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                _logger.LogWarning("Gemini API key is not configured. Skipping AI call.");
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
                    Temperature = 0.1f,
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
                _logger.LogError(
                    "Gemini API returned {Status}: {Error}",
                    httpResponse.StatusCode, error);
                return null;
            }

            var geminiResponse = await httpResponse.Content
                .ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);

            return geminiResponse?
                .Candidates.FirstOrDefault()?
                .Content?
                .Parts.FirstOrDefault()?
                .Text;
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
            _                                     => "AI-recognized match from query context"
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
