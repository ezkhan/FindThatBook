namespace FindThatBook.Services
{
    public class OpenLibraryOptions
    {
        public const string SectionName = "OpenLibrary";

        private string _userAgent = DefaultUserAgent;

        internal const string DefaultUserAgent = "FindThatBook (https://github.com/ezkhan/FindThatBook)";

        /// <summary>
        /// Value sent as the User-Agent request header on every OL API call.
        /// OL's rate-limit policy grants identified apps 3 req/s vs 1 req/s for anonymous.
        /// Format: "AppName (contact@example.org)" or "AppName (https://github.com/…)".
        /// Set via OpenLibrary__UserAgent in Azure App Service Application Settings.
        /// Blank values are ignored; the built-in default is used instead.
        /// </summary>
        public string UserAgent
        {
            get => _userAgent;
            set => _userAgent = string.IsNullOrWhiteSpace(value) ? DefaultUserAgent : value;
        }
    }
}
