namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Dispatcher für alle aktivierten Benachrichtigungskanäle (GitHub-Issue #6). Spricht alle
/// aktivierten Kanäle GLEICHZEITIG an ("auch parallel" - explizite Nutzer-Anforderung), nicht
/// nacheinander, damit ein langsamer oder fehlschlagender Kanal die anderen nicht verzögert
/// oder blockiert. Ein Kanal-Fehler wird geloggt, aber nie an den Aufrufer durchgereicht - eine
/// fehlgeschlagene Benachrichtigung darf den eigentlichen Auth-Fehlerfluss nicht stören.
/// </summary>
public sealed class NotificationService(
    IEnumerable<INotificationChannel> channels,
    ILogger<NotificationService> logger)
{
    /// <summary>Wird aufgerufen, sobald die comdirect-Session eine neue TAN-Freigabe braucht (ComdirectAuthCoordinator.RefreshAsync).</summary>
    public async Task NotifyAuthRequiredAsync(string errorDetail, CancellationToken cancellationToken)
    {
        var enabledChannels = channels.Where(c => c.IsEnabled).ToList();
        if (enabledChannels.Count == 0)
        {
            return;
        }

        var message = new NotificationMessage(
            "comdirect-fetch: TAN-Freigabe erforderlich",
            "Die comdirect-Session ist abgelaufen oder konnte nach einem Neustart nicht automatisch " +
            "wiederhergestellt werden. Bitte 'comdirectctl.sh auth start' (bzw. POST /auth/start) " +
            $"ausführen und die TAN-Freigabe bestätigen.\n\nFehlerdetail: {errorDetail}");

        var tasks = enabledChannels.Select(channel => NotifySingleChannelAsync(channel, message, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task NotifySingleChannelAsync(INotificationChannel channel, NotificationMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await channel.NotifyAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Benachrichtigung über {Channel} fehlgeschlagen.", channel.GetType().Name);
        }
    }
}
