using ComdirectFetch.Worker.Services;

namespace ComdirectFetch.Tests;

public class WebhookNotificationChannelTests
{
    [Theory]
    [InlineData("https://api.telegram.org/bot123456:ABC-geheim/sendMessage?chat_id=42&text=x", "https://api.telegram.org")]
    [InlineData("http://homeassistant.local:8123/api/webhook/geheim", "http://homeassistant.local:8123")]
    [InlineData("https://user:pass@ntfy.example/topic", "https://ntfy.example")]
    [InlineData("kein-url", "(ungültige URL)")]
    [InlineData(null, "(ungültige URL)")]
    public void DescribeTarget_zeigt_nur_Schema_und_Host(string? url, string expected)
    {
        Assert.Equal(expected, WebhookNotificationChannel.DescribeTarget(url));
    }
}
