using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

public class CategorizationLogicTests
{
    private static Transaction MakeTransaction(
        decimal amount, string? bookingText = null, string? transactionType = null, string? counterpartyIban = null,
        string? counterpartyName = null) => new()
    {
        AccountId = 1,
        ComdirectReference = "ref-1",
        BookingDate = new DateOnly(2026, 1, 1),
        Amount = amount,
        Currency = "EUR",
        BookingText = bookingText,
        TransactionType = transactionType,
        CounterpartyIban = counterpartyIban,
        CounterpartyName = counterpartyName,
        FirstSeenAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Erkennt_interne_Umbuchung_anhand_strukturierter_Gegenkonto_Iban()
    {
        var transaction = MakeTransaction(100m, counterpartyIban: "DE02120300000000202051");

        var result = CategorizationLogic.Categorize(
            transaction,
            ownIbans: ["DE02120300000000202051"],
            internalCategoryId: 42,
            rulesByPriority: [],
            fallbackIncomeCategoryId: 1,
            fallbackExpenseCategoryId: 2);

        Assert.Equal(42, result);
    }

    [Fact]
    public void Erkennt_interne_Umbuchung_per_Freitext_Fallback_wenn_keine_strukturierte_Iban_vorliegt()
    {
        var transaction = MakeTransaction(100m, bookingText: "Umbuchung DE02120300000000202051 Tagesgeld");

        var result = CategorizationLogic.Categorize(
            transaction,
            ownIbans: ["DE02120300000000202051"],
            internalCategoryId: 42,
            rulesByPriority: [],
            fallbackIncomeCategoryId: 1,
            fallbackExpenseCategoryId: 2);

        Assert.Equal(42, result);
    }

    [Fact]
    public void Regel_auf_strukturiertem_Feld_hat_Vorrang_vor_Freitext_bei_gleicher_Prioritaetsreihenfolge()
    {
        var transaction = MakeTransaction(-15m, bookingText: "MIETE September", transactionType: "Dauerauftrag");
        var rules = new List<CategorizationRule>
        {
            new() { Pattern = "Dauerauftrag", MatchField = RuleMatchField.TransactionType, CategoryId = 10, Priority = 10 },
            new() { Pattern = "MIETE", MatchField = RuleMatchField.BookingText, CategoryId = 20, Priority = 100 },
        };

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: rules,
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.Equal(10, result);
    }

    [Fact]
    public void Freitext_Muster_wird_case_insensitiv_erkannt()
    {
        var transaction = MakeTransaction(-9m, bookingText: "netflix.com Abo");
        var rules = new List<CategorizationRule>
        {
            new() { Pattern = "NETFLIX", MatchField = RuleMatchField.BookingText, CategoryId = 30, Priority = 130 },
        };

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: rules,
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.Equal(30, result);
    }

    [Theory]
    [InlineData(50, 1)]
    [InlineData(-50, 2)]
    public void Fallback_richtet_sich_nach_Betragsvorzeichen_wenn_keine_Regel_greift(decimal amount, long expectedCategoryId)
    {
        var transaction = MakeTransaction(amount, bookingText: "Unbekannte Buchung");

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: [],
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.Equal(expectedCategoryId, result);
    }

    /// <summary>
    /// Aktueller Regelstand aus db/migrations/0004_extend_categorization_rules.sql und der
    /// Korrektur in 0005_fix_paypal_merchant_patterns.sql, als Testdaten nachgebaut (Priorität
    /// dient hier zugleich als "Kategorie-Id" für einfache Assertions).
    /// </summary>
    private static List<CategorizationRule> BuildCurrentCategorizationRules() =>
    [
        new() { Pattern = "ALDI SUED", MatchField = RuleMatchField.BookingText, CategoryId = 142, Priority = 142 },
        new() { Pattern = "Visa-Kreditkarte", MatchField = RuleMatchField.BookingText, CategoryId = 161, Priority = 161 },
        new() { Pattern = "Abschluss Zinsen", MatchField = RuleMatchField.BookingText, CategoryId = 170, Priority = 170 },
        new() { Pattern = "STEAMGAMES.COM", MatchField = RuleMatchField.BookingText, CategoryId = 200, Priority = 200 },
        new() { Pattern = "STEAM PURCHASE", MatchField = RuleMatchField.BookingText, CategoryId = 201, Priority = 201 },
        new() { Pattern = "Humble", MatchField = RuleMatchField.BookingText, CategoryId = 202, Priority = 202 }, // 0005: war "Humble Bundle", von comdirects Zeilenumbruch mitten im Wort "Bundle" zerteilt
        new() { Pattern = "CinemaxX", MatchField = RuleMatchField.BookingText, CategoryId = 203, Priority = 203 },
        new() { Pattern = "KFC MUELHEIM", MatchField = RuleMatchField.BookingText, CategoryId = 210, Priority = 210 },
        new() { Pattern = "miamamia", MatchField = RuleMatchField.BookingText, CategoryId = 211, Priority = 211 },
        new() { Pattern = "congstar", MatchField = RuleMatchField.BookingText, CategoryId = 220, Priority = 220 },
        new() { Pattern = "FirmenRente", MatchField = RuleMatchField.BookingText, CategoryId = 230, Priority = 230 },
        new() { Pattern = "Bochum-Gelsenkirchener", MatchField = RuleMatchField.BookingText, CategoryId = 240, Priority = 240 },
        new() { Pattern = "Storchen Apotheke", MatchField = RuleMatchField.BookingText, CategoryId = 250, Priority = 250 },
        new() { Pattern = "Headline", MatchField = RuleMatchField.BookingText, CategoryId = 260, Priority = 260 }, // 0005: war "Headline - Noth, Schneider", ebenfalls durch Zeilenumbruch zerteilt
    ];

    [Fact]
    public void Zinsgutschrift_wird_ueber_Abschluss_Zinsen_Muster_erkannt()
    {
        // Regressionstest für die live gefundene Fehlkategorisierung: eine echte Zinsgutschrift
        // landete in "Sonstige Einnahme", da keine Regel auf "Zinsen/Dividenden" zeigte.
        var transaction = MakeTransaction(17.24m, bookingText: "Abschluss Zinsen Kto 148457505EUR von 31.12.2025 bis 31.03.2026");

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: BuildCurrentCategorizationRules(),
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.Equal(170, result);
    }

    [Theory]
    [InlineData("01ALDI SUED, Muelheim an d  DE", 142)]
    [InlineData("01Entgelt                            02Visa-Kreditkarte", 161)]
    [InlineData("01STEAMGAMES.COM 4259522985, Hamburg", 200)]
    [InlineData("01STEAM PURCHASE, SEATTLE  DE", 201)]
    // Humble Bundle und Headline: bewusst der ECHTE, umgebrochene Text (nicht die idealisierte
    // Form) - comdirect fügt die Zeilennummer ohne Leerzeichen mitten ins Wort ein
    // ("Humble B02undle", "Headline02 - Noth"), siehe 0005_fix_paypal_merchant_patterns.sql.
    [InlineData("011049186667611/PP.7563.PP/. Humble B02undle, Inc., Ihr Einkauf bei Humble03 Bundle, Inc.", 202)]
    [InlineData("011049226541158/PP.7563.PP/. CinemaxX Entertainment GmbH", 203)]
    [InlineData("01KFC MUELHEIM A.D.RUHR, MUELHEIM  DE", 210)]
    [InlineData("01SumUp  *miamamia am Park, Essen  DE", 211)]
    [InlineData("01congstar Kundennummer 2202729614 Rechnung 7648284161", 220)]
    [InlineData("01FirmenRente L114945112 01.03.2026", 230)]
    [InlineData("01- Bochum-Gelsenkirchener Strassenbahnen AG. Buchungen vom", 240)]
    [InlineData("01Breker OHG - Storchen Apotheke//Bochum", 250)]
    [InlineData("011049206690082/PP.7563.PP/. Headline02 - Noth, Schneider + Verseck GbR", 260)]
    public void Neue_Freitext_Regeln_aus_echten_Umsatzdaten_werden_erkannt(string bookingText, long expectedCategoryId)
    {
        var transaction = MakeTransaction(-10m, bookingText: bookingText);

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: BuildCurrentCategorizationRules(),
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.Equal(expectedCategoryId, result);
    }

    [Fact]
    public void Generischer_SumUp_Praefix_allein_matched_die_Restaurant_Regel_nicht()
    {
        // Bewusste Design-Entscheidung: "SumUp" ist ein generischer Kartenterminal-Anbieter
        // vieler unabhängiger Händler - als Muster würde er fremde SumUp-Händler fälschlich
        // in dieselbe Kategorie stecken. Die Regel matcht daher gezielt "miamamia" (den
        // konkreten Café-Namen), nicht den SumUp-Präfix.
        var transaction = MakeTransaction(-5m, bookingText: "01SumUp  *Anderer Haendler XY, Berlin  DE");

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: BuildCurrentCategorizationRules(),
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.NotEqual(211, result);
    }

    [Fact]
    public void Regel_auf_CounterpartyName_erkennt_Ueberweisung_ohne_Haendlernamen_im_Buchungstext()
    {
        // Motivation: echte Überweisungen haben oft nur den Verwendungszweck im Buchungstext,
        // nicht den Empfänger-Namen - der liegt nur strukturiert vor (transaction.CounterpartyName,
        // aus comdirects remitter/deptor/creditor.holderName).
        var transaction = MakeTransaction(-25m, bookingText: "Rechnung 12345", counterpartyName: "Musterfirma GmbH");
        var rules = new List<CategorizationRule>
        {
            new() { Pattern = "Musterfirma", MatchField = RuleMatchField.CounterpartyName, CategoryId = 99, Priority = 50 },
        };

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: rules,
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.Equal(99, result);
    }

    [Fact]
    public void Regel_auf_CounterpartyName_matched_nicht_wenn_Feld_leer_ist()
    {
        var transaction = MakeTransaction(-25m, bookingText: "Kartenzahlung ohne strukturierten Empfänger");
        var rules = new List<CategorizationRule>
        {
            new() { Pattern = "Musterfirma", MatchField = RuleMatchField.CounterpartyName, CategoryId = 99, Priority = 50 },
        };

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: rules,
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.Equal(2, result);
    }

    [Fact]
    public void Nicht_identifizierbarer_Haendler_faellt_in_Vorzeichen_Fallback()
    {
        // Bewusst keine Regel für diese generische Rechnungs-/Mahnungsformulierung angelegt
        // (Händler aus dem Text nicht zuverlässig erkennbar) - lieber im Fallback landen als raten.
        var transaction = MakeTransaction(-9.99m, bookingText: "01Kd.1209379067 Wir sagen Danke. RG-Nr.M26020430747 9,99 EUR");

        var result = CategorizationLogic.Categorize(
            transaction, ownIbans: [], internalCategoryId: null, rulesByPriority: BuildCurrentCategorizationRules(),
            fallbackIncomeCategoryId: 1, fallbackExpenseCategoryId: 2);

        Assert.Equal(2, result);
    }
}
