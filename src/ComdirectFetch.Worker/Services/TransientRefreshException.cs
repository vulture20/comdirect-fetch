namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Der Token-Refresh ist an einem vorübergehenden Fehler gescheitert (Netzwerk, Timeout, 429/5xx nach allen
/// Wiederholungen). Der Token bleibt erhalten, ein späterer Versuch kann gelingen - anders als bei einer echten
/// Ablehnung durch comdirect, nach der eine neue TAN-Freigabe nötig ist.
/// </summary>
public sealed class TransientRefreshException(Exception inner)
    : Exception($"Token-Refresh vorübergehend fehlgeschlagen: {inner.Message}", inner);
