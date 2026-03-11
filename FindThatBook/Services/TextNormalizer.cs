using System.Globalization;
using System.Text;

namespace FindThatBook.Services
{
    public static class TextNormalizer
    {
        /// <summary>
        /// Lowercases input, strips diacritics, and replaces all non-alphanumeric
        /// characters with spaces, then collapses whitespace.
        /// e.g. "Héllo, Wörld!" -> "hello world"
        /// </summary>
        public static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            // Decompose Unicode characters so diacritics become separate combining marks
            var decomposed = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);

            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                    continue; // Strip diacritic combining marks

                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }

            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// Returns true when every normalized token in <paramref name="query"/>
        /// appears somewhere in the normalized <paramref name="candidate"/>.
        /// </summary>
        public static bool IsNearMatch(string query, string candidate)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
                return false;

            var queryTokens = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var normalizedCandidate = Normalize(candidate);
            return queryTokens.All(token => normalizedCandidate.Contains(token));
        }

        /// <summary>
        /// Returns the ratio [0.0–1.0] of normalized query tokens found in the normalized candidate.
        /// e.g. "tolkien hobbit" vs "j r r tolkien" → 0.5 (one of two tokens matched).
        /// </summary>
        public static double TokenMatchRatio(string query, string candidate)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
                return 0.0;

            var queryTokens = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (queryTokens.Length == 0) return 0.0;

            var normalizedCandidate = Normalize(candidate);
            var matched = queryTokens.Count(t => normalizedCandidate.Contains(t));
            return (double)matched / queryTokens.Length;
        }
    }
}
