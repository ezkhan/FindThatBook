using FindThatBook.Models;
using FindThatBook.Services;
using Microsoft.AspNetCore.Mvc;

namespace FindThatBook.Controllers.Api
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class SearchController : ControllerBase
    {
        private readonly IBookSearchService _bookSearch;

        public SearchController(IBookSearchService bookSearch)
        {
            _bookSearch = bookSearch;
        }

        /// <summary>
        /// Search for books matching the given query.
        /// Accepts a free-text blob plus optional structured title and author hints.
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<SearchResult>> Post(
            [FromBody] SearchQuery query,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query.FreeText)
                && string.IsNullOrWhiteSpace(query.Title)
                && string.IsNullOrWhiteSpace(query.Author))
            {
                return BadRequest(new { error = "Provide at least one of: free text, title, or author." });
            }

            var result = await _bookSearch.SearchAsync(query, ct);
            return Ok(result);
        }
    }
}
