using System.Text.Json.Serialization;

namespace FindThatBook.Models.OpenLibrary
{
    /// <summary>
    /// Top-level response from GET /search.json
    /// </summary>
    public class OlSearchResponse
    {
        [JsonPropertyName("numFound")]
        public int NumFound { get; set; }

        [JsonPropertyName("docs")]
        public List<OlSearchDoc> Docs { get; set; } = [];
    }

    public class OlSearchDoc
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;          // e.g. /works/OL27448W

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("author_name")]
        public List<string>? AuthorName { get; set; }

        [JsonPropertyName("author_key")]
        public List<string>? AuthorKey { get; set; }             // e.g. ["/authors/OL26320A"]

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("cover_i")]
        public int? CoverId { get; set; }

        [JsonPropertyName("subject")]
        public List<string>? Subjects { get; set; }
    }
}
