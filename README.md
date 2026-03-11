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
| Vague / AI-only | `that book where someone bets they can circle the globe in 80 days` |

The system handles sparse, noisy, and ambiguous input — including cases where the provided title and author contradict each other — and returns up to 5 ranked candidates, each with a one-sentence explanation grounded in the actual data retrieved.

---

## Architecture

```
???????????????????????????????????????????????????????
?                  Browser / Client                   ?
???????????????????????????????????????????????????????
                     ?  POST /  (Razor Page form)
                     ?  POST /api/search  (JSON API)
???????????????????????????????????????????????????????
?              ASP.NET Core (.NET 8)                  ?
?                                                     ?
?  Pages/Index  ???????????                           ?
?  Controllers/Api/Search ??                           ?
?                          ?                           ?
?              IBookSearchService                     ?
?           (orchestration layer)                     ?
?                 /          \                        ?
?   IGeminiService         IOpenLibraryService        ?
?   (Google Gemini)        (openlibrary.org)          ?
???????????????????????????????????????????????????????
```

### Request flow

```
1. User submits query (Title? + Author? + FreeText?)
        ?
2. GeminiService.ExtractFieldsAsync()
   ?? Returns: { title?, author?, keywords[], suggestions[] }
   ?? Fallback: raw query inputs used if Gemini fails or returns empty
        ?
3. CollectSearchResultsAsync()
   ?? OL /search.json  (title + author + keywords)  ?? parallel
   ?? OL /search.json  × N AI suggestions            ??
        ?
4. BuildCandidatesAsync()
   ?? QuickDetermineTier() — pre-filter, no I/O
   ?? OL /works/{id}.json  × top 5               ?? parallel
   ?? OL /authors/{id}.json × resolved authors   ?? parallel
        ?
5. AuthorWorksFallbackAsync()   (if no title matches found)
   ?? OL /authors/{id}/works.json
        ?
6. Score, de-duplicate by WorkId, sort, cap at 5 candidates
        ?
7. GeminiService.GenerateExplanationAsync() × 5  ?? parallel
        ?                                          ??
8. Return SearchResult
```

---

## Design Decisions

### Fuzzy match scoring

Rather than discrete tiers alone, each candidate receives a continuous relevance score used for final ranking:

```
score = (titleScore × 0.7) + (authorScore × 0.5) + (keywordScore × 0.3)
```

- **Title weight (0.7) > author weight (0.5)** ensures a correct-title/wrong-author result always outranks a correct-author/wrong-title result — matching the spec's priority hierarchy.
- Each component score is `[0.0–1.0]`: exact normalized match ? 1.0; token overlap ratio for partial matches.
- Contributor author matches are discounted to 0.6 (vs 1.0 for primary author) since contributors are lower-confidence signals.
- Scores above 1.0 are possible (e.g. exact title + exact primary author = 1.2), which is intentional.

### Match tier (categorical display)

A `MatchTier` enum is retained alongside the score purely for the UI badge:

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

Open Library's `/works/{id}.json` `authors` array includes a `role` field per entry. A `null`/empty role indicates a primary author; a non-null role (`"Illustrator"`, `"Adaptor"`, etc.) indicates a contributor. This is used for both tier classification and surfaced in result cards and AI explanations (e.g. *"Tolkien is primary author; Dixon listed as adaptor"*).

### AI integration (Gemini)

Two Gemini calls are made per search:

1. **Field extraction** (`responseMimeType: "application/json"`, `temperature: 0.1`) — parses the user's query into `{ title?, author?, keywords[], suggestions[] }`. The structured JSON mode avoids markdown fence stripping.
2. **Per-candidate explanation** (plain text, parallel) — generates a 1–2 sentence "why it matched" rationale grounded in the actual Open Library fields retrieved.

The `suggestions[]` list enables AI-knowledge-based matching: Gemini can recognise *"that book where someone bets they can circle the globe in 80 days"* as *Around the World in Eighty Days* from training data even with no parseable title or author tokens. Each suggestion must include a `reason` field.

### Graceful degradation

If Gemini is unavailable, rate-limited, or misconfigured, the service falls back to the user's raw inputs (`query.Title`, `query.Author`, tokenised `FreeText`) so a Gemini failure never silently returns zero results. Open Library search continues independently.

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

```bash
cd FindThatBook
dotnet user-secrets set "Gemini:ApiKey" "YOUR_API_KEY_HERE"
```

This stores the key in your local user secrets store and is never committed to source control. The `appsettings.json` `Gemini:ApiKey` field is intentionally left blank.

> The default model is `gemini-2.0-flash`. To use a different model, also run:
> `dotnet user-secrets set "Gemini:Model" "gemini-2.0-flash-lite"`

### 3. Run the web app

```bash
dotnet run --project FindThatBook
```

Open `https://localhost:7117` (or the URL shown in the terminal).

### 4. Run the sandbox (optional)

The sandbox is a console test harness that runs hardcoded queries against the full service stack without the browser or API layer:

```bash
dotnet run --project FindThatBookSandbox
```

It shares the same user secrets store as the web app — no extra key setup needed. Edit `FindThatBookSandbox/Program.cs` to add your own test cases.

---

## Project Structure

```
FindThatBook/
??? Controllers/
?   ??? HomeController.cs              # Serves Privacy + Error MVC views
?   ??? Api/SearchController.cs        # POST /api/search ? JSON
??? Models/
?   ??? SearchQuery.cs                 # Input: Title?, Author?, FreeText?
?   ??? ExtractedFields.cs             # AI output: title?, author?, keywords[], suggestions[]
?   ??? BookCandidate.cs               # One result + MatchTier + MatchScore + Explanation
?   ??? SearchResult.cs                # Response wrapper
?   ??? OpenLibrary/                   # OL API response DTOs
?   ??? Gemini/                        # Gemini API request/response DTOs
??? Services/
?   ??? TextNormalizer.cs              # Normalize(), IsNearMatch(), TokenMatchRatio()
?   ??? IOpenLibraryService.cs / OpenLibraryService.cs
?   ??? IGeminiService.cs / GeminiService.cs
?   ??? GeminiOptions.cs               # Config: Gemini:ApiKey, Gemini:Model
?   ??? IBookSearchService.cs / BookSearchService.cs
??? Pages/
    ??? Index.cshtml                   # Search form + result cards
    ??? Index.cshtml.cs                # PageModel with [BindProperty] SearchQuery

FindThatBookSandbox/
??? Program.cs                         # Console test harness
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
    "author": "Charles Dickens",
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

---

## Testing Strategy

The project currently relies on **manual integration testing** via the sandbox console app, which covers the main query types:

- Exact title + primary author
- Exact title + mismatched/wrong author (validates title-priority scoring)
- Author-only queries (validates author fallback path)
- Free-text / vague queries (validates AI suggestion path)
- Noisy multi-token input (validates normalization and near-match scoring)

### Unit test candidates (future `FindThatBook.Tests` project)

| Component | What to test |
|---|---|
| `TextNormalizer` | Diacritic stripping, punctuation removal, `TokenMatchRatio` boundaries |
| `BookSearchService.CalculateScore` | Formula correctness: title-only, author-only, both, partial, contributor discount |
| `BookSearchService.DetermineTier` | All 7 tier branches; primary vs contributor distinction |
| `OpenLibraryService` | Mocked `HttpClient`: search, work detail, author detail, author works responses |
| `GeminiService` | Mocked 200/404/timeout; JSON parse failure returns empty `ExtractedFields` |
| `IndexModel.OnPostAsync` | Empty query validation; result rendered on valid POST |

Mocking `IOpenLibraryService` and `IGeminiService` via interfaces allows `BookSearchService` to be tested entirely without network calls.

---

## Demo Deployment

> _Live URL: to be added after deployment._

Planned target: **Azure App Service (Free F1 tier)**

Steps:
1. Right-click `FindThatBook` in Visual Studio ? **Publish ? Azure ? Azure App Service (Windows)**
2. Create a new App Service — Free F1 tier is sufficient for a demo
3. In Azure Portal ? App Service ? **Configuration ? Application Settings**, add:
   ```
   Gemini__ApiKey   =  <your key>
   Gemini__Model    =  gemini-2.0-flash
   ```
   (Double underscore maps to `Gemini:ApiKey` in .NET configuration hierarchy)
4. Publish

---

## Future Improvements

### Search quality
- **Ingest Open Library data dumps** — OL publishes full data exports (~20 GB). Parsing titles and authors into a local n-gram index (e.g. Elasticsearch) would eliminate rate-limit constraints, remove multi-hop API latency, and enable proper fuzzy-string distance metrics (Levenshtein, BM25). This would be the single highest-impact improvement for a production system.
- **Gemini re-ranking pass** — after collecting candidates, send the full list back to Gemini in a single call and ask it to re-order with chain-of-thought rationale. Currently explanations are generated per candidate but the ordering is determined purely by the scoring formula.
- **Subtitle normalization** — *The Hobbit* vs *There and Back Again* disambiguation. Token matching partially handles this but a dedicated subtitle-strip step would improve precision.
- **ISBN / edition lookup** — a user-supplied ISBN could bypass fuzzy matching entirely via the OL Books API (`/isbn/{isbn}.json`).

### Reliability & performance
- **Resilience policies (Polly)** — Open Library occasionally returns 503. Adding retry + exponential backoff via `Microsoft.Extensions.Http.Resilience` (built into .NET 8) would make the service more robust without much code.
- **Author lookup caching** — `/authors/{id}.json` calls repeat across searches. A short-lived `IMemoryCache` keyed on author ID would significantly reduce Open Library call volume and latency.
- **Gemini streaming** — `streamGenerateContent` would allow explanation text to appear progressively rather than waiting for all 5 to complete before the page renders.

### Developer experience
- **Formal test project** — replace the sandbox with an `xUnit` + `NSubstitute` project. See [Testing Strategy](#testing-strategy) for the full candidate list.
- **OpenAPI / Swagger** — `Swashbuckle.AspNetCore` would auto-generate interactive API docs for `/api/search`.
- **Docker support** — a `Dockerfile` would enable deployment to Azure Container Apps, Railway, or any container host as an alternative to App Service.
