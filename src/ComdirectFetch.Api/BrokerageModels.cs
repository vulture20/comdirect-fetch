using System.Text.Json.Serialization;

namespace ComdirectFetch.Api;

// Verifiziert gegen die offizielle comdirect REST API Dokumentation/Swagger
// (/opt/comdirect-fetch/docs) – Endpunkte /brokerage/clients/{userId}/v3/depots und
// /brokerage/v3/depots/{depotId}/positions.

public sealed class DepotsResponse
{
    [JsonPropertyName("values")]
    public List<DepotEntry> Values { get; init; } = [];
}

public sealed class DepotEntry
{
    [JsonPropertyName("depotId")]
    public required string DepotId { get; init; }

    [JsonPropertyName("depotDisplayId")]
    public string? DepotDisplayId { get; init; }

    /// <summary>Kontoverknüpfung für Issue #12 (KONZEPT.md Abschnitt 6 Phase 4) - laut Swagger die
    /// "Account Id" des Standard-Verrechnungskontos; Format (UUID wie accounts.comdirect_account_id
    /// oder Kontonummer) live gegen echte Depots verifiziert, siehe PortfolioFetchService.</summary>
    [JsonPropertyName("defaultSettlementAccountId")]
    public string? DefaultSettlementAccountId { get; init; }

    [JsonPropertyName("settlementAccountIds")]
    public List<string>? SettlementAccountIds { get; init; }
}

public sealed class DepotPositionsResponse
{
    [JsonPropertyName("values")]
    public List<DepotPositionEntry> Values { get; init; } = [];

    [JsonPropertyName("aggregated")]
    public DepotAggregation? Aggregated { get; init; }
}

public sealed class DepotAggregation
{
    [JsonPropertyName("currentValue")]
    public AmountValue? CurrentValue { get; init; }

    [JsonPropertyName("purchaseValue")]
    public AmountValue? PurchaseValue { get; init; }
}

public sealed class DepotPositionEntry
{
    /// <summary>Nur die WKN steht direkt auf der Position; ISIN/Name kommen aus <see cref="Instrument"/>.</summary>
    [JsonPropertyName("wkn")]
    public string? Wkn { get; init; }

    /// <summary>Nur befüllt, wenn die Anfrage mit Query-Parameter "with-attr=instrument" erfolgt.</summary>
    [JsonPropertyName("instrument")]
    public Instrument? Instrument { get; init; }

    [JsonPropertyName("quantity")]
    public required AmountValue Quantity { get; init; }

    [JsonPropertyName("currentValue")]
    public required AmountValue CurrentValue { get; init; }

    [JsonPropertyName("purchaseValue")]
    public required AmountValue PurchaseValue { get; init; }

    [JsonPropertyName("profitLossPurchaseAbs")]
    public AmountValue? ProfitLossAbsolute { get; init; }
}

public sealed class Instrument
{
    [JsonPropertyName("isin")]
    public string? Isin { get; init; }

    [JsonPropertyName("wkn")]
    public string? Wkn { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Kommt bereits mit with-attr=instrument mit, kein weiteres with-attr nötig (GitHub-Issue #4).</summary>
    [JsonPropertyName("staticData")]
    public StaticData? StaticData { get; init; }
}

/// <summary>Nur die für dieses Projekt relevanten Felder aus dem offiziellen StaticData-Schema.</summary>
public sealed class StaticData
{
    /// <summary>
    /// Enum-String laut offizieller Doku: SHARE, BONDS, SUBSCRIPTION_RIGHT, ETF,
    /// PROFIT_PART_CERTIFICATE, FUND, WARRANT, CERTIFICATE, NOT_AVAILABLE. Bewusst als string statt
    /// C#-Enum modelliert - comdirect kann diese Liste jederzeit erweitern, ohne dass wir das vorher wissen.
    /// </summary>
    [JsonPropertyName("instrumentType")]
    public string? InstrumentType { get; init; }
}
