namespace FindThatBook.Models
{
    /// <summary>
    /// Indicates where a title or author value in <see cref="ExtractedFields"/> originated.
    /// Used by scoring to weight explicit user input higher than AI-inferred values.
    /// </summary>
    public enum FieldSource
    {
        /// <summary>Value came from the user's explicit Title or Author input field.</summary>
        UserInput,

        /// <summary>Value was inferred by the AI from free-text or query context.</summary>
        AiExtracted
    }

    /// <summary>
    /// Structured output from the AI service for a given query.
    /// </summary>
    public class ExtractedFields
    {
        /// <summary>Normalized title parsed from the query blob.</summary>
        public string? Title { get; set; }

        /// <summary>Whether <see cref="Title"/> came from explicit user input or AI extraction.</summary>
        public FieldSource TitleSource { get; set; } = FieldSource.AiExtracted;

        /// <summary>Normalized author parsed from the query blob.</summary>
        public string? Author { get; set; }

        /// <summary>Whether <see cref="Author"/> came from explicit user input or AI extraction.</summary>
        public FieldSource AuthorSource { get; set; } = FieldSource.AiExtracted;

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
