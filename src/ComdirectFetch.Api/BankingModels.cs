using System.Text.Json.Serialization;

namespace ComdirectFetch.Api;

// Feldnamen sind ein Best-Effort-Nachbau aus comdirect-Community-Quellen, NICHT aus der
// offiziellen Doku verifiziert. Vor dem ersten produktiven Lauf gegen die tatsächliche
// API-Antwort (z. B. via Postman-Collection von developer.comdirect.de) abgleichen und
// bei Abweichungen hier anpassen.

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
    [JsonPropertyName("values")]
    public List<TransactionEntry> Values { get; init; } = [];
}

public sealed class TransactionEntry
{
    [JsonPropertyName("reference")]
    public required string Reference { get; init; }

    [JsonPropertyName("bookingDate")]
    public required DateOnly BookingDate { get; init; }

    [JsonPropertyName("valutaDate")]
    public DateOnly? ValutaDate { get; init; }

    [JsonPropertyName("amount")]
    public required AmountValue Amount { get; init; }

    [JsonPropertyName("remittanceInfo")]
    public string? RemittanceInfo { get; init; }

    [JsonPropertyName("transactionType")]
    public KeyText? TransactionType { get; init; }
}

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
