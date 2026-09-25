using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Api;

/// <summary>
/// Salden und Kontoumsätze (KONZEPT.md Abschnitt 3). Pfade gegen die offizielle
/// comdirect-Doku/Swagger verifiziert (/opt/comdirect-fetch/docs).
/// </summary>
public sealed class ComdirectBankingClient(
    HttpClient httpClient,
    IOptions<ComdirectApiOptions> options,
    ComdirectRequestContext requestContext)
{
    private readonly ComdirectApiOptions _options = options.Value;

    public async Task<IReadOnlyList<AccountBalanceEntry>> GetBalancesAsync(
        OAuthToken token, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/banking/clients/user/v2/accounts/balances", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AccountBalanceResponse>(cancellationToken: cancellationToken);
        return body?.Values ?? [];
    }

    /// <summary>Iteriert über alle Seiten (paging-first/paging.matches), bis alle Umsätze abgerufen sind.</summary>
    public async Task<IReadOnlyList<TransactionEntry>> GetTransactionsAsync(
        OAuthToken token, string accountId, CancellationToken cancellationToken = default)
    {
        var results = new List<TransactionEntry>();
        var first = 0;

        while (true)
        {
            var path = $"/api/banking/v1/accounts/{accountId}/transactions?paging-first={first}";
            using var request = CreateRequest(HttpMethod.Get, path, token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<TransactionsResponse>(cancellationToken: cancellationToken);
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
        }

        return results;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, OAuthToken token)
    {
        var request = new HttpRequestMessage(method, $"{_options.BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Add("x-http-request-info", requestContext.BuildRequestInfoHeader());
        return request;
    }
}
