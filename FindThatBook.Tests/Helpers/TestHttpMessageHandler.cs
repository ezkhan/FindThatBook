using System.Net;
using System.Text;

namespace FindThatBook.Tests.Helpers
{
    /// <summary>
    /// A fake HttpMessageHandler that returns a pre-configured response.
    /// </summary>
    internal sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public HttpRequestMessage? LastRequest { get; private set; }

        public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        /// <summary>Returns a handler that always responds with 200 OK and the given JSON body.</summary>
        public static TestHttpMessageHandler ReturnJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new TestHttpMessageHandler(_ =>
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
        }

        /// <summary>Returns a handler that always throws an HttpRequestException.</summary>
        public static TestHttpMessageHandler Throws() =>
            new(_ => throw new HttpRequestException("Simulated network error"));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_handler(request));
        }
    }
}
