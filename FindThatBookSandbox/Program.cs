using FindThatBook.Models;
using FindThatBook.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddHttpClient<IOpenLibraryService, OpenLibraryService>(client =>
        {
            client.BaseAddress = new Uri("https://openlibrary.org/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.Configure<GeminiOptions>(ctx.Configuration.GetSection(GeminiOptions.SectionName));

        services.AddHttpClient<IGeminiService, GeminiService>(client =>
        {
            client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<IBookSearchService, BookSearchService>();
    })
    .Build();

var searchService = host.Services.GetRequiredService<IBookSearchService>();

var testCases = new[]
{
    new SearchQuery { Title = "tale two cities",   Author = "dickens" },
    new SearchQuery { Title = "The Great Gatsby",  Author = "F. Scott Fitzgerald" },
    new SearchQuery { Author = "tolkien" },
    new SearchQuery { FreeText = "man bets he can travel around the world in 80 days" },
    new SearchQuery { FreeText = "tolkien hobbit illustrated deluxe 1937" },
    new SearchQuery { FreeText = "austen bennet" },
};

foreach (var query in testCases)
{
    await RunTestAsync(searchService, query);
    Console.WriteLine();
}

static async Task RunTestAsync(IBookSearchService service, SearchQuery query)
{
    var label = string.Join(" | ", new[]
    {
        query.Title  is { } t ? $"title: \"{t}\"" : null,
        query.Author is { } a ? $"author: \"{a}\"" : null,
        query.FreeText is { } f ? $"free: \"{f}\"" : null,
    }.Where(s => s != null));

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"┌─ {label}");
    Console.ResetColor();

    var result = await service.SearchAsync(query);

    if (result.HasError)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"│  ERROR: {result.ErrorMessage}");
        Console.ResetColor();
        return;
    }

    var ef = result.ExtractedFields;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"│  AI extracted → title: {ef.Title ?? "-"}  author: {ef.Author ?? "-"}  keywords: [{string.Join(", ", ef.Keywords)}]");
    if (ef.Suggestions.Count > 0)
        Console.WriteLine($"│  AI suggestions → {string.Join(", ", ef.Suggestions.Select(s => $"\"{s.Title}\""))}");
    Console.ResetColor();

    if (result.Candidates.Count == 0)
    {
        Console.WriteLine("│  (no candidates)");
        return;
    }

    foreach (var (c, i) in result.Candidates.Select((c, i) => (c, i + 1)))
    {
        var authors = c.PrimaryAuthors.Count > 0 ? string.Join(", ", c.PrimaryAuthors) : "?";
        var year    = c.FirstPublishYear.HasValue ? $" ({c.FirstPublishYear})" : string.Empty;

        Console.ForegroundColor = GetTierColour(c.MatchTier);
        Console.Write($"│  {i}. [{c.MatchTier,-28}  {c.MatchScore:F2}]  ");
        Console.ResetColor();
        Console.WriteLine($"{c.Title} — {authors}{year}");

        if (!string.IsNullOrWhiteSpace(c.Explanation))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"│      └ {c.Explanation}");
            Console.ResetColor();
        }
    }
}

static ConsoleColor GetTierColour(FindThatBook.Models.MatchTier tier) => tier switch
{
    FindThatBook.Models.MatchTier.ExactTitlePrimaryAuthor     => ConsoleColor.Green,
    FindThatBook.Models.MatchTier.ExactTitleContributorAuthor => ConsoleColor.DarkCyan,
    FindThatBook.Models.MatchTier.ExactTitleOnly              => ConsoleColor.Green,
    FindThatBook.Models.MatchTier.NearMatchTitleAuthor        => ConsoleColor.Blue,
    FindThatBook.Models.MatchTier.NearMatchTitleOnly          => ConsoleColor.Blue,
    FindThatBook.Models.MatchTier.AuthorFallback              => ConsoleColor.Yellow,
    _                                                         => ConsoleColor.DarkGray,
};
