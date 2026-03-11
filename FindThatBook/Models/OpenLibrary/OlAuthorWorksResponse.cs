using System.Text.Json.Serialization;

namespace FindThatBook.Models.OpenLibrary
{
    /// <summary>
    /// Response from GET /authors/{author_id}/works.json
    /// </summary>
    public class OlAuthorWorksResponse
    {
        [JsonPropertyName("entries")]
        public List<OlAuthorWorkEntry> Entries { get; set; } = [];
    }

    public class OlAuthorWorkEntry
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("covers")]
        public List<int>? Covers { get; set; }

        [JsonPropertyName("first_publish_date")]
        public string? FirstPublishDate { get; set; }
    }
}
