using FindThatBook.Services;
using Microsoft.Extensions.Options;

namespace FindThatBook
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllersWithViews();
            builder.Services.AddRazorPages();

            builder.Services.Configure<GeminiOptions>(
                builder.Configuration.GetSection(GeminiOptions.SectionName));

            builder.Services.Configure<OpenLibraryOptions>(
                builder.Configuration.GetSection(OpenLibraryOptions.SectionName));

            builder.Services.AddHttpClient<IOpenLibraryService, OpenLibraryService>((sp, client) =>
            {
                var olOptions = sp.GetRequiredService<IOptions<OpenLibraryOptions>>().Value;
                client.BaseAddress = new Uri("https://openlibrary.org/");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                // Specifying a User-Agent grants 3× OL rate limit (3 req/s vs 1 req/s).
                // Override via OpenLibrary__UserAgent in Azure App Service Application Settings.
                client.DefaultRequestHeaders.Add("User-Agent", olOptions.UserAgent);
                client.Timeout = TimeSpan.FromSeconds(25);
            });

            builder.Services.AddSingleton<IPromptProvider, FilePromptProvider>();
            builder.Services.AddSingleton<IStringSimilarity, LevenshteinSimilarity>();

            builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
            {
                client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
                client.Timeout = TimeSpan.FromSeconds(45);
            });

            builder.Services.AddScoped<IBookSearchService, BookSearchService>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthorization();

            app.MapRazorPages();
            app.MapControllers(); // API controllers (e.g. /api/search)
            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            app.Run();
        }
    }
}
