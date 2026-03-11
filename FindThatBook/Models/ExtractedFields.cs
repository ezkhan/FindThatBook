namespace FindThatBook.Models
{
    /// <summary>
    /// Structured output from the AI service for a given query.
    /// </summary>
    public class ExtractedFields
    {
        /// <summary>Normalized title parsed from the query blob.</summary>
        public string? Title { get; set; }

        /// <summary>Normalized author parsed from the query blob.</summary>
        public string? Author { get; set; }

        public List<string> Keywords { get; set; } = [];

        /// <summary>
        /// Books the AI recognizes from its own training data, beyond what it can
        /// strictly parse from the input tokens. Each carries a reason for the suggestion.
        /// </summary>
        public List<AiBookSuggestion> Suggestions { get; set; } = [];
    }

    public class AiBookSuggestion
    {
        public string Title { get; set; } = string.Empty;
        public string? Author { get; set; }

        /// <summary>
        /// One-sentence rationale grounded in what the AI recognized
        /// (e.g. "Recognized from the '80 days' timeframe and world-travel premise").
        /// </summary>
        public string Reason { get; set; } = string.Empty;
    }
}
