namespace FindThatBook.Models
{
    public class BookCandidate
    {
        public string Title { get; set; } = string.Empty;
        public List<string> PrimaryAuthors { get; set; } = [];
        public List<string> Contributors { get; set; } = [];
        public int? FirstPublishYear { get; set; }
        public string WorkId { get; set; } = string.Empty;
        public string OpenLibraryUrl => string.IsNullOrEmpty(WorkId)
            ? string.Empty
            : $"https://openlibrary.org{WorkId}";
        public string? CoverUrl { get; set; }
        public string Explanation { get; set; } = string.Empty;
        public MatchTier MatchTier { get; set; }

        /// <summary>
        /// Continuous relevance score used for ranking.
        /// Formula: (titleScore × 0.7) + (authorScore × 0.5) + (keywordScore × 0.3).
        /// Higher is better. Title weight ensures a title match always outranks a pure author match.
        /// </summary>
        public double MatchScore { get; set; }
    }

    public enum MatchTier
    {
        ExactTitlePrimaryAuthor     = 1,  // title exact  + primary author match
        ExactTitleContributorAuthor = 2,  // title exact  + contributor author match
        ExactTitleOnly              = 3,  // title exact  — author not queried or not matched
        NearMatchTitleAuthor        = 4,  // title near   + author match
        NearMatchTitleOnly          = 5,  // title near   — author not queried or not matched
        AuthorFallback              = 6,  // author only match
        KeywordFallback             = 7   // AI / keyword only
    }
}
