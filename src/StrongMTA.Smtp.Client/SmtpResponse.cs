namespace StrongMTA.Smtp.Client;

/// <summary>
/// Resposta SMTP (possivelmente multilinha) já parseada: código de 3 dígitos + linhas de texto.
/// </summary>
public sealed record SmtpResponse(int Code, IReadOnlyList<string> Lines)
{
    public string Text => string.Join(' ', Lines);

    public bool IsPositiveCompletion => Code is >= 200 and < 300;
    public bool IsPositiveIntermediate => Code is >= 300 and < 400; // ex: 354 antes do corpo do DATA
    public bool IsTransientNegative => Code is >= 400 and < 500;
    public bool IsPermanentNegative => Code is >= 500 and < 600;
}
