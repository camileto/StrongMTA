using System.Text;

namespace StrongMTA.Smtp.Client.Tests;

public class SmtpDeliveryClientTests
{
    private static SmtpDeliveryRequest CreateRequest(int port, string body = "Subject: teste\r\n\r\nOla mundo.\r\n") => new()
    {
        TargetHost = "127.0.0.1",
        TargetPort = port,
        HeloHostName = "client.test",
        EnvelopeFrom = "bounce-abc123@strongmta.test",
        RecipientAddress = "destino@fake.test",
        OpenBodyStream = _ => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes(body))),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        CommandTimeout = TimeSpan.FromSeconds(5),
        DataTimeout = TimeSpan.FromSeconds(5)
    };

    [Fact]
    public async Task SendAsync_FullSuccessfulFlow_ReturnsDelivered()
    {
        await using var server = new FakeSmtpServer();
        server.Start();

        var result = await new SmtpDeliveryClient().SendAsync(CreateRequest(server.Port));

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal(250, result.SmtpCode);
        Assert.False(result.UsedStartTls);
    }

    [Fact]
    public async Task SendAsync_RcptRejectedWith5xx_ReturnsBounced()
    {
        await using var server = new FakeSmtpServer { RcptToResponse = "550 5.1.1 mailbox unavailable\r\n" };
        server.Start();

        var result = await new SmtpDeliveryClient().SendAsync(CreateRequest(server.Port));

        Assert.Equal(SmtpDeliveryOutcome.Bounced, result.Outcome);
        Assert.Equal(550, result.SmtpCode);
    }

    [Fact]
    public async Task SendAsync_FinalResponseIs4xx_ReturnsTransient()
    {
        await using var server = new FakeSmtpServer { FinalResponse = "451 4.3.0 temporary failure\r\n" };
        server.Start();

        var result = await new SmtpDeliveryClient().SendAsync(CreateRequest(server.Port));

        Assert.Equal(SmtpDeliveryOutcome.Transient, result.Outcome);
        Assert.Equal(451, result.SmtpCode);
    }

    [Fact]
    public async Task SendAsync_MailFromRejected_AbortsBeforeRcptAndData()
    {
        await using var server = new FakeSmtpServer { MailFromResponse = "550 5.7.1 sender blocked\r\n" };
        server.Start();

        var result = await new SmtpDeliveryClient().SendAsync(CreateRequest(server.Port));

        Assert.Equal(SmtpDeliveryOutcome.Bounced, result.Outcome);
        Assert.Equal(550, result.SmtpCode);
    }

    [Fact]
    public async Task SendAsync_ConnectionRefused_ReturnsConnectionFailed()
    {
        // porta sem listener: conexão deve ser recusada (mesma máquina, loopback)
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var freePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var result = await new SmtpDeliveryClient().SendAsync(CreateRequest(freePort));

        Assert.Equal(SmtpDeliveryOutcome.ConnectionFailed, result.Outcome);
        Assert.NotNull(result.ErrorDetail);
    }

    [Fact]
    public async Task SendAsync_WithStartTlsSupported_UpgradesAndDelivers()
    {
        await using var server = new FakeSmtpServer { SupportsStartTls = true };
        server.Start();

        var result = await new SmtpDeliveryClient().SendAsync(CreateRequest(server.Port));

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);
        Assert.True(result.UsedStartTls);
    }

    [Fact]
    public async Task SendAsync_BodyWithLeadingDotLine_IsDotStuffedAndServerReceivesOriginalContent()
    {
        await using var server = new FakeSmtpServer();
        server.Start();

        var body = "Subject: teste\r\n\r\n.linha comecando com ponto\r\nlinha normal\r\n";
        var result = await new SmtpDeliveryClient().SendAsync(CreateRequest(server.Port, body));

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);
        Assert.NotNull(server.ReceivedDataBody);
        var received = Encoding.ASCII.GetString(server.ReceivedDataBody!);
        // o servidor deve ter recebido a linha com o "." duplicado (stuffing), conforme RFC 5321
        Assert.Contains("\r\n..linha comecando com ponto\r\n", received);
    }
}
