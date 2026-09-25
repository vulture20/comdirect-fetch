namespace ComdirectFetch.Worker;

/// <summary>
/// Konfiguration für die aktive Benachrichtigung, wenn eine neue TAN-Freigabe erforderlich wird
/// (GitHub-Issue #6, KONZEPT.md Abschnitt 3). Zwei unabhängige Kanäle, beide opt-in und
/// gleichzeitig nutzbar ("auch parallel"): E-Mail (SMTP) und Webhook (beliebiger HTTP-Endpunkt,
/// z. B. ntfy.sh, Home Assistant, n8n). Ist keiner konfiguriert, passiert nichts - wie bisher
/// nur passiv über comdirectctl.sh status/sync_log sichtbar.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    /// <summary>E-Mail-Kanal aktiv, sobald Host, Absender und mindestens ein Empfänger gesetzt sind.</summary>
    public string? EmailSmtpHost { get; set; }
    public int EmailSmtpPort { get; set; } = 587;
    public string? EmailSmtpUser { get; set; }
    public string? EmailSmtpPassword { get; set; }
    public bool EmailUseStartTls { get; set; } = true;
    public string? EmailFrom { get; set; }

    /// <summary>Kommagetrennt bei mehreren Empfängern.</summary>
    public string? EmailTo { get; set; }

    /// <summary>Webhook-Kanal aktiv, sobald eine URL gesetzt ist. POST mit generischem JSON-Body.</summary>
    public string? WebhookUrl { get; set; }
}
