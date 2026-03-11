namespace FindThatBook.Models
{
    public class SearchQuery
    {
        /// <summary>Optional explicit title hint — still treated as potentially messy/incorrect.</summary>
        public string? Title { get; set; }

        /// <summary>Optional explicit author hint — still treated as potentially messy/incorrect.</summary>
        public string? Author { get; set; }

        /// <summary>Free-text unstructured input — the primary search input.</summary>
        public string? FreeText { get; set; }
    }
}
