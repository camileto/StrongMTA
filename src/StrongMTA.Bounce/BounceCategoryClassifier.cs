using System.Text.RegularExpressions;
using StrongMTA.Core;

namespace StrongMTA.Bounce;

/// <summary>
/// Classifica um bounce numa <see cref="BounceCategory"/> a partir do enhanced status code
/// (RFC 3463, ex. "5.1.1") e/ou do texto de diagnóstico. Regras avaliadas em ordem, primeira
/// que casar decide — a ordem importa porque alguns enhanced status codes (5.7.x) são
/// compartilhados entre política e spam. Lista de regras é injetável para permitir
/// customização sem recompilar.
/// </summary>
public sealed class BounceCategoryClassifier
{
    private static readonly (Regex Pattern, BounceCategory Category)[] DefaultRules =
    [
        (new Regex(@"^5\.1\.|^5\.2\.1\b|mailbox.*(unavailable|not found|unknown)|user unknown|no such user", RegexOptions.IgnoreCase), BounceCategory.BadMailbox),
        (new Regex(@"^5\.2\.2|^4\.2\.2|quota|mailbox full|over quota", RegexOptions.IgnoreCase), BounceCategory.QuotaIssues),
        (new Regex(@"spam|blocked|blacklist|reputation|reject.*content", RegexOptions.IgnoreCase), BounceCategory.SpamRelated),
        (new Regex(@"^5\.7\.|policy|not authorized|relay denied", RegexOptions.IgnoreCase), BounceCategory.PolicyRelated),
        (new Regex(@"^4\.4\.|^4\.3\.|connection|timed out|timeout|temporarily unavailable", RegexOptions.IgnoreCase), BounceCategory.BadConnection),
    ];

    private readonly (Regex Pattern, BounceCategory Category)[] _rules;

    public BounceCategoryClassifier(IEnumerable<(Regex Pattern, BounceCategory Category)>? customRules = null)
    {
        _rules = customRules?.ToArray() ?? DefaultRules;
    }

    public BounceCategory Classify(string? enhancedStatusCode, string? diagnosticText)
    {
        var haystack = $"{enhancedStatusCode} {diagnosticText}";
        foreach (var (pattern, category) in _rules)
        {
            if (pattern.IsMatch(haystack))
                return category;
        }

        return BounceCategory.Other;
    }
}
