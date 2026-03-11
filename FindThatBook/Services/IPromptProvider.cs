namespace FindThatBook.Services
{
    public interface IPromptProvider
    {
        string ExtractionTemplate { get; }
        string ExplanationTemplate { get; }
    }
}
