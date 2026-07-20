using MimeKit;

namespace StrongMTA.Bounce;

public sealed class DsnParseResult
{
    /// <summary>"failed", "delayed", "delivered", "relayed" ou "expanded" (RFC 3464 §2.3.3), sempre em minúsculas.</summary>
    public required string Action { get; init; }
    public string? Status { get; init; }
    public string? DiagnosticCode { get; init; }
    public string? FinalRecipient { get; init; }
}

/// <summary>
/// Extrai os campos relevantes de um DSN (RFC 3464): a parte <c>message/delivery-status</c>
/// de um <c>multipart/report</c>. O primeiro grupo de campos (<c>StatusGroups[0]</c>) é
/// por-mensagem (Reporting-MTA etc.) — ignorado aqui; usamos apenas o primeiro grupo
/// por-destinatário (<c>StatusGroups[1]</c>), suficiente porque nosso uso real é sempre
/// 1 destinatário por mensagem original e, portanto, por DSN.
/// </summary>
public static class DsnReportParser
{
    public static DsnParseResult? TryParse(MimeMessage message)
    {
        var status = message.BodyParts.OfType<MessageDeliveryStatus>().FirstOrDefault();
        if (status is null || status.StatusGroups.Count < 2)
            return null;

        var perRecipient = status.StatusGroups[1];
        var action = perRecipient["Action"];
        if (action is null)
            return null;

        return new DsnParseResult
        {
            Action = action.Trim().ToLowerInvariant(),
            Status = perRecipient["Status"]?.Trim(),
            DiagnosticCode = perRecipient["Diagnostic-Code"]?.Trim(),
            FinalRecipient = perRecipient["Final-Recipient"]?.Trim()
        };
    }
}
