using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Api;

/// <summary>Salden und Kontoumsätze (KONZEPT.md Abschnitt 3). Pfade siehe Hinweis in BankingModels.cs.</summary>
public sealed class ComdirectBankingClient(
    HttpClient httpClient,
    IOptions<ComdirectApiOptions> options,
    ComdirectRequestContext requestContext)
{
    private readonly ComdirectApiOptions _options = options.Value;

    public async Task<IReadOnlyList<AccountBalanceEntry>> GetBalancesAsync(
        OAuthToken token, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/banking/clients/user/v1/accounts/balances", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AccountBalanceResponse>(cancellationToken: cancellationToken);
        return body?.Values ?? [];
    }

    public async Task<IReadOnlyList<TransactionEntry>> GetTransactionsAsync(
        OAuthToken token, string accountId, CancellationToken cancellationToken = default)
    {
        var path = $"/api/banking/v1/accounts/{accountId}/transactions";
        using var request = CreateRequest(HttpMethod.Get, path, token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<TransactionsResponse>(cancellationToken: cancellationToken);
        return body?.Values ?? [];

        // TODO: comdirect paginiert Umsätze (z. B. über "paging"-Felder in der Antwort).
        // Für vollständige Historie beim ersten Lauf muss ggf. über mehrere Seiten iteriert werden.
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, OAuthToken token)
    {
        var request = new HttpRequestMessage(method, $"{_options.BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Add("x-http-request-info", requestContext.BuildRequestInfoHeader());
        return request;
    }
}
