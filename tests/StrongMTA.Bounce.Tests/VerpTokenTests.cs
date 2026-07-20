namespace StrongMTA.Bounce.Tests;

public class VerpTokenTests
{
    [Fact]
    public void Format_ThenTryExtractRecipientId_RoundTrips()
    {
        var recipientId = Guid.NewGuid();

        var address = VerpToken.Format(recipientId, "bounce.strongmta.test");
        var extracted = VerpToken.TryExtractRecipientId(address, out var roundTripped);

        Assert.True(extracted);
        Assert.Equal(recipientId, roundTripped);
        Assert.Equal($"bounce-{recipientId:N}@bounce.strongmta.test", address);
    }

    [Fact]
    public void TryExtractRecipientId_AcceptsRfc822HeaderValueWithAngleBracketsAndDisplayName()
    {
        var recipientId = Guid.NewGuid();
        var headerValue = $"\"Mailer Daemon\" <bounce-{recipientId:N}@bouncedomain.test>";

        var extracted = VerpToken.TryExtractRecipientId(headerValue, out var result);

        Assert.True(extracted);
        Assert.Equal(recipientId, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("fbl@bouncedomain.test")] // sem prefixo bounce-
    [InlineData("bounce-not-a-guid@bouncedomain.test")]
    public void TryExtractRecipientId_InvalidInput_ReturnsFalse(string? input)
    {
        var extracted = VerpToken.TryExtractRecipientId(input, out _);

        Assert.False(extracted);
    }
}
