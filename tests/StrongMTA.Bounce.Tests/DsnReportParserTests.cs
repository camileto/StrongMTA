using System.Text;
using MimeKit;

namespace StrongMTA.Bounce.Tests;

public class DsnReportParserTests
{
    private static MimeMessage LoadDsn(string action, string status, string diagnosticCode, string finalRecipient = "rfc822; recipient1@example.com")
    {
        var raw =
            "From: Mail Delivery Subsystem <mailer-daemon@mailhost1.example.com>\r\n" +
            "To: <bounce-aabbccdd00112233445566778899aabb@bouncedomain.test>\r\n" +
            "Subject: Delivery Status Notification (Failure)\r\n" +
            "Content-Type: multipart/report; report-type=delivery-status; boundary=\"B1\"\r\n" +
            "MIME-Version: 1.0\r\n\r\n" +
            "--B1\r\nContent-Type: text/plain\r\n\r\nYour message could not be delivered.\r\n\r\n" +
            "--B1\r\nContent-Type: message/delivery-status\r\n\r\n" +
            "Reporting-MTA: dns; mailhost1.example.com\r\n" +
            "Arrival-Date: Wed, 28 Jul 2021 09:14:35 -0700\r\n\r\n" +
            $"Final-Recipient: {finalRecipient}\r\n" +
            $"Action: {action}\r\n" +
            $"Status: {status}\r\n" +
            $"Diagnostic-Code: {diagnosticCode}\r\n" +
            "Last-Attempt-Date: Wed, 28 Jul 2021 09:15:00 -0700\r\n\r\n" +
            "--B1\r\nContent-Type: message/rfc822\r\n\r\nFrom: x\r\nTo: y\r\nSubject: original\r\n\r\ncorpo\r\n" +
            "--B1--\r\n";
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
    }

    [Fact]
    public void TryParse_FailedDsn_ExtractsAllFields()
    {
        var message = LoadDsn("failed", "5.1.1", "smtp; 550 5.1.1 User unknown");

        var result = DsnReportParser.TryParse(message);

        Assert.NotNull(result);
        Assert.Equal("failed", result!.Action);
        Assert.Equal("5.1.1", result.Status);
        Assert.Equal("smtp; 550 5.1.1 User unknown", result.DiagnosticCode);
        Assert.Equal("rfc822; recipient1@example.com", result.FinalRecipient);
    }

    [Fact]
    public void TryParse_DelayedDsn_ActionIsDelayed()
    {
        var message = LoadDsn("delayed", "4.4.7", "smtp; 451 4.4.7 timeout");

        var result = DsnReportParser.TryParse(message);

        Assert.Equal("delayed", result!.Action);
    }

    [Fact]
    public void TryParse_ActionIsCaseInsensitive_NormalizedToLowercase()
    {
        var message = LoadDsn("FAILED", "5.1.1", "smtp; 550 user unknown");

        var result = DsnReportParser.TryParse(message);

        Assert.Equal("failed", result!.Action);
    }

    [Fact]
    public void TryParse_NotAMultipartReport_ReturnsNull()
    {
        var plain = "From: a@b.com\r\nTo: c@d.com\r\nSubject: not a dsn\r\n\r\ncorpo normal\r\n";
        var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(plain)));

        var result = DsnReportParser.TryParse(message);

        Assert.Null(result);
    }
}
