using FindThatBook.Services;

namespace FindThatBook.Tests
{
    public class TextNormalizerTests
    {
        // ------------------------------------------------------------------
        // Normalize
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Normalize_ReturnsEmpty_WhenInputIsNullOrWhitespace(string? input)
        {
            var result = TextNormalizer.Normalize(input!);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void Normalize_LowercasesInput()
        {
            Assert.Equal("hello world", TextNormalizer.Normalize("Hello World"));
        }

        [Fact]
        public void Normalize_StripsDiacritics()
        {
            Assert.Equal("hello world", TextNormalizer.Normalize("Héllo, Wörld!"));
        }

        [Fact]
        public void Normalize_ReplacesNonAlphanumericWithSpaces()
        {
            Assert.Equal("a b c", TextNormalizer.Normalize("a-b.c"));
        }

        [Fact]
        public void Normalize_CollapsesMultipleSpaces()
        {
            Assert.Equal("a b c", TextNormalizer.Normalize("a   b   c"));
        }

        [Fact]
        public void Normalize_PreservesDigits()
        {
            Assert.Equal("book 2", TextNormalizer.Normalize("Book 2"));
        }

        // ------------------------------------------------------------------
        // IsNearMatch
        // ------------------------------------------------------------------

        [Fact]
        public void IsNearMatch_ReturnsTrue_WhenAllQueryTokensFoundInCandidate()
        {
            Assert.True(TextNormalizer.IsNearMatch("tolkien hobbit", "J.R.R. Tolkien - The Hobbit"));
        }

        [Fact]
        public void IsNearMatch_ReturnsFalse_WhenAQueryTokenIsMissing()
        {
            Assert.False(TextNormalizer.IsNearMatch("tolkien dune", "J.R.R. Tolkien - The Hobbit"));
        }

        [Theory]
        [InlineData(null, "some candidate")]
        [InlineData("", "some candidate")]
        [InlineData("   ", "some candidate")]
        [InlineData("query", null)]
        [InlineData("query", "")]
        public void IsNearMatch_ReturnsFalse_WhenEitherInputIsNullOrWhitespace(
            string? query, string? candidate)
        {
            Assert.False(TextNormalizer.IsNearMatch(query!, candidate!));
        }

        [Fact]
        public void IsNearMatch_IsCaseInsensitive()
        {
            Assert.True(TextNormalizer.IsNearMatch("TOLKIEN", "Tolkien"));
        }

        [Fact]
        public void IsNearMatch_IgnoresDiacritics()
        {
            Assert.True(TextNormalizer.IsNearMatch("Hello", "Héllo World"));
        }

        // ------------------------------------------------------------------
        // TokenMatchRatio
        // ------------------------------------------------------------------

        [Fact]
        public void TokenMatchRatio_ReturnsOne_WhenAllTokensMatch()
        {
            var ratio = TextNormalizer.TokenMatchRatio("tolkien hobbit", "j r r tolkien the hobbit");
            Assert.Equal(1.0, ratio);
        }

        [Fact]
        public void TokenMatchRatio_ReturnsHalf_WhenOneOfTwoTokensMatch()
        {
            var ratio = TextNormalizer.TokenMatchRatio("tolkien dune", "j r r tolkien the hobbit");
            Assert.Equal(0.5, ratio);
        }

        [Fact]
        public void TokenMatchRatio_ReturnsZero_WhenNoTokensMatch()
        {
            var ratio = TextNormalizer.TokenMatchRatio("dune", "tolkien hobbit");
            Assert.Equal(0.0, ratio);
        }

        [Theory]
        [InlineData(null, "candidate")]
        [InlineData("", "candidate")]
        [InlineData("query", null)]
        [InlineData("query", "")]
        public void TokenMatchRatio_ReturnsZero_WhenEitherInputIsNullOrWhitespace(
            string? query, string? candidate)
        {
            Assert.Equal(0.0, TextNormalizer.TokenMatchRatio(query!, candidate!));
        }
    }
}
