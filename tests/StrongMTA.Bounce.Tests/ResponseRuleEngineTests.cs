using System.Text.RegularExpressions;
using StrongMTA.Core;

namespace StrongMTA.Bounce.Tests;

public class ResponseRuleEngineTests
{
    private static ResponseRule Rule(string pattern, params ResponseRuleAction[] actions) => new()
    {
        Pattern = new Regex(pattern, RegexOptions.IgnoreCase),
        Actions = actions
    };

    [Fact]
    public void Evaluate_EmptyRuleList_ReturnsNull()
    {
        var engine = new ResponseRuleEngine();

        var result = engine.Evaluate([], "550 5.1.1 mailbox unavailable");

        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_NullResponseText_ReturnsNull()
    {
        var engine = new ResponseRuleEngine();
        var rules = new[] { Rule(".*", ResponseRuleAction.ForceBounce) };

        var result = engine.Evaluate(rules, null);

        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_NoRuleMatches_ReturnsNull()
    {
        var engine = new ResponseRuleEngine();
        var rules = new[] { Rule("quota exceeded", ResponseRuleAction.ForceBounce) };

        var result = engine.Evaluate(rules, "421 4.7.0 too many connections");

        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_FirstMatchingRuleWins_IgnoresLaterMatches()
    {
        var engine = new ResponseRuleEngine();
        var rules = new[]
        {
            Rule("too many connections", ResponseRuleAction.SkipMx),
            Rule("connections", ResponseRuleAction.ForceBounce)
        };

        var result = engine.Evaluate(rules, "421 4.7.0 too many connections");

        Assert.NotNull(result);
        Assert.Equal([ResponseRuleAction.SkipMx], result.Actions);
    }

    [Fact]
    public void Evaluate_RuleWithMultipleActions_ReturnsAllOfThem()
    {
        var engine = new ResponseRuleEngine();
        var rules = new[] { Rule("temporarily blocked", ResponseRuleAction.EnterBackoff, ResponseRuleAction.DisableSourceIp) };

        var result = engine.Evaluate(rules, "451 4.7.1 temporarily blocked due to reputation");

        Assert.NotNull(result);
        Assert.True(result.Has(ResponseRuleAction.EnterBackoff));
        Assert.True(result.Has(ResponseRuleAction.DisableSourceIp));
        Assert.False(result.Has(ResponseRuleAction.ForceBounce));
    }
}
