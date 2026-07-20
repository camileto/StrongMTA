using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace StrongMTA.Smtp.Client;

/// <summary>
/// Motor de entrega outbound: conecta, faz EHLO/STARTTLS oportunista, envia
/// MAIL FROM/RCPT TO/DATA e classifica a resposta final em Delivered/Bounced/Transient.
/// Uma instância trata exatamente uma tentativa de entrega para um destinatário.
/// </summary>
public sealed class SmtpDeliveryClient
{
    public async Task<SmtpDeliveryResult> SendAsync(SmtpDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        Socket? socket = null;
        Stream? stream = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (request.LocalIpAddress is not null)
                socket.Bind(new IPEndPoint(request.LocalIpAddress, 0));

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(request.ConnectTimeout);
            await socket.ConnectAsync(request.TargetHost, request.TargetPort, connectCts.Token).ConfigureAwait(false);

            stream = new NetworkStream(socket, ownsSocket: true);
            var reader = new SmtpResponseReader(stream);
            var usedStartTls = false;

            using var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cmdCts.CancelAfter(request.CommandTimeout);

            var banner = await reader.ReadResponseAsync(cmdCts.Token).ConfigureAwait(false);
            if (!banner.IsPositiveCompletion)
                return Classify(banner, usedStartTls);

            var ehlo = await SendCommandAsync(stream, reader, $"EHLO {request.HeloHostName}", cmdCts.Token).ConfigureAwait(false);
            if (!ehlo.IsPositiveCompletion)
                return Classify(ehlo, usedStartTls);

            var supportsStartTls = ehlo.Lines.Any(l => l.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase));
            if (supportsStartTls)
            {
                var startTlsResponse = await SendCommandAsync(stream, reader, "STARTTLS", cmdCts.Token).ConfigureAwait(false);
                if (startTlsResponse.IsPositiveCompletion)
                {
                    try
                    {
                        var sslStream = new SslStream(stream, leaveInnerStreamOpen: false,
                            userCertificateValidationCallback: (_, _, _, _) => true); // oportunista: criptografa sem validar identidade (DANE/MTA-STS ficam para fase 2)
                        await sslStream.AuthenticateAsClientAsync(request.TargetHost).ConfigureAwait(false);
                        stream = sslStream;
                        reader = new SmtpResponseReader(stream);
                        usedStartTls = true;

                        var ehloAfterTls = await SendCommandAsync(stream, reader, $"EHLO {request.HeloHostName}", cmdCts.Token).ConfigureAwait(false);
                        if (!ehloAfterTls.IsPositiveCompletion)
                            return Classify(ehloAfterTls, usedStartTls);
                    }
                    catch (Exception ex) when (ex is IOException or AuthenticationException)
                    {
                        if (request.RequireStartTls)
                            return new SmtpDeliveryResult { Outcome = SmtpDeliveryOutcome.Transient, ErrorDetail = $"Falha no handshake STARTTLS: {ex.Message}" };
                        // oportunista: cai para texto puro na conexão original (stream/reader já são os de antes do upgrade)
                    }
                }
                else if (request.RequireStartTls)
                {
                    return new SmtpDeliveryResult { Outcome = SmtpDeliveryOutcome.Transient, ErrorDetail = "Servidor remoto rejeitou STARTTLS e a política exige TLS." };
                }
            }
            else if (request.RequireStartTls)
            {
                return new SmtpDeliveryResult { Outcome = SmtpDeliveryOutcome.Transient, ErrorDetail = "Servidor remoto não anuncia STARTTLS e a política exige TLS." };
            }

            var mailFrom = await SendCommandAsync(stream, reader, $"MAIL FROM:<{request.EnvelopeFrom}>", cmdCts.Token).ConfigureAwait(false);
            if (!mailFrom.IsPositiveCompletion)
                return Classify(mailFrom, usedStartTls);

            var rcptTo = await SendCommandAsync(stream, reader, $"RCPT TO:<{request.RecipientAddress}>", cmdCts.Token).ConfigureAwait(false);
            if (!rcptTo.IsPositiveCompletion)
                return Classify(rcptTo, usedStartTls);

            var dataStart = await SendCommandAsync(stream, reader, "DATA", cmdCts.Token).ConfigureAwait(false);
            if (!dataStart.IsPositiveIntermediate)
                return Classify(dataStart, usedStartTls);

            using var dataCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dataCts.CancelAfter(request.DataTimeout);
            await using (var body = await request.OpenBodyStream(dataCts.Token).ConfigureAwait(false))
            {
                await WriteDotStuffedAsync(body, stream, dataCts.Token).ConfigureAwait(false);
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n.\r\n"), dataCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(dataCts.Token).ConfigureAwait(false);

            var finalResponse = await reader.ReadResponseAsync(dataCts.Token).ConfigureAwait(false);

            await SendCommandAsync(stream, reader, "QUIT", cmdCts.Token).ConfigureAwait(false);

            return Classify(finalResponse, usedStartTls);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or AuthenticationException)
        {
            return SmtpDeliveryResult.ConnectionFailure(ex.Message);
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            socket?.Dispose();
        }
    }

    private static async Task<SmtpResponse> SendCommandAsync(Stream stream, SmtpResponseReader reader, string command, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SmtpDeliveryResult Classify(SmtpResponse response, bool usedStartTls)
    {
        var outcome = response.Code switch
        {
            >= 200 and < 300 => SmtpDeliveryOutcome.Delivered,
            >= 400 and < 500 => SmtpDeliveryOutcome.Transient,
            _ => SmtpDeliveryOutcome.Bounced
        };
        return SmtpDeliveryResult.FromResponse(outcome, response, usedStartTls);
    }

    /// <summary>
    /// Copia o corpo aplicando dot-stuffing (RFC 5321 §4.5.2): qualquer linha que comece
    /// com "." recebe um "." extra. Assume que o stream de origem já usa CRLF.
    /// </summary>
    internal static async Task WriteDotStuffedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var atStartOfLine = true;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (atStartOfLine && buffer[i] == (byte)'.')
                {
                    await destination.WriteAsync(buffer.AsMemory(start, i - start + 1), cancellationToken).ConfigureAwait(false);
                    await destination.WriteAsync(new byte[] { (byte)'.' }, cancellationToken).ConfigureAwait(false);
                    start = i + 1;
                }
                atStartOfLine = buffer[i] == (byte)'\n';
            }
            if (start < read)
                await destination.WriteAsync(buffer.AsMemory(start, read - start), cancellationToken).ConfigureAwait(false);
        }
    }
}
