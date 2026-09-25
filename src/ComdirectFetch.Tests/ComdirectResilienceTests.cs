using System.Net;
using ComdirectFetch.Api;
using Polly;

namespace ComdirectFetch.Tests;

public class ComdirectResilienceTests
{
    // Winzige Delays statt der Produktions-Pipeline (Basis 1s exponentiell), damit die Tests
    // nicht mehrere Sekunden auf echte Backoff-Pausen warten müssen.
    private static readonly ResiliencePipeline<HttpResponseMessage> FastPipeline =
        ComdirectResilience.BuildPipeline(TimeSpan.FromMilliseconds(1));

    private sealed class SequenceHandler(params HttpStatusCode[] statusCodes) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = statusCodes[Math.Min(_callCount, statusCodes.Length - 1)];
            _callCount++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    [Fact]
    public async Task Wiederholt_bei_429_und_liefert_schliesslich_den_erfolgreichen_Versuch()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);

        using var response = await ComdirectResilience.SendWithRetryAsync(
            httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/"),
            CancellationToken.None,
            FastPipeline);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Gibt_nach_MaxRetryAttempts_den_letzten_429_zurueck_statt_endlos_zu_wiederholen()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);

        using var response = await ComdirectResilience.SendWithRetryAsync(
            httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/"),
            CancellationToken.None,
            FastPipeline);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(5, handler.CallCount); // 1 Erstversuch + 4 Retries
    }

    [Fact]
    public async Task Wiederholt_nicht_bei_einem_normalen_Fehlerstatus_wie_404()
    {
        var handler = new SequenceHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);

        using var response = await ComdirectResilience.SendWithRetryAsync(
            httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/"),
            CancellationToken.None,
            FastPipeline);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }
}
