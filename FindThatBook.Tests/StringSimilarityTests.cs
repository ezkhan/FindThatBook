using FindThatBook.Services;

namespace FindThatBook.Tests
{
    public class LevenshteinSimilarityTests
    {
        private readonly IStringSimilarity _sut = new LevenshteinSimilarity();

        // ------------------------------------------------------------------
        // Edge cases
        // ------------------------------------------------------------------

        [Fact]
        public void Similarity_ReturnsOne_ForIdenticalStrings()
            => Assert.Equal(1.0, _sut.Similarity("hobbit", "hobbit"));

        [Fact]
        public void Similarity_ReturnsOne_ForBothEmpty()
            => Assert.Equal(1.0, _sut.Similarity("", ""));

        [Theory]
        [InlineData("abc", "")]
        [InlineData("", "abc")]
        public void Similarity_ReturnsZero_WhenOneInputIsEmpty(string a, string b)
            => Assert.Equal(0.0, _sut.Similarity(a, b));

        // ------------------------------------------------------------------
        // Exact numeric values  (score = 1 − distance / max(|a|, |b|))
        // ------------------------------------------------------------------

        [Fact]
        public void Similarity_OneSingleSubstitution_ScoresCorrectly()
        {
            // "cat" → "bat": distance 1, max len 3  →  1 − 1/3 ≈ 0.667
            var score = _sut.Similarity("cat", "bat");
            Assert.Equal(1.0 - 1.0 / 3, score, precision: 10);
        }

        [Fact]
        public void Similarity_OneDeletion_ScoresCorrectly()
        {
            // "abcd" → "abc": distance 1, max len 4  →  1 − 1/4 = 0.75
            var score = _sut.Similarity("abcd", "abc");
            Assert.Equal(0.75, score, precision: 10);
        }

        [Fact]
        public void Similarity_CompletelyDifferentSameLength_ScoresZero()
        {
            // "abc" → "xyz": distance 3, max len 3  →  1 − 3/3 = 0.0
            Assert.Equal(0.0, _sut.Similarity("abc", "xyz"));
        }

        [Fact]
        public void Similarity_Tolkien_Misspelled_ScoresAboveHalf()
        {
            // "tolkien" vs "tolkein": two substitutions (i↔e), distance 2, max len 7  →  ≈ 0.714
            var score = _sut.Similarity("tolkien", "tolkein");
            Assert.True(score > 0.5 && score < 1.0,
                $"Expected score in (0.5, 1.0) but got {score:F4}");
        }

        // ------------------------------------------------------------------
        // Symmetry and ordering invariants
        // ------------------------------------------------------------------

        [Fact]
        public void Similarity_IsSymmetric()
            => Assert.Equal(_sut.Similarity("hobbit", "rabbit"), _sut.Similarity("rabbit", "hobbit"));

        [Fact]
        public void Similarity_ExactMatchScoresHigherThanPartialMatch()
            => Assert.True(_sut.Similarity("tolkien", "tolkien") > _sut.Similarity("tolkien", "tolkein"));

        [Fact]
        public void Similarity_CloserStringsScoreHigherThanMoreDistantOnes()
        {
            double closeScore = _sut.Similarity("tolkien", "tolkein");   // 1 edit away (approx)
            double farScore   = _sut.Similarity("tolkien", "verne");     // very different
            Assert.True(closeScore > farScore,
                $"Expected close ({closeScore:F4}) > far ({farScore:F4})");
        }

        // ------------------------------------------------------------------
        // Result is always in [0.0, 1.0]
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("the hobbit", "there and back again")]
        [InlineData("j r r tolkien", "tolkien")]
        [InlineData("hamlet", "verne")]
        [InlineData("a", "z")]
        public void Similarity_IsAlwaysInUnitRange(string a, string b)
        {
            var score = _sut.Similarity(a, b);
            Assert.True(score >= 0.0 && score <= 1.0,
                $"Similarity(\"{a}\", \"{b}\") = {score:F4} is outside [0,1]");
        }
    }

    public class JaroWinklerSimilarityTests
    {
        private readonly IStringSimilarity _sut = new JaroWinklerSimilarity();

        // ------------------------------------------------------------------
        // Edge cases
        // ------------------------------------------------------------------

        [Fact]
        public void Similarity_ReturnsOne_ForIdenticalStrings()
            => Assert.Equal(1.0, _sut.Similarity("hobbit", "hobbit"));

        [Fact]
        public void Similarity_ReturnsOne_ForBothEmpty()
            => Assert.Equal(1.0, _sut.Similarity("", ""));

        [Theory]
        [InlineData("abc", "")]
        [InlineData("", "abc")]
        public void Similarity_ReturnsZero_WhenOneInputIsEmpty(string a, string b)
            => Assert.Equal(0.0, _sut.Similarity(a, b));

        [Fact]
        public void Similarity_ReturnsZero_ForNoCommonCharactersInWindow()
        {
            // match_distance = max(3,3)/2 − 1 = 0, so characters must be at same index;
            // "abc" and "xyz" share no characters at any matching position → 0.0
            Assert.Equal(0.0, _sut.Similarity("abc", "xyz"));
        }

        // ------------------------------------------------------------------
        // Classic Jaro-Winkler reference pair
        // ------------------------------------------------------------------

        [Fact]
        public void Similarity_Martha_Marhta_MatchesExpectedValue()
        {
            // "martha" / "marhta": all 6 characters match, 2 transpositions.
            // Jaro ≈ 0.9444, prefix length = 3 ("mar") → JW ≈ 0.961
            var score = _sut.Similarity("martha", "marhta");
            Assert.True(score > 0.95 && score <= 1.0,
                $"Expected JW(martha, marhta) > 0.95, got {score:F4}");
        }

        // ------------------------------------------------------------------
        // Prefix-boost: identical prefix should score higher than reversed prefix
        // ------------------------------------------------------------------

        [Fact]
        public void Similarity_SharedPrefixScoresHigher_ThanNoPrefix()
        {
            // "tolkien" vs "tolkein" shares "tolk" — JW boosts this
            // "tolkien" vs "nieklot" shares no prefix
            double withPrefix    = _sut.Similarity("tolkien", "tolkein");
            double withoutPrefix = _sut.Similarity("tolkien", "nieklot");
            Assert.True(withPrefix > withoutPrefix,
                $"Prefix-sharing pair ({withPrefix:F4}) should score above no-prefix pair ({withoutPrefix:F4})");
        }

        // ------------------------------------------------------------------
        // Symmetry and ordering invariants
        // ------------------------------------------------------------------

        [Fact]
        public void Similarity_IsSymmetric()
            => Assert.Equal(_sut.Similarity("hobbit", "rabbit"), _sut.Similarity("rabbit", "hobbit"));

        [Fact]
        public void Similarity_ExactMatchScoresHigherThanPartialMatch()
            => Assert.True(_sut.Similarity("tolkien", "tolkien") > _sut.Similarity("tolkien", "tolkein"));

        [Fact]
        public void Similarity_CloserStringsScoreHigherThanMoreDistantOnes()
        {
            double closeScore = _sut.Similarity("tolkien", "tolkein");
            double farScore   = _sut.Similarity("tolkien", "verne");
            Assert.True(closeScore > farScore,
                $"Expected close ({closeScore:F4}) > far ({farScore:F4})");
        }

        // ------------------------------------------------------------------
        // Result is always in [0.0, 1.0]
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("the hobbit", "there and back again")]
        [InlineData("j r r tolkien", "tolkien")]
        [InlineData("hamlet", "verne")]
        [InlineData("a", "z")]
        public void Similarity_IsAlwaysInUnitRange(string a, string b)
        {
            var score = _sut.Similarity(a, b);
            Assert.True(score >= 0.0 && score <= 1.0,
                $"Similarity(\"{a}\", \"{b}\") = {score:F4} is outside [0,1]");
        }
    }
}
