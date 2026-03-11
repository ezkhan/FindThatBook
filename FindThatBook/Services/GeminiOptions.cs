namespace FindThatBook.Services
{
    public class GeminiOptions
    {
        public const string SectionName = "Gemini";

        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gemini model name. Defaults to gemini-2.5-flash (free tier).
        /// </summary>
        public string Model { get; set; } = "gemini-2.5-flash";
    }
}
