using Polly;
using Polly.Retry;

namespace ComdirectFetch.Api;

/// <summary>
/// Retry mit exponentiellem Backoff für HTTP 429 (Rate-Limit, live beobachtet, siehe
/// CHANGELOG.md 0.5.0) und 5xx. Respektiert einen "Retry-After"-Header, falls comdirect
/// einen sendet, sonst exponentielles Backoff mit Jitter.
///
/// ACHTUNG: Bewusst NICHT auf den TAN-Endpunkten (Session validieren/aktivieren) verwendet –
/// ein automatischer Retry dort könnte ungewollt eine neue TAN-Challenge auslösen und zur
/// 5-Challenge-Sperre beitragen (siehe ComdirectAuthClient). Nur für Token- und
/// Datenabrufe, die gefahrlos wiederholbar sind.
/// </summary>
public static class ComdirectResilience
{
    public const int MaxRetryAttempts = 4;

    private static readonly ResiliencePipeline<HttpResponseMessage> Pipeline = BuildPipeline(TimeSpan.FromSeconds(1));

    /// <summary>Als eigene Methode (statt inline im static field) auch für Tests mit kurzen Delays nutzbar.</summary>
    public static ResiliencePipeline<HttpResponseMessage> BuildPipeline(TimeSpan baseDelay) =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Result is { } response
                    && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)),
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = baseDelay,
                UseJitter = true,
                DelayGenerator = args => ValueTask.FromResult(args.Outcome.Result?.Headers.RetryAfter?.Delta),
            })
            .Build();

    /// <param name="requestFactory">
    /// Muss bei jedem Aufruf ein neues HttpRequestMessage liefern – ein einmal gesendetes
    /// HttpRequestMessage kann .NET nicht für einen Retry wiederverwenden.
    /// </param>
    /// <param name="pipeline">Nur für Tests: eigene Pipeline mit kürzeren Delays statt der Produktions-Pipeline.</param>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        ResiliencePipeline<HttpResponseMessage>? pipeline = null)
    {
        return await (pipeline ?? Pipeline).ExecuteAsync(
            async ct =>
            {
                using var request = requestFactory();
                return await httpClient.SendAsync(request, ct);
            },
            cancellationToken);
    }
}
