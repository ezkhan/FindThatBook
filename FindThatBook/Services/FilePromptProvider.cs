using Microsoft.Extensions.Options;

namespace FindThatBook.Services
{
    /// <summary>
    /// Loads prompt templates from the Prompts/ folder at startup and caches them.
    /// To try a different prompt, either swap the file on disk or point
    /// GeminiOptions.ExtractionPromptFile / ExplanationPromptFile at a new .md file.
    /// </summary>
    public class FilePromptProvider : IPromptProvider
    {
        public string ExtractionTemplate { get; }
        public string ExplanationTemplate { get; }

        public FilePromptProvider(IWebHostEnvironment env, IOptions<GeminiOptions> options)
        {
            var opts = options.Value;
            var promptsDir = Path.Combine(env.ContentRootPath, "Prompts");
            ExtractionTemplate = File.ReadAllText(Path.Combine(promptsDir, opts.ExtractionPromptFile));
            ExplanationTemplate = File.ReadAllText(Path.Combine(promptsDir, opts.ExplanationPromptFile));
        }
    }
}
