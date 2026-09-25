namespace ComdirectFetch.Api;

internal static class HttpResponseExtensions
{
    /// <summary>
    /// Wie EnsureSuccessStatusCode(), liest bei einem Fehler aber zusätzlich den Response-Body
    /// (comdirect liefert dort meist eine aussagekräftige Fehlerbeschreibung, z. B. bei 422)
    /// und hängt ihn an die Exception-Message an, statt ihn stillschweigend zu verwerfen.
    /// </summary>
    public static async Task EnsureSuccessWithBodyAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"{(int)response.StatusCode} {response.ReasonPhrase} für {response.RequestMessage?.RequestUri}: {body}");
    }
}
