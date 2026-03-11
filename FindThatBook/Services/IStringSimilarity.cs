namespace FindThatBook.Services
{
    /// <summary>
    /// Computes a string similarity score between two strings.
    /// Implementations are interchangeable via the strategy pattern (e.g. Levenshtein, Jaro-Winkler).
    /// </summary>
    public interface IStringSimilarity
    {
        /// <summary>
        /// Returns a similarity score in [0.0, 1.0], where 1.0 is identical and 0.0 is completely dissimilar.
        /// Callers should pass pre-normalised strings (lower-cased, diacritics stripped) for consistent results.
        /// </summary>
        double Similarity(string a, string b);
    }
}
