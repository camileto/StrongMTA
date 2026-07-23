using StrongMTA.Smtp.Client;

namespace StrongMTA.Smtp.Client.Tests;

/// <summary>
/// Testes unitários de <see cref="MtaStsResolver.ParsePolicy"/> e <see cref="MtaStsPolicy.MxMatches"/>.
/// Sem rede — só lógica de parse e matching.
/// </summary>
public class MtaStsPolicyTests
{
    [Fact]
    public void ParsePolicy_EnforceMode_ParsedCorrectly()
    {
        var text = """
            version: STSv1
            mode: enforce
            mx: mail.example.com
            mx: *.example.net
            max_age: 86400
            """;

        var policy = MtaStsResolver.ParsePolicy(text);

        Assert.Equal(MtaStsPolicyMode.Enforce, policy.Mode);
        Assert.Equal(["mail.example.com", "*.example.net"], policy.MxPatterns);
        Assert.Equal(86400, policy.MaxAge);
    }

    [Fact]
    public void ParsePolicy_TestingMode_ParsedCorrectly()
    {
        var text = "version: STSv1\nmode: testing\nmx: mx.example.com\nmax_age: 604800\n";
        var policy = MtaStsResolver.ParsePolicy(text);

        Assert.Equal(MtaStsPolicyMode.Testing, policy.Mode);
    }

    [Fact]
    public void ParsePolicy_UnknownMode_ReturnsNone()
    {
        var text = "version: STSv1\nmode: report\nmx: mx.example.com\nmax_age: 300\n";
        var policy = MtaStsResolver.ParsePolicy(text);
        Assert.Equal(MtaStsPolicyMode.None, policy.Mode);
    }

    [Fact]
    public void ParsePolicy_NoMode_ReturnsNone()
    {
        var text = "version: STSv1\nmx: mx.example.com\nmax_age: 300\n";
        var policy = MtaStsResolver.ParsePolicy(text);
        Assert.Same(MtaStsPolicy.None, policy);
    }

    [Fact]
    public void MxMatches_ExactMatch_ReturnsTrue()
    {
        var policy = new MtaStsPolicy
        {
            Mode = MtaStsPolicyMode.Enforce,
            MxPatterns = ["mail.example.com"]
        };

        Assert.True(policy.MxMatches("mail.example.com"));
        Assert.True(policy.MxMatches("MAIL.EXAMPLE.COM")); // case-insensitive
        Assert.False(policy.MxMatches("smtp.example.com"));
    }

    [Fact]
    public void MxMatches_WildcardPrefix_MatchesSingleLabel()
    {
        var policy = new MtaStsPolicy
        {
            Mode = MtaStsPolicyMode.Enforce,
            MxPatterns = ["*.example.com"]
        };

        Assert.True(policy.MxMatches("mail.example.com"));
        Assert.True(policy.MxMatches("smtp.example.com"));
        Assert.False(policy.MxMatches("example.com"));           // wildcard exige pelo menos um label de prefixo
        Assert.False(policy.MxMatches("sub.mail.example.com")); // wildcard não casa com dois labels
        Assert.False(policy.MxMatches("mail.example.net"));
    }

    [Fact]
    public void MxMatches_NoPatterns_ReturnsFalse()
    {
        var policy = new MtaStsPolicy { Mode = MtaStsPolicyMode.Enforce, MxPatterns = [] };
        Assert.False(policy.MxMatches("mail.example.com"));
    }
}
