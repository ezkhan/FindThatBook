namespace FindThatBook.Models
{
    public class SearchResult
    {
        public string OriginalQuery { get; set; } = string.Empty;
        public ExtractedFields ExtractedFields { get; set; } = new();
        public List<BookCandidate> Candidates { get; set; } = [];
        public string? ErrorMessage { get; set; }
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    }
}
