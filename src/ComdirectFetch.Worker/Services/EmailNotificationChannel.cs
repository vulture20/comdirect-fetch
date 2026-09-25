using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// E-Mail-Kanal für Benachrichtigungen (GitHub-Issue #6) über SMTP
/// (System.Net.Mail.SmtpClient - Teil der BCL, keine zusätzliche Paketabhängigkeit nötig für
/// diesen gelegentlichen, niedrigvolumigen Anwendungsfall).
/// </summary>
public sealed class EmailNotificationChannel(
    IOptions<NotificationOptions> options,
    ILogger<EmailNotificationChannel> logger) : INotificationChannel
{
    private readonly NotificationOptions _options = options.Value;

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_options.EmailSmtpHost) &&
        !string.IsNullOrWhiteSpace(_options.EmailFrom) &&
        !string.IsNullOrWhiteSpace(_options.EmailTo);

    public async Task NotifyAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_options.EmailSmtpHost, _options.EmailSmtpPort)
        {
            EnableSsl = _options.EmailUseStartTls,
        };
        if (!string.IsNullOrEmpty(_options.EmailSmtpUser))
        {
            client.Credentials = new NetworkCredential(_options.EmailSmtpUser, _options.EmailSmtpPassword);
        }

        using var mail = new MailMessage(_options.EmailFrom!, _options.EmailTo!, message.Subject, message.Body);
        await client.SendMailAsync(mail, cancellationToken);
        logger.LogInformation("Benachrichtigung per E-Mail an {To} gesendet.", _options.EmailTo);
    }
}
