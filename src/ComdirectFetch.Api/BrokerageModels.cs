using System.Text.Json.Serialization;

namespace ComdirectFetch.Api;

// Siehe Hinweis in BankingModels.cs: Best-Effort-Nachbau, vor Produktivbetrieb verifizieren.

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
    [JsonPropertyName("wkn")]
    public string? Wkn { get; init; }

    [JsonPropertyName("isin")]
    public string? Isin { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("quantity")]
    public required AmountValue Quantity { get; init; }

    [JsonPropertyName("currentValue")]
    public required AmountValue CurrentValue { get; init; }

    [JsonPropertyName("purchaseValue")]
    public required AmountValue PurchaseValue { get; init; }

    [JsonPropertyName("profitLossPurchaseAbs")]
    public AmountValue? ProfitLossAbsolute { get; init; }
}
