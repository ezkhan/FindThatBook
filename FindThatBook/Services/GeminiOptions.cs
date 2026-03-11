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

        /// <summary>
        /// Prompt template file under Prompts/ used for field extraction.
        /// Swap to a different .md file to try an alternative extraction prompt.
        /// </summary>
        public string ExtractionPromptFile { get; set; } = "extraction.md";

        /// <summary>
        /// Prompt template file under Prompts/ used for generating match explanations.
        /// </summary>
        public string ExplanationPromptFile { get; set; } = "explanation.md";
    }
}
