using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

public class ErrorBodyCondenserTests
{
    [Fact]
    public void Laesst_kurzen_JSON_Fehlertext_unveraendert()
    {
        const string body = "{\"error\":\"unauthorized\",\"error_description\":\"Fehler beim Login-Service. HttpStatus: '500'\"}";

        Assert.Equal(body, ErrorBodyCondenser.Condense(body));
    }

    [Fact]
    public void Ersetzt_HTML_Fehlerseite_durch_ihren_Titel()
    {
        const string body = "<!doctype html>\n<html>\n<head>\n    <title>comdirect - eine Marke der Commerzbank AG</title>\n"
            + "    <style>\n        body { margin: 0; }\n    </style>\n</head>\n<body>\n    <svg>...</svg>\n</body>\n</html>\n";

        Assert.Equal("[HTML-Seite: comdirect - eine Marke der Commerzbank AG]", ErrorBodyCondenser.Condense(body));
    }

    [Fact]
    public void Nennt_bei_HTML_ohne_Titel_nur_die_Laenge()
    {
        const string body = "<html><body>Fehler</body></html>";

        Assert.Equal($"[HTML-Seite, {body.Length} Zeichen]", ErrorBodyCondenser.Condense(body));
    }

    [Fact]
    public void Fasst_Zeilenumbrueche_und_Leerraum_zusammen()
    {
        Assert.Equal("Service nicht verfügbar bitte später", ErrorBodyCondenser.Condense("  Service nicht\r\n\n verfügbar\t bitte später \n"));
    }

    [Fact]
    public void Kuerzt_lange_Texte()
    {
        var result = ErrorBodyCondenser.Condense(new string('x', 2000));

        Assert.Equal(ErrorBodyCondenser.MaxLength + 1, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Leerer_Body_bleibt_leer()
    {
        Assert.Equal(string.Empty, ErrorBodyCondenser.Condense(" \n "));
    }
}
