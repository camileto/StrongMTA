namespace StrongMTA.Core;

public enum RecipientStatus
{
    Pending,
    InFlight,

    Delivered,

    /// <summary>Veredito explícito de permanente: 5xx imediato, DSN com Status 5.x.x, ou regra que force ForceBounce. Nunca por esgotamento de TTL.</summary>
    Bounced,

    Transient,
    Suppressed,

    /// <summary>Pausado administrativamente (CLI, por JobId) — não é terminal nem retryable até um resume explícito.</summary>
    Paused,

    /// <summary>TTL (BounceAfter) esgotado sem nunca receber um veredito explícito de permanente — decisão nossa de desistir, distinta de Bounced.</summary>
    Expired
}
