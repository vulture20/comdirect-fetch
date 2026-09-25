using System.Text.Json.Serialization;

namespace ComdirectFetch.Api;

// Verifiziert gegen die offizielle comdirect REST API Dokumentation/Swagger
// (/opt/comdirect-fetch/docs, Stand siehe README) – Endpunkte /banking/clients/{user}/v2/accounts/balances
// und /banking/v1/accounts/{accountId}/transactions.

public sealed class AccountBalanceResponse
{
    [JsonPropertyName("values")]
    public List<AccountBalanceEntry> Values { get; init; } = [];
}

public sealed class AccountBalanceEntry
{
    [JsonPropertyName("account")]
    public required AccountInfo Account { get; init; }

    [JsonPropertyName("balance")]
    public required AmountValue Balance { get; init; }

    [JsonPropertyName("availableCashAmount")]
    public required AmountValue AvailableCashAmount { get; init; }
}

public sealed class AccountInfo
{
    [JsonPropertyName("accountId")]
    public required string AccountId { get; init; }

    [JsonPropertyName("iban")]
    public string? Iban { get; init; }

    [JsonPropertyName("accountType")]
    public KeyText? AccountType { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }
}

public sealed class TransactionsResponse
{
    [JsonPropertyName("paging")]
    public PagingInfo? Paging { get; init; }

    [JsonPropertyName("values")]
    public List<TransactionEntry> Values { get; init; } = [];
}

public sealed class PagingInfo
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>Gesamtzahl der Treffer über alle Seiten – Basis für die Pagination-Schleife.</summary>
    [JsonPropertyName("matches")]
    public int Matches { get; init; }
}

public sealed class TransactionEntry
{
    [JsonPropertyName("reference")]
    public required string Reference { get; init; }

    /// <summary>Verschachteltes Objekt {"date": "yyyy-MM-dd"} – kein einfacher String (anders als valutaDate).</summary>
    [JsonPropertyName("bookingDate")]
    public required DateWrapper BookingDate { get; init; }

    [JsonPropertyName("valutaDate")]
    public string? ValutaDate { get; init; }

    [JsonPropertyName("amount")]
    public required AmountValue Amount { get; init; }

    [JsonPropertyName("remittanceInfo")]
    public string? RemittanceInfo { get; init; }

    [JsonPropertyName("transactionType")]
    public KeyText? TransactionType { get; init; }

    [JsonPropertyName("remitter")]
    public AccountInformation? Remitter { get; init; }

    [JsonPropertyName("deptor")]
    public AccountInformation? Debtor { get; init; }

    [JsonPropertyName("creditor")]
    public AccountInformation? Creditor { get; init; }
}

public sealed class DateWrapper
{
    [JsonPropertyName("date")]
    public required DateOnly Date { get; init; }
}

public sealed class AccountInformation
{
    [JsonPropertyName("holderName")]
    public string? HolderName { get; init; }

    [JsonPropertyName("iban")]
    public string? Iban { get; init; }

    [JsonPropertyName("bic")]
    public string? Bic { get; init; }
}

/// <summary>comdirect liefert Beträge als JSON-String (z. B. "999.99"), nicht als Zahl.</summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed class AmountValue
{
    [JsonPropertyName("value")]
    public required decimal Value { get; init; }

    [JsonPropertyName("unit")]
    public required string Unit { get; init; }
}

public sealed class KeyText
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
