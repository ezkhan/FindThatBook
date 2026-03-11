using FindThatBook.Models;

namespace FindThatBook.Services
{
    public interface IBookSearchService
    {
        Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default);
    }
}
