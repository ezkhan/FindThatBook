using System.Text.Json.Serialization;

namespace FindThatBook.Models.OpenLibrary
{
    /// <summary>
    /// Response from GET /works/{work_id}.json
    /// </summary>
    public class OlWorkDetails
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("authors")]
        public List<OlWorkAuthorEntry>? Authors { get; set; }

        [JsonPropertyName("first_publish_date")]
        public string? FirstPublishDate { get; set; }

        [JsonPropertyName("covers")]
        public List<int>? Covers { get; set; }

        [JsonPropertyName("subjects")]
        public List<string>? Subjects { get; set; }
    }

    public class OlWorkAuthorEntry
    {
        [JsonPropertyName("author")]
        public OlKeyRef? Author { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }             // null = primary author; "Introduction", "Illustrator", etc.
    }

    public class OlKeyRef
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;   // e.g. /authors/OL26320A
    }
}
