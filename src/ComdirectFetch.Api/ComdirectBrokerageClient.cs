using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Api;

/// <summary>Depotübersicht und -positionen (KONZEPT.md Abschnitt 3). Pfade siehe Hinweis in BrokerageModels.cs.</summary>
public sealed class ComdirectBrokerageClient(
    HttpClient httpClient,
    IOptions<ComdirectApiOptions> options,
    ComdirectRequestContext requestContext)
{
    private readonly ComdirectApiOptions _options = options.Value;

    public async Task<IReadOnlyList<DepotEntry>> GetDepotsAsync(
        OAuthToken token, CancellationToken cancellationToken = default)
    {
        using var response = await ComdirectResilience.SendWithRetryAsync(
            httpClient,
            () => CreateRequest(HttpMethod.Get, "/api/brokerage/clients/user/v3/depots", token),
            cancellationToken);
        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<DepotsResponse>(cancellationToken: cancellationToken);
        return body?.Values ?? [];
    }

    public async Task<DepotPositionsResponse> GetDepotPositionsAsync(
        OAuthToken token, string depotId, CancellationToken cancellationToken = default)
    {
        // with-attr=instrument: ohne diesen Parameter liefert die Position weder ISIN noch Name.
        var path = $"/api/brokerage/v3/depots/{depotId}/positions?with-attr=instrument";
        using var response = await ComdirectResilience.SendWithRetryAsync(
            httpClient,
            () => CreateRequest(HttpMethod.Get, path, token),
            cancellationToken);
        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        return await response.Content.ReadFromJsonAsync<DepotPositionsResponse>(cancellationToken: cancellationToken)
            ?? new DepotPositionsResponse();
    }

    /// <summary>
    /// Alle Depot-Transaktionen (Käufe/Verkäufe) eines Depots, Issue #19. Ohne min-bookingDate liefert
    /// comdirect nur ein Standard-Zeitfenster (live: die ältesten Trades fehlten), daher bewusst ein
    /// frühes Startdatum. Paginierung wie bei den Kontoumsätzen (paging-first/paging.matches, live
    /// geprüft). Rein lesend, daher über ComdirectResilience.
    /// </summary>
    public async Task<IReadOnlyList<DepotTransactionEntry>> GetDepotTransactionsAsync(
        OAuthToken token, string depotId, CancellationToken cancellationToken = default)
    {
        var results = new List<DepotTransactionEntry>();
        var first = 0;

        while (true)
        {
            var path = $"/api/brokerage/v3/depots/{depotId}/transactions" +
                $"?with-attr=instrument&min-bookingDate=2000-01-01&paging-count=500&paging-first={first}";
            using var response = await ComdirectResilience.SendWithRetryAsync(
                httpClient,
                () => CreateRequest(HttpMethod.Get, path, token),
                cancellationToken);
            await response.EnsureSuccessWithBodyAsync(cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<DepotTransactionsResponse>(cancellationToken: cancellationToken);
            if (body is null || body.Values.Count == 0)
            {
                break;
            }

            results.AddRange(body.Values);

            var matches = body.Paging?.Matches ?? results.Count;
            first += body.Values.Count;
            if (first >= matches)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        return results;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, OAuthToken token)
    {
        var request = new HttpRequestMessage(method, $"{_options.BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("x-http-request-info", requestContext.BuildRequestInfoHeader());
        return request;
    }
}
