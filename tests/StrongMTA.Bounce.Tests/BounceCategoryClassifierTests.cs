using StrongMTA.Core;

namespace StrongMTA.Bounce.Tests;

public class BounceCategoryClassifierTests
{
    private readonly BounceCategoryClassifier _classifier = new();

    [Theory]
    [InlineData("5.1.1", "smtp; 550 5.1.1 User unknown", BounceCategory.BadMailbox)]
    [InlineData("5.2.2", "smtp; 552 5.2.2 mailbox full", BounceCategory.QuotaIssues)]
    [InlineData("5.7.1", "smtp; 550 5.7.1 message rejected as spam", BounceCategory.SpamRelated)]
    [InlineData("5.7.1", "smtp; 550 5.7.1 relay denied", BounceCategory.PolicyRelated)]
    [InlineData("4.4.7", "smtp; 451 4.4.7 connection timed out", BounceCategory.BadConnection)]
    [InlineData("5.5.5", "smtp; 500 something completely unrelated", BounceCategory.Other)]
    public void Classify_KnownPatterns_ReturnsExpectedCategory(string status, string diagnostic, BounceCategory expected)
    {
        var result = _classifier.Classify(status, diagnostic);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Classify_NullInputs_ReturnsOther()
    {
        Assert.Equal(BounceCategory.Other, _classifier.Classify(null, null));
    }

    [Fact]
    public void Classify_CustomRules_OverrideDefaults()
    {
        var customClassifier = new BounceCategoryClassifier([
            (new System.Text.RegularExpressions.Regex("minha-regra-customizada"), BounceCategory.PolicyRelated)
        ]);

        // o status "5.1.1" bateria em BadMailbox nas regras default, mas as regras customizadas não o reconhecem
        Assert.Equal(BounceCategory.Other, customClassifier.Classify("5.1.1", "user unknown"));
        Assert.Equal(BounceCategory.PolicyRelated, customClassifier.Classify(null, "minha-regra-customizada"));
    }
}
