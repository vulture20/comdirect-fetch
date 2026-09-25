using System.Net.Http.Json;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Webhook-Kanal für Benachrichtigungen (GitHub-Issue #6) - POST mit generischem JSON-Body an
/// eine beliebige URL (z. B. ntfy.sh, Home Assistant, n8n/Node-RED - alles, was einen simplen
/// HTTP-POST-Trigger akzeptiert). Bewusst kein Dienst-spezifisches Format (z. B. Slack-Webhook-
/// Schema), um nicht an einen bestimmten Anbieter zu binden.
/// </summary>
public sealed class WebhookNotificationChannel(
    HttpClient httpClient,
    IOptions<NotificationOptions> options,
    ILogger<WebhookNotificationChannel> logger) : INotificationChannel
{
    private readonly NotificationOptions _options = options.Value;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.WebhookUrl);

    public async Task NotifyAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var payload = new
        {
            @event = "auth_required",
            subject = message.Subject,
            body = message.Body,
            occurredAt = DateTimeOffset.UtcNow,
            appVersion = AppVersion.Current,
        };

        using var response = await httpClient.PostAsJsonAsync(_options.WebhookUrl, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        logger.LogInformation("Benachrichtigung per Webhook an {Url} gesendet.", _options.WebhookUrl);
    }
}
