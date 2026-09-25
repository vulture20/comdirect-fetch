namespace ComdirectFetch.Worker.Services;

/// <summary>Ein einzelner Benachrichtigungskanal (GitHub-Issue #6) - E-Mail, Webhook, ggf. später weitere.</summary>
public interface INotificationChannel
{
    /// <summary>Ob dieser Kanal ausreichend konfiguriert ist, um genutzt zu werden. Rein opt-in - keine Pflichtkonfiguration.</summary>
    bool IsEnabled { get; }

    Task NotifyAsync(NotificationMessage message, CancellationToken cancellationToken);
}

public sealed record NotificationMessage(string Subject, string Body);
