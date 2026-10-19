using System.Text.RegularExpressions;

namespace ComdirectFetch.Domain;

/// <summary>
/// Kürzt einen HTTP-Fehler-Body auf eine einzeilige, kurze Beschreibung, bevor er in Exception-Message,
/// Log und sync_log.error_message landet. Live passiert: bei einer Störung lieferte comdirect auf
/// /oauth/token statt JSON eine komplette HTML-Fehlerseite (503, mehrere KB inkl. CSS und SVG-Logo),
/// die ungekürzt in sync_log stand und die Statusausgabe von comdirectctl.sh mit Hunderten Zeilen
/// zumüllte. Von einer HTML-Seite bleibt nur der Titel, sonst werden Zeilenumbrüche und Leerraum
/// zusammengefasst und der Text auf MaxLength Zeichen begrenzt - die eigentlichen comdirect-Fehlertexte
/// (JSON, meist unter 200 Zeichen) bleiben dadurch unverändert.
/// </summary>
public static partial class ErrorBodyCondenser
{
    public const int MaxLength = 500;

    public static string Condense(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (LooksLikeHtml(trimmed))
        {
            var title = TitleRegex().Match(trimmed);
            var titleText = title.Success ? CollapseWhitespace(title.Groups[1].Value) : string.Empty;
            return titleText.Length > 0
                ? $"[HTML-Seite: {Truncate(titleText)}]"
                : $"[HTML-Seite, {trimmed.Length} Zeichen]";
        }

        return Truncate(CollapseWhitespace(trimmed));
    }

    private static bool LooksLikeHtml(string text) =>
        text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase);

    private static string CollapseWhitespace(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    private static string Truncate(string text) =>
        text.Length <= MaxLength ? text : string.Concat(text.AsSpan(0, MaxLength), "…");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
}
