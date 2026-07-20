using System.Text.RegularExpressions;

namespace StrongMTA.Core;

/// <summary>
/// Ações que uma <see cref="ResponseRule"/> pode disparar quando seu padrão casa com a
/// resposta SMTP/DSN — equivalente ao ACTION-LIST do <c>&lt;smtp-pattern-list&gt;</c> do
/// PowerMTA. Várias ações podem coexistir na mesma regra (ex.: EnterBackoff + DisableSourceIp).
/// </summary>
public enum ResponseRuleAction
{
    /// <summary>Bounça o destinatário imediatamente, mesmo que a resposta fosse 4xx/transiente.</summary>
    ForceBounce,

    /// <summary>Marca como Expired imediatamente — usado raramente, simétrico ao ForceBounce.</summary>
    ForceExpire,

    /// <summary>Força retry/Transient, mesmo que a resposta fosse 5xx — ainda respeita o BounceAfter normal depois.</summary>
    ForceRetry,

    /// <summary>Tenta o próximo MX (na mesma tentativa) em vez de desistir do domínio por causa de um host específico.</summary>
    SkipMx,

    /// <summary>Coloca a fila (domínio × VirtualMta) em modo de backoff.</summary>
    EnterBackoff,

    /// <summary>Tira a fila do modo de backoff.</summary>
    ExitBackoff,

    /// <summary>Desabilita temporariamente o VirtualMta de origem.</summary>
    DisableSourceIp,

    /// <summary>Bounça todos os destinatários pendentes daquele domínio imediatamente.</summary>
    BounceQueue
}

/// <summary>
/// Uma regra de override sobre o texto de uma resposta SMTP ou diagnóstico de DSN —
/// equivalente a uma entrada <c>reply /PATTERN/ ACTION-LIST</c> do PowerMTA. Configurada
/// por domínio via <see cref="DomainConfig.ResponseRules"/>; lista vazia (o padrão) significa
/// nenhuma regra, e portanto nenhuma mudança no comportamento default de classificação.
/// </summary>
public sealed class ResponseRule
{
    public required Regex Pattern { get; init; }
    public required IReadOnlyList<ResponseRuleAction> Actions { get; init; }

    /// <summary>Usado só com EnterBackoff: tempo após o qual a fila volta a Normal automaticamente. Null = não reverte automaticamente.</summary>
    public TimeSpan? BackoffToNormalAfter { get; init; }

    /// <summary>Usado só com DisableSourceIp: tempo após o qual o VirtualMta volta a ser habilitado. Null = nunca (precisa de ação manual).</summary>
    public TimeSpan? ReenableAfter { get; init; }

    public bool Has(ResponseRuleAction action) => Actions.Contains(action);
}
