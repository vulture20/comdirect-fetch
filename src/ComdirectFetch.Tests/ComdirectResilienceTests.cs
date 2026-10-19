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

    /// <summary>Wirft bei den ersten Aufrufen einen Verbindungsfehler wie ein echter Netzwerkausfall, danach 200.</summary>
    private sealed class FlakyNetworkHandler(int failures) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return CallCount <= failures
                ? throw new HttpRequestException("An error occurred while sending the request.")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task Wiederholt_bei_einem_Verbindungsfehler()
    {
        var handler = new FlakyNetworkHandler(failures: 2);
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
    public async Task Wirft_den_Verbindungsfehler_nach_MaxRetryAttempts_weiter()
    {
        var handler = new FlakyNetworkHandler(failures: int.MaxValue);
        using var httpClient = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => ComdirectResilience.SendWithRetryAsync(
            httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/"),
            CancellationToken.None,
            FastPipeline));
        Assert.Equal(5, handler.CallCount);
    }

    [Theory]
    [InlineData(null, true)]                                   // keine Antwort: Verbindung/DNS/TLS
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]           // echte Ablehnung, z. B. Refresh-Token ungültig
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void IsTransient_unterscheidet_Netzwerkfehler_und_Ablehnung(HttpStatusCode? status, bool expected)
    {
        var exception = new HttpRequestException("x", inner: null, status);

        Assert.Equal(expected, ComdirectResilience.IsTransient(exception, CancellationToken.None));
    }

    [Fact]
    public void IsTransient_Timeout_ja_Abbruch_durch_den_Aufrufer_nein()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.True(ComdirectResilience.IsTransient(new TaskCanceledException(), CancellationToken.None));
        Assert.False(ComdirectResilience.IsTransient(new TaskCanceledException(), cancelled.Token));
        Assert.False(ComdirectResilience.IsTransient(new InvalidOperationException(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureSuccessWithBodyAsync_gibt_den_Statuscode_mit()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("invalid_token") };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => response.EnsureSuccessWithBodyAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.False(ComdirectResilience.IsTransient(exception, CancellationToken.None));
    }

    [Theory]
    [InlineData("401  für https://api.comdirect.de/oauth/token: {\"error\":\"unauthorized\",\"error_description\":\"Fehler beim Corona-Login-Service. HttpStatus: '500'\"}", true)]
    [InlineData("401  für https://api.comdirect.de/oauth/token: {\"error\":\"invalid_token\",\"error_description\":\"Invalid refresh token\"}", false)]
    public void IsTransient_erkennt_als_401_verpackten_Serverfehler(string message, bool expected)
    {
        var exception = new HttpRequestException(message, inner: null, HttpStatusCode.Unauthorized);

        Assert.Equal(expected, ComdirectResilience.IsTransient(exception, CancellationToken.None));
    }

    [Fact]
    public void IsTransient_verpackter_Serverfehler_nur_bei_401()
    {
        var exception = new HttpRequestException("HttpStatus: '500'", inner: null, HttpStatusCode.BadRequest);

        Assert.False(ComdirectResilience.IsTransient(exception, CancellationToken.None));
    }
}
