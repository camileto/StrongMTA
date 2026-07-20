namespace StrongMTA.Core;

/// <summary>
/// Teto global de entregas simultâneas em voo. Entrega SMTP é I/O-bound (sockets, não CPU) —
/// o multiplicador por núcleo é só um ponto de partida configurável, não uma tentativa de
/// medir/limitar uso real de CPU. Para referência: o manual do PowerMTA (§3.3.1.1
/// total-max-smtp-out) usa default de 1200 conexões simultâneas na licença Enterprise e
/// recomenda não passar de 7500 mesmo sem limite de licença — numa máquina de 12 núcleos,
/// núcleos × 100 bate exatamente no default Enterprise do PowerMTA.
/// </summary>
public sealed class SchedulerOptions
{
    public const int DefaultCoreMultiplier = 100;

    public required int GlobalMaxConcurrency { get; init; }

    public static SchedulerOptions CreateDefault(int? processorCount = null) => new()
    {
        GlobalMaxConcurrency = (processorCount ?? Environment.ProcessorCount) * DefaultCoreMultiplier
    };
}
