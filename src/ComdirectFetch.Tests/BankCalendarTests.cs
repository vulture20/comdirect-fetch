using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

public class BankCalendarTests
{
    [Theory]
    [InlineData(2026, 9, 15, 21, 59, 15)]   // Sommerzeit (UTC+2): 23:59 Uhr Ortszeit
    [InlineData(2026, 9, 15, 22, 0, 16)]    // 00:00 Uhr Ortszeit = neuer Tag
    [InlineData(2026, 12, 15, 22, 30, 15)]  // Winterzeit (UTC+1): 23:30 Uhr Ortszeit
    [InlineData(2026, 12, 15, 23, 0, 16)]   // Winterzeit: 00:00 Uhr Ortszeit
    public void Zeitpunkt_wird_dem_deutschen_Kalendertag_zugeordnet(int year, int month, int day, int hour, int minute, int expectedDay)
    {
        var date = BankCalendar.ToBankDate(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(year, month, expectedDay), date);
    }
}
