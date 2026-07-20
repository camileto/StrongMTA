using System.Text;
using MimeKit;

namespace StrongMTA.Bounce.Tests;

public class ArfReportParserTests
{
    private static MimeMessage LoadArf(string feedbackType, string originalMailFrom, string originalRcptTo = "<user@example.com>")
    {
        var raw =
            "From: <abuse@example.com>\r\n" +
            "To: <fbl@bouncedomain.test>\r\n" +
            "Subject: FW: complaint\r\n" +
            "Content-Type: multipart/report; report-type=feedback-report; boundary=\"B2\"\r\n" +
            "MIME-Version: 1.0\r\n\r\n" +
            "--B2\r\nContent-Type: text/plain\r\n\r\nThis is an email abuse report.\r\n\r\n" +
            "--B2\r\nContent-Type: message/feedback-report\r\n\r\n" +
            $"Feedback-Type: {feedbackType}\r\n" +
            "User-Agent: SomeGenerator/1.0\r\n" +
            "Version: 1\r\n" +
            $"Original-Mail-From: {originalMailFrom}\r\n" +
            $"Original-Rcpt-To: {originalRcptTo}\r\n\r\n" +
            "--B2\r\nContent-Type: message/rfc822\r\n\r\nFrom: x\r\nTo: y\r\nSubject: original\r\n\r\ncorpo\r\n" +
            "--B2--\r\n";
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
    }

    [Fact]
    public void TryParse_AbuseReport_ExtractsAllFields()
    {
        var message = LoadArf("abuse", "<bounce-aabbccdd00112233445566778899aabb@bouncedomain.test>");

        var result = ArfReportParser.TryParse(message);

        Assert.NotNull(result);
        Assert.Equal("abuse", result!.FeedbackType);
        Assert.Equal("<bounce-aabbccdd00112233445566778899aabb@bouncedomain.test>", result.OriginalMailFrom);
        Assert.Equal("<user@example.com>", result.OriginalRcptTo);
    }

    [Fact]
    public void TryParse_FeedbackTypeIsCaseInsensitive_NormalizedToLowercase()
    {
        var message = LoadArf("ABUSE", "<bounce-x@bouncedomain.test>");

        var result = ArfReportParser.TryParse(message);

        Assert.Equal("abuse", result!.FeedbackType);
    }

    [Fact]
    public void TryParse_NotAFeedbackReport_ReturnsNull()
    {
        var plain = "From: a@b.com\r\nTo: c@d.com\r\nSubject: not arf\r\n\r\ncorpo normal\r\n";
        var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(plain)));

        var result = ArfReportParser.TryParse(message);

        Assert.Null(result);
    }
}
