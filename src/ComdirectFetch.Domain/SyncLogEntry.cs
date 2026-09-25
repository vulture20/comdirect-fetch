namespace ComdirectFetch.Domain;

/// <summary>Protokoll eines Abrufvorgangs (KONZEPT.md Abschnitt 5, Tabelle sync_log).</summary>
public sealed class SyncLogEntry
{
    public long Id { get; set; }
    public required SyncDataKind DataKind { get; set; }

    /// <summary>Optional, da z. B. ein Token-Refresh keinem einzelnen Konto/Depot zugeordnet ist.</summary>
    public long? AccountId { get; set; }
    public long? PortfolioId { get; set; }

    public required string ApplicationVersion { get; set; }
    public required DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public required SyncStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}
