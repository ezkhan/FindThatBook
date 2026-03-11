namespace FindThatBook.Services
{
    /// <summary>
    /// String similarity based on normalised Levenshtein (edit) distance.
    /// Similarity = 1 − (editDistance / max(|a|, |b|)), giving a score in [0.0, 1.0]
    /// that scales inversely proportional to the number of single-character edits required.
    /// Uses a space-optimised two-row DP implementation: O(min(|a|,|b|)) memory.
    /// </summary>
    public sealed class LevenshteinSimilarity : IStringSimilarity
    {
        public double Similarity(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0.0;

            int distance = ComputeDistance(a, b);
            return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
        }

        private static int ComputeDistance(string a, string b)
        {
            // Ensure b is the shorter string to minimise allocation
            if (a.Length < b.Length) (a, b) = (b, a);

            int m = a.Length, n = b.Length;
            var prev = new int[n + 1];
            var curr = new int[n + 1];

            for (int j = 0; j <= n; j++) prev[j] = j;

            for (int i = 1; i <= m; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= n; j++)
                {
                    curr[j] = a[i - 1] == b[j - 1]
                        ? prev[j - 1]
                        : 1 + Math.Min(prev[j - 1], Math.Min(prev[j], curr[j - 1]));
                }
                (prev, curr) = (curr, prev);
            }

            return prev[n];
        }
    }
}
