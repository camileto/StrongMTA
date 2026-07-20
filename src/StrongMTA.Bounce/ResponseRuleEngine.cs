using StrongMTA.Core;

namespace StrongMTA.Bounce;

/// <summary>
/// Avalia uma lista ordenada de <see cref="ResponseRule"/> contra o texto de uma resposta
/// SMTP ou diagnóstico de DSN — primeira regra que casa vale, igual ao
/// <c>&lt;smtp-pattern-list&gt;</c> do PowerMTA. Sem estado, sem dependências: lista vazia ou
/// nenhum match retornam null, preservando integralmente o comportamento default de quem
/// chama (este é o requisito central: zero regras configuradas = zero mudança de comportamento).
/// </summary>
public sealed class ResponseRuleEngine
{
    public ResponseRule? Evaluate(IReadOnlyList<ResponseRule> rules, string? responseText)
    {
        if (responseText is null)
            return null;

        foreach (var rule in rules)
        {
            if (rule.Pattern.IsMatch(responseText))
                return rule;
        }

        return null;
    }
}
