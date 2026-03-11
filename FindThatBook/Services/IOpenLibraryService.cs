using FindThatBook.Models.OpenLibrary;

namespace FindThatBook.Services
{
    public interface IOpenLibraryService
    {
        /// <summary>
        /// Searches Open Library using field-specific parameters.
        /// Keywords are used as a fallback <c>q=</c> query only when title and author are both absent.
        /// </summary>
        Task<OlSearchResponse> SearchAsync(
            string? title,
            string? author,
            IEnumerable<string>? keywords,
            int limit = 10,
            CancellationToken ct = default);

        /// <summary>
        /// Fetches full work details from <c>/works/{id}.json</c>, including author entries with roles.
        /// </summary>
        Task<OlWorkDetails?> GetWorkAsync(string workKey, CancellationToken ct = default);

        /// <summary>
        /// Fetches author details from <c>/authors/{id}.json</c>.
        /// </summary>
        Task<OlAuthorDetails?> GetAuthorAsync(string authorKey, CancellationToken ct = default);

        /// <summary>
        /// Fetches the top works for an author from <c>/authors/{id}/works.json</c>.
        /// </summary>
        Task<OlAuthorWorksResponse> GetAuthorWorksAsync(string authorKey, int limit = 10, CancellationToken ct = default);

        /// <summary>
        /// Builds the URL for a cover image from the Open Library Covers API.
        /// Size: S, M, or L.
        /// </summary>
        string GetCoverUrl(int coverId, string size = "M");
    }
}
