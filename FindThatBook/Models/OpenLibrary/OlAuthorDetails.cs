using System.Text.Json.Serialization;

namespace FindThatBook.Models.OpenLibrary
{
    /// <summary>
    /// Response from GET /authors/{author_id}.json
    /// </summary>
    public class OlAuthorDetails
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("personal_name")]
        public string? PersonalName { get; set; }

        [JsonPropertyName("birth_date")]
        public string? BirthDate { get; set; }
    }
}
