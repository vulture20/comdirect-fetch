using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

public class CategorizationLogicTests
{
    private static Transaction MakeTransaction(decimal amount, string? bookingText = null, string? transactionType = null) => new()
    {
        AccountId = 1,
        ComdirectReference = "ref-1",
        BookingDate = new DateOnly(2026, 1, 1),
        Amount = amount,
        Currency = "EUR",
        BookingText = bookingText,
        TransactionType = transactionType,
        FirstSeenAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Erkennt_interne_Umbuchung_anhand_bekannter_Iban()
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
}
