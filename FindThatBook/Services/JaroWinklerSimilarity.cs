namespace FindThatBook.Services
{
    /// <summary>
    /// String similarity using the Jaro-Winkler algorithm.
    /// Jaro measures character overlap within a sliding match window; the Winkler extension
    /// boosts scores for strings sharing a common prefix (up to 4 characters), making it
    /// well-suited for proper names and short titles where prefixes carry more signal.
    /// Returns a score in [0.0, 1.0].
    /// </summary>
    public sealed class JaroWinklerSimilarity : IStringSimilarity
    {
        /// <summary>Prefix scaling factor — standard value recommended by Winkler.</summary>
        private const double PrefixScale = 0.1;

        public double Similarity(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0.0;

            double jaro = ComputeJaro(a, b);

            // Common-prefix boost (up to 4 characters)
            int maxPrefix = Math.Min(4, Math.Min(a.Length, b.Length));
            int prefixLen = 0;
            while (prefixLen < maxPrefix && a[prefixLen] == b[prefixLen])
                prefixLen++;

            return jaro + prefixLen * PrefixScale * (1.0 - jaro);
        }

        private static double ComputeJaro(string a, string b)
        {
            int lenA = a.Length, lenB = b.Length;
            int matchDistance = Math.Max(lenA, lenB) / 2 - 1;
            if (matchDistance < 0) matchDistance = 0;

            var matchedA = new bool[lenA];
            var matchedB = new bool[lenB];
            int matches = 0;

            for (int i = 0; i < lenA; i++)
            {
                int start = Math.Max(0, i - matchDistance);
                int end   = Math.Min(lenB - 1, i + matchDistance);
                for (int j = start; j <= end; j++)
                {
                    if (matchedB[j] || a[i] != b[j]) continue;
                    matchedA[i] = true;
                    matchedB[j] = true;
                    matches++;
                    break;
                }
            }

            if (matches == 0) return 0.0;

            // Count transpositions (pairs of matched characters in wrong order)
            int transpositions = 0;
            int k = 0;
            for (int i = 0; i < lenA; i++)
            {
                if (!matchedA[i]) continue;
                while (!matchedB[k]) k++;
                if (a[i] != b[k]) transpositions++;
                k++;
            }

            return ((double)matches / lenA
                  + (double)matches / lenB
                  + (double)(matches - transpositions / 2) / matches) / 3.0;
        }
    }
}
