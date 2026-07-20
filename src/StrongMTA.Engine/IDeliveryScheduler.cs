namespace StrongMTA.Engine;

/// <summary>
/// Abstração mínima sobre "para onde vai um item pronto para entrega" — implementada por
/// <see cref="FairShareDeliveryScheduler"/> em produção. Existe principalmente para permitir
/// testar <see cref="SubmissionService"/>/<see cref="SpoolBootRecovery"/>/<see cref="RetryScheduler"/>
/// sem disparar entregas reais (um duplo de teste pode só capturar os itens enfileirados).
/// </summary>
public interface IDeliveryScheduler
{
    void Enqueue(RecipientWorkItem item);
}
