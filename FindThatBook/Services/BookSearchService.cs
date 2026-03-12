using System.Text.RegularExpressions;
using FindThatBook.Models;
using FindThatBook.Models.OpenLibrary;

namespace FindThatBook.Services
{
    public class BookSearchService : IBookSearchService
    {
        private const int MaxCandidates = 5;
        private const int OlSearchLimit = 10;
        private const int DetailFetchLimit = 5;    // max work-detail + author calls

        // Hard ceiling for one full search. Individual HttpClient timeouts are per-request;
        // this guards against cumulative fan-out (up to 7 parallel OL calls + 5 Gemini calls)
        // exceeding the total time a user is willing to wait.
        private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

        private readonly IOpenLibraryService _openLibrary;
        private readonly IGeminiService _gemini;
        private readonly IStringSimilarity _stringSimilarity;
        private readonly ILogger<BookSearchService> _logger;

        public BookSearchService(
            IOpenLibraryService openLibrary,
            IGeminiService gemini,
            IStringSimilarity stringSimilarity,
            ILogger<BookSearchService> logger)
        {
            _openLibrary = openLibrary;
            _gemini = gemini;
            _stringSimilarity = stringSimilarity;
            _logger = logger;
        }

        // -------------------------------------------------------------------------
        // Public entry point
        // -------------------------------------------------------------------------

        public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
        {
            // Link the caller's token with a hard deadline so cumulative fan-out
            // (multiple parallel OL + Gemini calls) never blocks indefinitely.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SearchTimeout);
            ct = cts.Token;

            var result = new SearchResult { OriginalQuery = BuildOriginalQuery(query) };

            try
            {
                // 1. AI extracts structured fields and recognizes books from training data
                result.ExtractedFields = await _gemini.ExtractFieldsAsync(query, ct);
                var fields = result.ExtractedFields;

                // Fallback: if AI returned nothing for a field the user explicitly provided,
                // use the raw input directly so a Gemini failure never silently kills the search
                if (string.IsNullOrWhiteSpace(fields.Title) && !string.IsNullOrWhiteSpace(query.Title))
                {
                    fields.Title = query.Title;
                    fields.TitleSource = FieldSource.UserInput;
                }
                if (string.IsNullOrWhiteSpace(fields.Author) && !string.IsNullOrWhiteSpace(query.Author))
                {
                    fields.Author = query.Author;
                    fields.AuthorSource = FieldSource.UserInput;
                }
                // Merge any free-text tokens into keywords so they contribute to scoring
                // regardless of whether title/author were also extracted. This ensures that
                // supplementary signals like a year ("1951") or genre ("illustrated") typed
                // into the FreeText field always reach CalculateScore even when Gemini already
                // parsed a clean title and author from the same query.
                if (!string.IsNullOrWhiteSpace(query.FreeText))
                {
                    var freeTextTokens = query.FreeText
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(t => !fields.Keywords.Contains(t, StringComparer.OrdinalIgnoreCase));
                    fields.Keywords = [.. fields.Keywords, .. freeTextTokens];
                }

                // 2. Collect raw OL docs from field-based search + per-suggestion searches
                var rawDocs = await CollectSearchResultsAsync(fields, query.FreeText, ct);

                // 3. Resolve authors + classify into BookCandidates
                var candidates = await BuildCandidatesAsync(rawDocs, fields, ct);

                // 4. Author-works fallback: when no title matches exist but we have an author
                if (!string.IsNullOrWhiteSpace(fields.Author)
                    && candidates.All(c => c.MatchTier >= MatchTier.AuthorFallback))
                {
                    var authorWorks = await AuthorWorksFallbackAsync(fields, ct);
                    candidates.AddRange(authorWorks);
                }

                // 5. De-dup by WorkId (keep highest score per work), drop zero-score
                //    candidates (no matching evidence against any extracted field), then
                //    sort by score descending and cap at MaxCandidates.
                result.Candidates = candidates
                    .GroupBy(c => c.WorkId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(c => c.MatchScore).First())
                    .Where(c => c.MatchScore > 0)
                    .OrderByDescending(c => c.MatchScore)
                    .ThenBy(c => (int)c.MatchTier)
                    .Take(MaxCandidates)
                    .ToList();

                // 6. Generate AI explanations for all final candidates in parallel
                await Task.WhenAll(result.Candidates.Select(c =>
                    FillExplanationAsync(c, result.OriginalQuery, ct)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Search failed for query: {Query}", query.FreeText);
                result.ErrorMessage = "An error occurred while searching. Please try again.";
            }

            return result;
        }

        // -------------------------------------------------------------------------
        // Step 2 — gather raw OL docs from all search paths
        // -------------------------------------------------------------------------

        private async Task<List<(OlSearchDoc Doc, bool FromAiSuggestion, string AiReason)>>
            CollectSearchResultsAsync(ExtractedFields fields, string? freeText, CancellationToken ct)
        {
            var all = new List<(OlSearchDoc, bool, string)>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddDocs(IEnumerable<OlSearchDoc> docs, bool fromAi, string reason)
            {
                foreach (var doc in docs)
                    if (!string.IsNullOrEmpty(doc.Key) && seenKeys.Add(doc.Key))
                        all.Add((doc, fromAi, reason));
            }

            bool hasTitle    = !string.IsNullOrWhiteSpace(fields.Title);
            bool hasAuthor   = !string.IsNullOrWhiteSpace(fields.Author);
            bool hasKeywords = fields.Keywords.Count > 0;

            // Build the structured search tasks. Each present field gets its own solo search
            // so mismatched pairs (e.g. title "Hamlet" + author "Verne") still surface results
            // for whichever field is correct. The combined search handles cases where all three
            // fields reinforce each other. All tasks run in parallel; dedup via seenKeys
            // prevents the same work appearing more than once across the result set.
            //
            //  hasTitle  →  title-only
            //  hasAuthor →  author-only
            //  hasKeywords → keywords-only
            //  any of the above →  combined (title + author + keywords)
            //  !hasTitle + freeText →  verbatim phrase (fromAi=true for min-score guarantee)
            //
            // Maximum 5 structured OL calls + up to 3 suggestion calls = 8 total, all parallel.

            var tasks = new List<(Task<OlSearchResponse> Task, bool FromAi, string Reason)>();

            if (hasTitle)
                tasks.Add((_openLibrary.SearchAsync(fields.Title, null, null, OlSearchLimit, ct),
                    false, string.Empty));

            if (hasAuthor)
                tasks.Add((_openLibrary.SearchAsync(null, fields.Author, null, OlSearchLimit, ct),
                    false, string.Empty));

            if (hasKeywords)
                tasks.Add((_openLibrary.SearchAsync(null, null, fields.Keywords, OlSearchLimit, ct),
                    false, string.Empty));

            if (hasTitle || hasAuthor || hasKeywords)
                tasks.Add((_openLibrary.SearchAsync(fields.Title, fields.Author, fields.Keywords, OlSearchLimit, ct),
                    false, string.Empty));

            if (!hasTitle && !string.IsNullOrWhiteSpace(freeText))
                tasks.Add((_openLibrary.SearchAsync(null, null, [freeText!], OlSearchLimit, ct),
                    true, "verbatim freetext"));

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks.Select(t => t.Task));
                foreach (var (task, fromAi, reason) in tasks)
                    AddDocs(task.Result.Docs, fromAi, reason);
            }

            // Per-suggestion searches run in parallel (capped at 3 suggestions)
            if (fields.Suggestions.Count > 0)
            {
                var suggestionTasks = fields.Suggestions
                    .Take(3)
                    .Select(async s =>
                    {
                        var response = await _openLibrary.SearchAsync(s.Title, s.Author, null, 3, ct);
                        return (response.Docs, s.Reason);
                    });

                foreach (var (docs, reason) in await Task.WhenAll(suggestionTasks))
                    AddDocs(docs, true, reason);
            }

            return all;
        }

        // -------------------------------------------------------------------------
        // Step 3 — resolve authors + build BookCandidates
        // -------------------------------------------------------------------------

        private async Task<List<BookCandidate>> BuildCandidatesAsync(
            List<(OlSearchDoc Doc, bool FromAiSuggestion, string AiReason)> rawDocs,
            ExtractedFields fields,
            CancellationToken ct)
        {
            // Quick-score without work details, then take only the top N for expensive fetches
            var quickScored = rawDocs
                .Select(r => (r.Doc, r.FromAiSuggestion, r.AiReason,
                    QuickTier: QuickDetermineTier(r.Doc, fields)))
                .OrderBy(r => (int)r.QuickTier)
                .Take(DetailFetchLimit)
                .ToList();

            // Fetch work details in parallel
            var withDetails = await Task.WhenAll(quickScored.Select(async r =>
            {
                var details = await _openLibrary.GetWorkAsync(r.Doc.Key, ct);
                return (r.Doc, r.FromAiSuggestion, r.AiReason, Details: details);
            }));

            // Resolve authors and assign definitive tiers in parallel
            var candidates = await Task.WhenAll(withDetails.Select(async r =>
            {
                var (primaryAuthors, contributors) =
                    await ResolveAuthorsAsync(r.Doc, r.Details, ct);

                var tier = DetermineTier(r.Doc, primaryAuthors, contributors, fields, r.FromAiSuggestion);
                var score = CalculateScore(r.Doc, primaryAuthors, contributors, fields);

                return new BookCandidate
                {
                    Title = r.Doc.Title,
                    PrimaryAuthors = primaryAuthors,
                    Contributors = contributors,
                    FirstPublishYear = r.Doc.FirstPublishYear ?? ParseYear(r.Details?.FirstPublishDate),
                    WorkId = r.Doc.Key,
                    CoverUrl = r.Doc.CoverId.HasValue
                        ? _openLibrary.GetCoverUrl(r.Doc.CoverId.Value)
                        : GetCoverFromDetails(r.Details),
                    MatchTier  = tier,
                    // AI-sourced candidates (suggestions, verbatim phrase) get a guaranteed
                    // minimum of 0.1 so they survive the > 0 filter even when extracted
                    // keywords happen not to appear in the returned title.
                    MatchScore = r.FromAiSuggestion ? Math.Max(score, 0.1) : score
                };
            }));

            return [.. candidates];
        }

        // -------------------------------------------------------------------------
        // Step 4 — author-works fallback
        // -------------------------------------------------------------------------

        private async Task<List<BookCandidate>> AuthorWorksFallbackAsync(
            ExtractedFields fields, CancellationToken ct)
        {
            var author = fields.Author!;
            var searchResponse = await _openLibrary.SearchAsync(null, author, null, 5, ct);
            var authorKey = searchResponse.Docs
                .SelectMany(d => d.AuthorKey ?? [])
                .FirstOrDefault();

            if (string.IsNullOrEmpty(authorKey))
                return [];

            var worksTask = _openLibrary.GetAuthorWorksAsync(authorKey, MaxCandidates, ct);
            var authorTask = _openLibrary.GetAuthorAsync(authorKey, ct);
            await Task.WhenAll(worksTask, authorTask);

            var authorName = authorTask.Result?.Name ?? author;

            return worksTask.Result.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Title))
                .Select(e =>
                {
                    // Author is confirmed (1.0); add keyword signal from title and publish year
                    var normTitle  = TextNormalizer.Normalize(e.Title);
                    var yearString = ParseYear(e.FirstPublishDate)?.ToString();
                    var keywordScore = fields.Keywords.Count > 0
                        ? fields.Keywords
                            .Select(TextNormalizer.Normalize)
                            .Count(k => !string.IsNullOrEmpty(k) &&
                                (normTitle.Contains(k) ||
                                 (!string.IsNullOrEmpty(yearString) && yearString == k)))
                          / (double)fields.Keywords.Count
                        : 0.0;

                    return new BookCandidate
                    {
                        Title = e.Title,
                        PrimaryAuthors = [authorName],
                        Contributors = [],
                        FirstPublishYear = ParseYear(e.FirstPublishDate),
                        WorkId = e.Key,
                        CoverUrl = e.Covers?.FirstOrDefault(c => c > 0) is int coverId
                            ? _openLibrary.GetCoverUrl(coverId)
                            : null,
                        MatchTier  = MatchTier.AuthorFallback,
                        MatchScore = (0.0 * 0.7) + (1.0 * 0.5) + (keywordScore * 0.3)
                    };
                })
                .ToList();
        }

        // -------------------------------------------------------------------------
        // Author resolution — separates primary authors from contributors using
        // the role field on OlWorkAuthorEntry (null role = primary author in OL convention)
        // -------------------------------------------------------------------------

        private async Task<(List<string> PrimaryAuthors, List<string> Contributors)>
            ResolveAuthorsAsync(OlSearchDoc doc, OlWorkDetails? details, CancellationToken ct)
        {
            if (details?.Authors == null || details.Authors.Count == 0)
            {
                // No role info available — treat all search-doc author names as primary
                return (doc.AuthorName ?? [], []);
            }

            var primaryKeys = new List<string>();
            var contributorEntries = new List<(string Key, string Role)>();

            foreach (var entry in details.Authors)
            {
                if (string.IsNullOrEmpty(entry.Author?.Key)) continue;

                if (string.IsNullOrWhiteSpace(entry.Role))
                    primaryKeys.Add(entry.Author.Key);
                else
                    contributorEntries.Add((entry.Author.Key, entry.Role));
            }

            // Resolve primary author display names in parallel
            var primaryNameTasks = primaryKeys.Select(async key =>
            {
                var authorDetails = await _openLibrary.GetAuthorAsync(key, ct);
                return authorDetails?.Name ?? key.Split('/').Last();
            });
            var primaryNames = (await Task.WhenAll(primaryNameTasks)).ToList();

            // Resolve contributor display names in parallel (capped at 3)
            var contributorNameTasks = contributorEntries.Take(3).Select(async c =>
            {
                var authorDetails = await _openLibrary.GetAuthorAsync(c.Key, ct);
                var name = authorDetails?.Name ?? c.Key.Split('/').Last();
                return $"{name} ({c.Role})";
            });
            var contributorNames = (await Task.WhenAll(contributorNameTasks)).ToList();

            return (primaryNames, contributorNames);
        }

        // -------------------------------------------------------------------------
        // Scoring — continuous relevance score used for final ranking
        // -------------------------------------------------------------------------

        /// <summary>
        /// Computes a weighted relevance score for a candidate against the extracted query fields.
        /// Formula: (userInputTitleMatch × 0.7) + (geminiTitleMatch × 0.6)
        ///        + (userInputAuthorMatch × 0.5) + (geminiAuthorMatch × 0.4)
        ///        + (geminiKeywordMatch × 0.3)
        /// Exact matches score 1.0; partial matches are scored by <see cref="IStringSimilarity"/>.
        /// User-supplied fields carry higher weight than AI-inferred equivalents.
        /// Scores above 1.0 are possible when title and author both match strongly.
        /// </summary>
        private double CalculateScore(
            OlSearchDoc doc,
            List<string> primaryAuthors,
            List<string> contributors,
            ExtractedFields fields)
        {
            // --- Title score [0.0–1.0] ---
            double titleScore = 0.0;
            if (!string.IsNullOrWhiteSpace(fields.Title))
            {
                var normDoc   = TextNormalizer.Normalize(doc.Title);
                var normQuery = TextNormalizer.Normalize(fields.Title);

                titleScore = normDoc == normQuery
                    ? 1.0
                    : _stringSimilarity.Similarity(normQuery, normDoc);
            }

            // --- Author score [0.0–1.0] ---
            // Primary author match → full credit; contributor → discounted; partial token overlap → further discounted
            double authorScore = 0.0;
            if (!string.IsNullOrWhiteSpace(fields.Author))
            {
                if (primaryAuthors.Any(a => TextNormalizer.IsNearMatch(fields.Author, a)))
                    authorScore = 1.0;
                else if (contributors.Any(a => TextNormalizer.IsNearMatch(fields.Author, a)))
                    authorScore = 0.6;
                else
                {
                    var normAuthorQuery = TextNormalizer.Normalize(fields.Author);
                    var allAuthors = primaryAuthors
                        .Concat(contributors.Select(c => c.Split('(')[0].Trim()));
                    var best = allAuthors
                        .Select(a => _stringSimilarity.Similarity(normAuthorQuery, TextNormalizer.Normalize(a)))
                        .DefaultIfEmpty(0.0)
                        .Max();
                    authorScore = best * 0.5;   // partial match, discounted
                }
            }

            // --- Keyword score [0.0–1.0] ---
            // Each keyword is checked against: the candidate title, its subjects, and its
            // first-publish year. A year keyword (e.g. "1951") is compared to FirstPublishYear
            // directly so edition-year queries meaningfully re-order results.
            double keywordScore = 0.0;
            if (fields.Keywords.Count > 0)
            {
                var normTitle    = TextNormalizer.Normalize(doc.Title);
                var normSubjects = (doc.Subjects ?? [])
                    .Select(s => TextNormalizer.Normalize(s))
                    .ToList();
                var yearString   = doc.FirstPublishYear?.ToString();

                var matched = fields.Keywords
                    .Select(TextNormalizer.Normalize)
                    .Count(k =>
                        (!string.IsNullOrEmpty(k) && normTitle.Contains(k)) ||
                        normSubjects.Any(s => s.Contains(k)) ||
                        (!string.IsNullOrEmpty(yearString) && yearString == k));

                keywordScore = (double)matched / fields.Keywords.Count;
            }

            // User-supplied fields carry higher weight than AI-inferred equivalents.
            double titleWeight  = fields.TitleSource  == FieldSource.UserInput ? 0.7 : 0.6;
            double authorWeight = fields.AuthorSource == FieldSource.UserInput ? 0.5 : 0.4;

            return (titleScore * titleWeight) + (authorScore * authorWeight) + (keywordScore * 0.3);
        }

        // -------------------------------------------------------------------------
        // Tier determination
        // -------------------------------------------------------------------------

        /// <summary>
        /// Fast estimate using search doc data only (no role info). Used for pre-filtering.
        /// </summary>
        private static MatchTier QuickDetermineTier(OlSearchDoc doc, ExtractedFields fields)
        {
            bool hasQTitle = !string.IsNullOrWhiteSpace(fields.Title);
            bool hasQAuthor = !string.IsNullOrWhiteSpace(fields.Author);

            bool titleExact = hasQTitle &&
                TextNormalizer.Normalize(doc.Title) == TextNormalizer.Normalize(fields.Title!);
            bool titleNear = hasQTitle && TextNormalizer.IsNearMatch(fields.Title!, doc.Title);
            bool authorMatch = hasQAuthor &&
                (doc.AuthorName ?? []).Any(a => TextNormalizer.IsNearMatch(fields.Author!, a));

            if (titleExact && (authorMatch || !hasQAuthor)) return MatchTier.ExactTitlePrimaryAuthor;
            if (titleExact)                                   return MatchTier.ExactTitleOnly;
            if (titleNear  && (authorMatch || !hasQAuthor))  return MatchTier.NearMatchTitleAuthor;
            if (titleNear)                                   return MatchTier.NearMatchTitleOnly;
            if (!hasQTitle && authorMatch)                   return MatchTier.AuthorFallback;
            return MatchTier.KeywordFallback;
        }

        /// <summary>
        /// Definitive tier using resolved primary/contributor author lists from work details.
        /// </summary>
        private static MatchTier DetermineTier(
            OlSearchDoc doc,
            List<string> primaryAuthors,
            List<string> contributors,
            ExtractedFields fields,
            bool fromAiSuggestion)
        {
            bool hasQTitle = !string.IsNullOrWhiteSpace(fields.Title);
            bool hasQAuthor = !string.IsNullOrWhiteSpace(fields.Author);

            bool titleExact = hasQTitle &&
                TextNormalizer.Normalize(doc.Title) == TextNormalizer.Normalize(fields.Title!);
            bool titleNear = hasQTitle && TextNormalizer.IsNearMatch(fields.Title!, doc.Title);

            bool primaryMatch = hasQAuthor &&
                primaryAuthors.Any(a => TextNormalizer.IsNearMatch(fields.Author!, a));
            bool contributorMatch = hasQAuthor &&
                contributors.Any(a => TextNormalizer.IsNearMatch(fields.Author!, a));

            if (titleExact && primaryMatch)                            return MatchTier.ExactTitlePrimaryAuthor;
            if (titleExact && contributorMatch)                        return MatchTier.ExactTitleContributorAuthor;
            if (titleExact)                                            return MatchTier.ExactTitleOnly;
            if (titleNear  && (primaryMatch || contributorMatch))      return MatchTier.NearMatchTitleAuthor;
            if (titleNear)                                             return MatchTier.NearMatchTitleOnly;
            if (!hasQTitle && primaryMatch)                            return MatchTier.AuthorFallback;

            return MatchTier.KeywordFallback;
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private async Task FillExplanationAsync(
            BookCandidate candidate, string originalQuery, CancellationToken ct)
        {
            candidate.Explanation =
                await _gemini.GenerateExplanationAsync(candidate, originalQuery, ct);
        }

        private static string BuildOriginalQuery(SearchQuery query)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(query.FreeText))   parts.Add(query.FreeText.Trim());
            if (!string.IsNullOrWhiteSpace(query.Title))  parts.Add($"title: {query.Title.Trim()}");
            if (!string.IsNullOrWhiteSpace(query.Author)) parts.Add($"author: {query.Author.Trim()}");
            return string.Join(" | ", parts);
        }

        private static int? ParseYear(string? dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return null;
            var match = Regex.Match(dateStr, @"\b(1[0-9]{3}|20[0-9]{2})\b");
            return match.Success ? int.Parse(match.Value) : null;
        }

        private string? GetCoverFromDetails(OlWorkDetails? details)
        {
            var coverId = details?.Covers?.FirstOrDefault(c => c > 0);
            return coverId.HasValue ? _openLibrary.GetCoverUrl(coverId.Value) : null;
        }
    }
}
