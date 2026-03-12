# Find That Book

A book discovery application that accepts messy, partial, or vague natural-language queries and returns ranked candidate matches sourced from [Open Library](https://openlibrary.org), with AI-generated explanations for each result.

Built as a technical challenge demonstrating full-stack .NET 8 skills, real LLM integration, and thoughtful API design.

---

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Design Decisions](#design-decisions)
- [Local Setup & Running](#local-setup--running)
- [Project Structure](#project-structure)
- [API Reference](#api-reference)
- [Testing Strategy](#testing-strategy)
- [Demo Deployment](#demo-deployment)
- [Future Improvements](#future-improvements)

---

## Overview

Users can query with any combination of:

| Input | Example |
|---|---|
| Title hint | `tale two cities` |
| Author hint | `dickens` |
| Free-text description | `tolkien hobbit illustrated deluxe 1937` |

The system handles sparse, noisy, and ambiguous input — including cases where the provided title and author contradict each other — and returns up to 5 ranked candidates, each with a Gemini-generated explanation grounded in the actual data retrieved.

---

## Architecture

```
+-------------------------------------------+
|            Browser / Client               |
+-------------------------------------------+
          |  POST /  (Razor Page form)
          |  POST /api/search  (JSON API)
+-------------------------------------------+
|           ASP.NET Core (.NET 8)           |
|                                           |
|  Pages/Index ----------+                  |
|  Controllers/Api/Search+                  |
|                        |                  |
|           IBookSearchService              |
|           (orchestration layer)           |
|         /        |         \              |
|  IGemini-  IOpenLib-  IStringSimilarity   |
|  Service   aryService  (scoring strategy) |
|    |                                      |
|  IPromptProvider                          |
+-------------------------------------------+
```

### Request flow

```
1. User submits query OneOf(Title? + Author? + FreeText?)
        |
2. GeminiService.ExtractFieldsAsync()
   -> Returns: { title?, author?, keywords[], suggestions[] }
      FieldSource stamped: UserInput if field was explicitly provided,
                           AiExtracted if inferred from FreeText by Gemini
   -> Fallback: raw query inputs used if Gemini fails or returns empty
        |
3. CollectSearchResultsAsync()  -- all OL calls run in a single parallel batch
   -> OL /search.json  (title only)         -- if hasTitle
   -> OL /search.json  (author only)        -- if hasAuthor
   -> OL /search.json  (keywords only)      -- if hasKeywords
   -> OL /search.json  (title+author+keywords combined) -- if any field present
   -> OL /search.json  (verbatim freeText)  -- if !hasTitle and freeText provided
   -> OL /search.json  x N AI suggestions   -- parallel
   Results de-duplicated by work key before proceeding.
        |
4. BuildCandidatesAsync()
   -> QuickDetermineTier() -- pre-filter, no I/O
   -> OL /works/{id}.json   -- top 5               -- parallel
   -> OL /authors/{id}.json -- resolved authors    -- parallel
        |
5. AuthorWorksFallbackAsync()   (if no title matches found)
   -> OL /authors/{id}/works.json
        |
6. Score, de-duplicate by WorkId, sort, cap at 5 candidates
        |
7. GeminiService.GenerateExplanationAsync() x 5  -- parallel
        |
8. Return SearchResult
```

---

## Design Decisions

### Open Library search strategy

Each search fires up to five structured OL `/search.json` calls plus up to three AI-suggestion calls, all in a **single parallel batch**. Results are de-duplicated by work key before scoring.

| Call | Condition | Rationale |
|---|---|---|
| Title-only | `hasTitle` | Ensures title hits are not suppressed by a mismatched or absent author |
| Author-only | `hasAuthor` | Ensures author hits are not suppressed by a mismatched or absent title |
| Keywords-only | `hasKeywords` | Surfaces thematic matches when no title/author is known |
| Combined (title + author + keywords) | any field present | Highest-precision query; all three fields reinforce each other |
| Verbatim free-text | `!hasTitle` and `freeText` provided | Sends the raw phrase as a single `q=` term; OL's own phrase-matching reliably handles quote- and description-style queries (e.g. *"it was the best of times"* → *A Tale of Two Cities*) that sparse extracted keywords would miss |
| Per AI suggestion | `suggestions.Count > 0` (up to 3) | Fires a title+author search for each book Gemini recognised from training knowledge; enables matches that have no parseable title/author tokens in the user's query |

**Why solo searches alongside the combined search?**
The combined query (`title=X&author=Y&q=keywords`) requires OL to satisfy all supplied fields simultaneously. A mismatched pair — e.g. `title: Hamlet, author: Verne` — returns zero results from the combined call even though each field alone would surface the correct book. The solo searches guarantee that a correct title or correct author always produces candidates regardless of what the other fields contain. The combined search is still included because when all fields agree it returns the most relevant results first, improving ranking.

**Candidates from the solo searches subsume all pair combinations.** If `title=X` and `author=Y` are each searched solo, any book that would appear in a `title=X&author=Y` search will also appear in at least one of the two solo results. Explicit pair searches (`title=X&author=Y` without keywords, `title=X&q=keywords` without author, etc.) are therefore redundant and are omitted to keep the call count minimal.

### Fuzzy match scoring

Each candidate receives a continuous relevance score used for final ranking:

```
score = (userInputTitle  x 0.7) + (geminiTitle  x 0.6)
      + (userInputAuthor x 0.5) + (geminiAuthor x 0.4)
      + (keywords        x 0.3)
```

- **User-supplied fields carry higher weight than AI-inferred ones.** `FieldSource.UserInput` (from the explicit Title/Author input fields) uses the higher weight column; `FieldSource.AiExtracted` (Gemini-inferred from free-text) uses the reduced column. This distinction is stamped during extraction and preserved through the fallback path.
- **Exact normalized matches** score 1.0. **Partial matches** are scored via the pluggable `IStringSimilarity` strategy (default: `LevenshteinSimilarity` — `1 - editDistance / max(|a|, |b|)`), giving a score that scales inversely with edit distance.
- **Contributor author matches** are discounted to 0.6 vs 1.0 for primary authors. Partial matches in the contributor fallback are further multiplied by 0.5.
- Scores above 1.0 are possible when title and author both match strongly (e.g. exact user-input title + exact primary author = 1.2).
- **Keywords are matched against title text, `subject` tags, and `first_publish_year`**, so a year token such as `"1951"` contributes to `keywordScore` and thereby raises the overall `MatchScore` of editions whose publish year matches — naturally floating them higher in the score-ordered results without any explicit sort by year.

### FreeText dual purpose

`FreeText` is the unstructured input field and serves two distinct roles in the same request:

1. **Verbatim phrase search** — when no title was extracted, the full `FreeText` string is sent to OL as a single `q=` term. OL's own phrase-matching handles quote- and description-style queries accurately (e.g. *"it was the best of times"* → *A Tale of Two Cities*) even when Gemini only extracts sparse keywords like `["best", "times"]` that would miss the book.

2. **Scoring keyword tokens** — `FreeText` tokens are always merged into `fields.Keywords` after Gemini extraction, deduplicated against anything Gemini already returned. This means supplementary signals typed into FreeText — a year (`"1951"`), a format (`"illustrated"`), a subject (`"dystopia"`) — always reach `CalculateScore` and adjust the candidate's `MatchScore`. The final order is purely score-descending; there is no explicit sort by year or any other keyword field.

The two roles are independent: the verbatim path uses the raw `query.FreeText` string; the keyword-scoring path uses the merged token list.

### Swappable similarity strategy

Partial match scoring is delegated to an `IStringSimilarity` interface following the strategy pattern. Two implementations are provided:

| Implementation | Algorithm | Best for |
|---|---|---|
| `LevenshteinSimilarity` *(default)* | `1 - editDist / max(\|a\|, \|b\|)` | General-purpose fuzzy matching |
| `JaroWinklerSimilarity` | Jaro similarity + common-prefix boost | Proper names and short titles where shared prefixes carry more signal |

To swap implementations, change one line in `Program.cs`:

```csharp
// builder.Services.AddSingleton<IStringSimilarity, LevenshteinSimilarity>();
builder.Services.AddSingleton<IStringSimilarity, JaroWinklerSimilarity>();
```

### Match tier (categorical display)

A `MatchTier` enum is retained alongside the continuous score purely for the UI badge:

| Tier | Condition |
|---|---|
| `ExactTitlePrimaryAuthor` | Exact normalized title + primary author |
| `ExactTitleContributorAuthor` | Exact title + contributor (not primary) author |
| `ExactTitleOnly` | Exact title — author unconfirmed or no match |
| `NearMatchTitleAuthor` | All title tokens present + author match |
| `NearMatchTitleOnly` | All title tokens present — author unconfirmed |
| `AuthorFallback` | Author confirmed, no title signal |
| `KeywordFallback` | AI-recognized or keyword-only match |

### Primary author vs contributor distinction

Open Library's `/works/{id}.json` `authors` array includes a `role` field per entry. A `null`/empty role indicates a primary author; a non-null role (`"Illustrator"`, `"Adaptor"`, etc.) indicates a contributor. This is used for both tier classification and is surfaced in result cards and AI explanations (e.g. *"Tolkien is primary author; Dixon listed as adaptor"*).

### AI integration (Gemini)

Two Gemini calls are made per search:

1. **Field extraction** (`responseMimeType: "application/json"`, `temperature: 0.1`) — parses the user's query into `{ title?, author?, keywords[], suggestions[] }`. Structured JSON mode avoids markdown-fence stripping. The `title` and `author` fields are populated from explicit query tokens **or** from Gemini's own recognition of a quote, plot description, or character name — whichever applies. `suggestions[]` is reserved for cases where the query is ambiguous enough to plausibly match multiple books.
2. **Per-candidate explanation** (plain text, parallel) — generates a 1–2 sentence "why it matched" rationale grounded in the actual Open Library fields retrieved, displayed inline on each result card.

Both prompts are loaded from **Markdown template files** (`Prompts/extraction.md`, `Prompts/explanation.md`) at startup via `IPromptProvider` / `FilePromptProvider`. To try a different prompt, duplicate the `.md` file, edit it, and point `GeminiOptions.ExtractionPromptFile` at the new filename — no code change required.

The `suggestions[]` list enables AI-knowledge-based matching: Gemini can recognise *"that book where someone bets they can circle the globe in 80 days"* as *Around the World in Eighty Days* from training data even when no title or author tokens are parseable. Each suggestion must include a `reason` field.

### Direct REST API usage (no SDKs)

Both external dependencies — Open Library and Gemini — are accessed via raw `HttpClient` calls rather than SDK packages. This was a deliberate choice.

**Open Library** has no official .NET SDK. OL's own client library is Python-only. A third-party .NET wrapper, [OpenLibrary.NET](https://github.com/Luca3317/OpenLibrary.NET) (`dotnet add package OpenLibrary.NET`), does exist and covers the APIs used here (Search, Works, Authors, Covers). It was considered but not adopted for the following reasons:

- It is a community project with no releases published, 22 stars, and open issues — not a stability baseline suitable for a dependency.
- This application uses a narrow, well-defined slice of the OL API (four endpoints, specific field projections). A bespoke `IOpenLibraryService` with hand-written DTOs is no more code and gives precise control over what is deserialized and how errors are handled.
- Wrapping `HttpClient` directly keeps the `IOpenLibraryService` interface mockable with the standard `HttpMessageHandler` pattern — no additional test-double infrastructure required.

**Gemini** is accessed via the `generateContent` REST endpoint directly. Google publishes `Google.AI.Generative` NuGet packages, but the two calls made here — structured JSON extraction and plain-text explanation generation — map to straightforward `POST` requests whose shape is stable and fully documented. The `GeminiRequest` / `GeminiResponse` DTOs in `Models/Gemini/` own that contract explicitly, making the integration auditable without chasing through SDK source code.

The broader trade-offs of raw REST vs SDK apply to both:

| Concern | SDK | Raw REST |
|---|---|---|
| Dependency footprint | Adds a versioned package per service | Zero extra dependencies |
| Transparency | Abstracts auth, serialization, retry | Every request is explicit and inspectable |
| Testability | Requires mocking the SDK client | `HttpClient` mocked via `HttpMessageHandler` — standard .NET pattern |
| Portability | Ties code to a vendor's release cadence | Immune to SDK breaking changes or deprecations |
| Learning signal | Hides what the API actually does | Demonstrates knowledge of the underlying HTTP contract |

The trade-off is that retry logic, streaming, and future API version upgrades require manual handling rather than a library update — acknowledged in [Future Improvements](#future-improvements).

### Graceful degradation

If Gemini is unavailable, rate-limited, or misconfigured:

- `ExtractFieldsAsync` returns an empty `ExtractedFields`.
- The fallback copies `query.Title` / `query.Author` directly into the extracted fields with `FieldSource.UserInput`, so the full user-input weights are applied in scoring.
- `query.FreeText` tokens become fallback keywords.
- Open Library search continues independently — a Gemini failure never silently produces zero results.

---

## Local Setup & Running

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- A [Gemini API key](https://ai.google.dev/gemini-api/docs/api-key) — free tier, no billing required

### 1. Clone

```bash
git clone https://github.com/ezkhan/FindThatBook.git
cd FindThatBook
```

### 2. Set the Gemini API key

There are two ways to provide the key — choose whichever suits your workflow:

**Option A — `appsettings.json` directly (quickest for a one-off run)**

Open `FindThatBook/appsettings.json` and fill in the blank value:

```json
"Gemini": {
  "ApiKey": "YOUR_API_KEY_HERE"
}
```

> ⚠️ Do not commit the file with a real key in it. This option is fine for a quick local test but is not recommended if you intend to make and commit code changes.

**Option B — User secrets (recommended if you are working on and committing code)**

.NET user secrets stores the key outside the repository so it can never be accidentally committed. If this is your first time using user secrets with this project, initialise the store first:

```bash
cd FindThatBook
dotnet user-secrets init
```

Then set the key:

```bash
dotnet user-secrets set "Gemini:ApiKey" "YOUR_API_KEY_HERE"
```

This stores the key in your local user secrets store and is never committed to source control. The `appsettings.json` `Gemini:ApiKey` field is intentionally left blank and safe to commit as-is.

> The default model is `gemini-2.5-flash`. To change it via user secrets:
> ```bash
> dotnet user-secrets set "Gemini:Model" "gemini-2.0-flash-lite"
> ```
> Or set `Gemini:Model` directly in `appsettings.json` — the model name is not case-sensitive.

### 3. Run the web app

```bash
dotnet run --project FindThatBook
```

Open `https://localhost:7117` (or the URL shown in the terminal).

### 4. Run the sandbox (optional)

The sandbox is a console app to serve as a manual test harness that runs hardcoded queries against the full service stack without the browser or API layer.
 This was the playground for development and remains available for manual/interactive experimentation, but all core logic is covered by unit tests in `FindThatBook.Tests`.
 It is not necessary to run, but if so desired:

```bash
dotnet run --project FindThatBookSandbox
```

It shares the same user secrets store as the web app — no extra key setup needed. Edit `FindThatBookSandbox/Program.cs` to add your own test cases.

---

## Project Structure

```
FindThatBook/
├── Controllers/
│   ├── HomeController.cs              # Serves Privacy + Error MVC views
│   └── Api/SearchController.cs        # POST /api/search -> JSON
├── Models/
│   ├── SearchQuery.cs                 # Input: Title?, Author?, FreeText?
│   ├── ExtractedFields.cs             # AI output + FieldSource (UserInput vs AiExtracted)
│   ├── BookCandidate.cs               # One result + MatchTier + MatchScore + Explanation
│   ├── SearchResult.cs                # Response wrapper
│   ├── OpenLibrary/                   # OL API response DTOs
│   └── Gemini/                        # Gemini API request/response DTOs
├── Prompts/
│   ├── extraction.md                  # Extraction prompt template ({{USER_INPUT}} placeholder)
│   └── explanation.md                 # Explanation prompt template (named placeholders)
├── Services/
│   ├── TextNormalizer.cs              # Normalize(), IsNearMatch(), TokenMatchRatio()
│   ├── IStringSimilarity.cs           # Strategy interface: Similarity(a,b) -> [0.0, 1.0]
│   ├── LevenshteinSimilarity.cs       # Default: 1 - editDist / max(|a|,|b|)
│   ├── JaroWinklerSimilarity.cs       # Alternative: Jaro + common-prefix boost
│   ├── IPromptProvider.cs             # Interface: ExtractionTemplate, ExplanationTemplate
│   ├── FilePromptProvider.cs          # Loads .md prompts from Prompts/ at startup
│   ├── GeminiOptions.cs               # Config: ApiKey, Model, ExtractionPromptFile, ExplanationPromptFile
│   ├── IOpenLibraryService.cs / OpenLibraryService.cs
│   ├── IGeminiService.cs / GeminiService.cs
│   └── IBookSearchService.cs / BookSearchService.cs
└── Pages/
    ├── Index.cshtml                   # Search form + result cards (tier badge, score chip, Gemini explanation)
    └── Index.cshtml.cs                # PageModel with [BindProperty] SearchQuery

FindThatBookSandbox/
└── Program.cs                         # Console app as sandbox for manual testing against live APIs

FindThatBook.Tests/                    # xUnit + Moq unit test project
├── TextNormalizerTests.cs
├── OpenLibraryServiceTests.cs
├── GeminiServiceTests.cs
├── BookSearchServiceTests.cs
├── StringSimilarityTests.cs
└── Helpers/
    └── TestHttpMessageHandler.cs
```

---

## API Reference

### `POST /api/search`

**Request** (`application/json`) — at least one field must be non-empty:

```json
{
  "title": "tale two cities",
  "author": "dickens",
  "freeText": "optional additional free-text context"
}
```

**Response** (`application/json`):

```json
{
  "originalQuery": "title: tale two cities | author: dickens",
  "extractedFields": {
    "title": "A Tale of Two Cities",
    "titleSource": 0,
    "author": "Charles Dickens",
    "authorSource": 0,
    "keywords": [],
    "suggestions": []
  },
  "candidates": [
    {
      "title": "A Tale of Two Cities",
      "primaryAuthors": ["Charles Dickens"],
      "contributors": [],
      "firstPublishYear": 1859,
      "workId": "/works/OL118421W",
      "openLibraryUrl": "https://openlibrary.org/works/OL118421W",
      "coverUrl": "https://covers.openlibrary.org/b/id/8739161-M.jpg",
      "explanation": "Exact title and author match — Charles Dickens is confirmed as the primary author.",
      "matchTier": 1,
      "matchScore": 1.20
    }
  ],
  "errorMessage": null
}
```

`titleSource` / `authorSource`: `0` = `UserInput` (from explicit field), `1` = `AiExtracted` (inferred from free-text).

---

## Testing Strategy

The `FindThatBook.Tests` project (xUnit + Moq, 98 tests) covers all service layers without hitting any external API.

| Suite | What is covered |
|---|---|
| `TextNormalizerTests` | `Normalize`, `IsNearMatch`, `TokenMatchRatio` — null/whitespace, diacritics, case, partial token ratios |
| `OpenLibraryServiceTests` | All 5 interface methods: URL/parameter construction, deserialization, HTTP error resilience, cover URL format |
| `GeminiServiceTests` | `ExtractFieldsAsync` / `GenerateExplanationAsync` — success path, invalid JSON, HTTP failure, `FieldSource` stamping for explicit vs free-text input |
| `BookSearchServiceTests` | Happy path, Gemini fallback, deduplication by WorkId, `FieldSource` scoring discount, mismatched title/author regression, error propagation, candidate field population |
| `StringSimilarityTests` | `LevenshteinSimilarity` and `JaroWinklerSimilarity` — edge cases (empty, identical), exact numeric values, symmetry, ordering invariants, unit-range guarantee |

Run all tests:

```bash
dotnet test FindThatBook.Tests
```



---

## Demo Deployment

> _Live Demo URL_: https://findthatbook20260312073741-agb8b0gsauejghew.canadacentral-01.azurewebsites.net/

Planned target: **Azure App Service (Free F1 tier)**

Steps:
1. Right-click `FindThatBook` in Visual Studio → **Publish → Azure → Azure App Service (Windows)**
2. Create a new App Service — Free F1 tier is sufficient for a demo
3. In Azure Portal → App Service → **Configuration → Application Settings**, add:
   ```
   Gemini__ApiKey                =  <your key>
   Gemini__Model                 =  gemini-2.5-flash
   Gemini__ExtractionPromptFile  =  extraction.md
   Gemini__ExplanationPromptFile =  explanation.md
   OpenLibrary__UserAgent        =  FindThatBook (<your contact email>)
   ```
   (Double underscore maps to `Gemini:ApiKey` etc. in .NET configuration hierarchy)
4. Publish

---

## Future Improvements

### Search quality
- **Ingest Open Library data dumps** — OL publishes full data exports (~20 GB). Parsing titles and authors into a local inverted index (e.g. Elasticsearch + BM25) would eliminate rate-limit constraints and remove multi-hop API latency. Levenshtein similarity is already applied locally; a proper index would be the step-change improvement for recall and precision.
- **Gemini re-ranking pass** — after collecting candidates, send the full ranked list back to Gemini in a single call and ask it to re-order with chain-of-thought rationale. Currently explanations are generated per candidate but ordering is determined purely by the scoring formula.
- **Subtitle normalization** — *The Hobbit* vs *There and Back Again* disambiguation. Token matching partially handles this but a dedicated subtitle-strip step would improve precision.
- **ISBN / edition lookup** — a user-supplied ISBN could bypass fuzzy matching entirely via the OL Books API (`/isbn/{isbn}.json`).

### Reliability, Performance, and Scalability
- **Resilience policies (Polly)** — Open Library occasionally returns 503. Adding retry + exponential backoff via `Microsoft.Extensions.Http.Resilience` (built into .NET 8) would make the service more robust without much code.
- **Author lookup caching** — `/authors/{id}.json` calls repeat across searches. A short-lived `IMemoryCache` keyed on author ID would significantly reduce Open Library call volume and latency.
- **Gemini streaming** — `streamGenerateContent` would allow explanation text to appear progressively rather than waiting for all 5 explanations to complete before the page renders.
- **Background indexing** — if ingesting OL data dumps, the web app could trigger a background indexing process on startup or via an admin endpoint, with health checks to indicate when the index is ready.
- **Caching layer** — a more robust caching layer (e.g. Redis) could store popular queries and their results, or even the full OL API responses, to speed up common searches and reduce load on both Open Library and Gemini.
- **Rate limiting and circuit breaking** — to prevent cascading failures if Open Library or Gemini become unresponsive, rate limiting and circuit breaker patterns could be implemented using `Microsoft.Extensions.Http.Resilience` policies.
- **N-gram indexing** — for a more performant fuzzy search without a full external search engine, an n-gram index could be built in-memory on startup (especially if we ingest OL data dumps) to quickly narrow down candidate titles/authors before, or candidate works after querying OL.
- **Cache-Control headers** — appropriate caching headers on the API response would allow client-side caching of popular queries, and CDN caching if deployed to a platform that supports it.
- **Pagination** — for queries that return many candidates, the API could be redesigned to return results in pages, with the frontend requesting additional pages as the user scrolls. This would reduce initial latency and allow Gemini explanations to be generated on-demand for visible candidates.
- **Horizontal and vertical scaling** — while the Free F1 tier of Azure App Service is sufficient for a demo, a production deployment would benefit from scaling out to multiple instances and/or upgrading to a more powerful tier to handle increased traffic and reduce latency.

### Observability and monitoring
As a toy/demo application, this project relies only on .NET's built-in `ILogger` writing to the console and the Azure App Service log stream. That is sufficient to diagnose issues during development but falls well short of what a production deployment would need.

In a real-world system we may have additional considerations like:

- **Error tracking** — a service such as [Raygun](https://raygun.com/) would capture unhandled exceptions and degraded searches (e.g. Gemini timeouts, OL 503s) with full stack traces, request context, and alerting. The existing `catch` blocks in `BookSearchService` and the two HTTP services already log structured errors via `ILogger`; routing those to Raygun would require only adding the `Mindscape.Raygun4Net.AspNetCore` package and a one-line middleware registration.
- **Application performance monitoring** — tools like Azure Application Insights or Raygun APM would surface slow OL/Gemini call latencies, fan-out bottlenecks, and per-endpoint throughput without any manual instrumentation beyond registering the SDK.
- **Database query monitoring** — this application has no data layer, so there is nothing to monitor at the query level today. If a database were introduced (e.g. to cache OL responses or store search history), a tool like [SolarWinds Database Performance Analyzer (DPA)](https://www.solarwinds.com/database-performance-analyzer) would be the natural choice for identifying slow queries, index gaps, and wait-time analysis across the query lifecycle.

### Developer experience
- **OpenAPI / Swagger** — `Swashbuckle.AspNetCore` would auto-generate interactive API docs for `/api/search`.
- **Docker support** — a `Dockerfile` would enable deployment to Azure Container Apps, Railway, or any container host as an alternative to App Service.
