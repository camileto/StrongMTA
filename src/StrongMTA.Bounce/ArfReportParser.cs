using MimeKit;

namespace StrongMTA.Bounce;

public sealed class ArfParseResult
{
    /// <summary>"abuse", "fraud", "virus" etc. (RFC 5965 §4.1), sempre em minúsculas.</summary>
    public required string FeedbackType { get; init; }
    public string? OriginalMailFrom { get; init; }
    public string? OriginalRcptTo { get; init; }
}

/// <summary>
/// Extrai os campos relevantes de um ARF (RFC 5965): a parte <c>message/feedback-report</c>
/// de um <c>multipart/report; report-type=feedback-report</c>. <c>Original-Mail-From</c> é o
/// que nos permite correlacionar de volta ao destinatário original — é o nosso próprio
/// envelope VERP, ecoado pelo provedor dentro do relatório.
/// Nota: o JMRP da Hotmail/Outlook não segue este formato (usa cabeçalho proprietário
/// <c>X-HmXmrOriginalRecipient</c>) — fora do escopo do MVP, conforme já avaliado.
/// </summary>
public static class ArfReportParser
{
    public static ArfParseResult? TryParse(MimeMessage message)
    {
        var report = message.BodyParts.OfType<MessageFeedbackReport>().FirstOrDefault();
        if (report is null)
            return null;

        var feedbackType = report.Fields["Feedback-Type"];
        if (feedbackType is null)
            return null;

        return new ArfParseResult
        {
            FeedbackType = feedbackType.Trim().ToLowerInvariant(),
            OriginalMailFrom = report.Fields["Original-Mail-From"]?.Trim(),
            OriginalRcptTo = report.Fields["Original-Rcpt-To"]?.Trim()
        };
    }
}
