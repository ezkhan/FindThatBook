using FindThatBook.Models;

namespace FindThatBook.Services
{
    public interface IGeminiService
    {
        /// <summary>
        /// Parses the user query into structured fields and returns any books
        /// Gemini recognizes from its own training data.
        /// </summary>
        Task<ExtractedFields> ExtractFieldsAsync(SearchQuery query, CancellationToken ct = default);

        /// <summary>
        /// Generates a 1-2 sentence "why it matched" explanation for a candidate,
        /// grounded in the actual Open Library fields that were retrieved.
        /// </summary>
        Task<string> GenerateExplanationAsync(BookCandidate candidate, string originalQuery, CancellationToken ct = default);
    }
}
