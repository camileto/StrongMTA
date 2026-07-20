using System.Collections.Concurrent;

namespace StrongMTA.Spool;

/// <summary>
/// Atualiza o estado de UM destinatário dentro do .state compartilhado da mensagem.
/// Como o .state é um único arquivo por mensagem (não por destinatário — ver layout do
/// spool), destinatários da MESMA mensagem entregues concorrentemente por workers
/// diferentes precisam serializar a leitura-modificação-escrita; um lock por MessageId
/// faz isso. Nota: o dicionário de locks cresce ao longo da vida do processo (uma entrada
/// por MessageId já visto) — aceitável no MVP, fase 2 pode trocar por um pool com limpeza.
/// </summary>
public sealed class SpoolStateUpdater(SpoolPaths paths, SpoolReader reader, SpoolWriter writer)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task UpdateRecipientAsync(Guid messageId, Guid recipientId, Action<RecipientStateData> mutate, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(messageId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var statePath = paths.GetStateFilePath(messageId);
            var state = await reader.ReadStateAsync(statePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Estado ausente para a mensagem {messageId}.");

            var recipient = state.Recipients.FirstOrDefault(r => r.RecipientId == recipientId)
                ?? throw new InvalidOperationException($"Destinatário {recipientId} não encontrado no estado da mensagem {messageId}.");

            mutate(recipient);

            await writer.WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
