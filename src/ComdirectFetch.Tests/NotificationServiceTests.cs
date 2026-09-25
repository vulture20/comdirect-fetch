using ComdirectFetch.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ComdirectFetch.Tests;

/// <summary>
/// Reine Dispatch-Logik von NotificationService (GitHub-Issue #6), ohne echte E-Mail-/Webhook-
/// Kanäle - die faken INotificationChannel-Implementierungen erlauben, "alle aktivierten Kanäle
/// gleichzeitig, ein Kanal-Fehler blockiert die anderen nicht" deterministisch zu verifizieren.
/// </summary>
public class NotificationServiceTests
{
    private sealed class FakeChannel(bool enabled, bool throwOnNotify = false) : INotificationChannel
    {
        public bool IsEnabled => enabled;
        public int CallCount { get; private set; }
        public NotificationMessage? LastMessage { get; private set; }

        public Task NotifyAsync(NotificationMessage message, CancellationToken cancellationToken)
        {
            CallCount++;
            LastMessage = message;
            if (throwOnNotify)
            {
                throw new InvalidOperationException("simulierter Kanal-Fehler");
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Spricht_nur_aktivierte_Kanaele_an()
    {
        var enabledChannel = new FakeChannel(enabled: true);
        var disabledChannel = new FakeChannel(enabled: false);
        var service = new NotificationService([enabledChannel, disabledChannel], NullLogger<NotificationService>.Instance);

        await service.NotifyAuthRequiredAsync("Testfehler", CancellationToken.None);

        Assert.Equal(1, enabledChannel.CallCount);
        Assert.Equal(0, disabledChannel.CallCount);
        Assert.Contains("Testfehler", enabledChannel.LastMessage!.Body);
    }

    [Fact]
    public async Task Spricht_alle_aktivierten_Kanaele_an_auch_wenn_einer_fehlschlaegt()
    {
        var failingChannel = new FakeChannel(enabled: true, throwOnNotify: true);
        var workingChannel = new FakeChannel(enabled: true);
        var service = new NotificationService([failingChannel, workingChannel], NullLogger<NotificationService>.Instance);

        await service.NotifyAuthRequiredAsync("Testfehler", CancellationToken.None);

        Assert.Equal(1, failingChannel.CallCount);
        Assert.Equal(1, workingChannel.CallCount);
    }

    [Fact]
    public async Task Tut_nichts_wenn_kein_Kanal_aktiviert_ist()
    {
        var disabledChannel = new FakeChannel(enabled: false);
        var service = new NotificationService([disabledChannel], NullLogger<NotificationService>.Instance);

        await service.NotifyAuthRequiredAsync("Testfehler", CancellationToken.None);

        Assert.Equal(0, disabledChannel.CallCount);
    }
}
