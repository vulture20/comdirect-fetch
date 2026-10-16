namespace ComdirectFetch.Domain;

/// <summary>
/// Kalendertag aus Sicht der Bank (Issue #18): comdirects Buchungstage sind deutsche Kalendertage, ein
/// Snapshot um 00:30 Uhr Ortszeit gehört also zum neuen Tag, obwohl es in UTC noch der Vortag ist.
/// </summary>
public static class BankCalendar
{
    private static readonly TimeZoneInfo BankTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static DateOnly ToBankDate(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, BankTimeZone).DateTime);
}
