using FindThatBook.Models;
using FindThatBook.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindThatBook.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IBookSearchService _bookSearch;

        public IndexModel(IBookSearchService bookSearch)
        {
            _bookSearch = bookSearch;
        }

        [BindProperty]
        public SearchQuery Query { get; set; } = new();

        public SearchResult? Result { get; set; }

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync(CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return Page();

            if (string.IsNullOrWhiteSpace(Query.FreeText)
                && string.IsNullOrWhiteSpace(Query.Title)
                && string.IsNullOrWhiteSpace(Query.Author))
            {
                ModelState.AddModelError(string.Empty,
                    "Please enter at least one of: a title, an author, free-text description.");
                return Page();
            }

            Result = await _bookSearch.SearchAsync(Query, ct);
            return Page();
        }
    }
}
