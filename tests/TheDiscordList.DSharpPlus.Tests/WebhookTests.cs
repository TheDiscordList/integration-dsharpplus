using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace TheDiscordList.DSharpPlus.Tests;

public sealed class WebhookTests
{
    private const string Secret = "whsec_test";
    private const long T = 1_700_000_000;
    private const string Body = """{"id":"d1","event":"webhook.ping","created_at":"2023-11-14T22:13:20+00:00","listing":{"id":"1","type":"bot","name":"Bot","url":"https://thediscordlist.com/bots/1"},"data":{"message":"hi"}}""";

    /// <summary>From the shared SPEC_VECTOR.txt, computed with the server's own PHP formula.</summary>
    private const string VectorV1 = "b6eeea31d04c3f86990dcbb706dfaf988c9ff2fc234316ae77e168831127dc59";

    private static FakeTimeProvider At(long seconds) => new(DateTimeOffset.FromUnixTimeSeconds(seconds));

    private static string Sign(string body, long t = T, string secret = Secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant();

    private static TdlWebhookErrorCode Fails(string body, string? header, long now = T, string secret = Secret) =>
        Assert.Throws<TdlWebhookException>(() => TdlWebhook.Verify(body, header, secret, timeProvider: At(now))).Code;

    [Fact]
    public void SharedVectorVerifies()
    {
        var v1 = Sign(Body);
        Assert.Equal(VectorV1, v1);

        var webhook = TdlWebhook.Verify(Body, $"t={T},v1={v1}", Secret, timeProvider: At(T));

        Assert.Equal("d1", webhook.Id);
        Assert.Equal(TdlWebhookEvents.Ping, webhook.Event);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(T), webhook.CreatedAt);
        Assert.Equal(new TdlWebhookListing("1", "bot", "Bot", "https://thediscordlist.com/bots/1"), webhook.Listing);
        Assert.Equal(new TdlWebhookPing("hi"), webhook.DataAs<TdlWebhookPing>());
    }

    [Fact]
    public void RawBytesVerify()
    {
        var webhook = TdlWebhook.Verify(Encoding.UTF8.GetBytes(Body), $"t={T},v1={VectorV1}", Secret, timeProvider: At(T));

        Assert.Equal(TdlWebhookEvents.Ping, webhook.Event);
    }

    [Fact]
    public void TamperedBodyFails() =>
        Assert.Equal(TdlWebhookErrorCode.InvalidSignature, Fails(Body.Replace("\"hi\"", "\"ho\"", StringComparison.Ordinal), $"t={T},v1={VectorV1}"));

    [Fact]
    public void WrongSecretFails() =>
        Assert.Equal(TdlWebhookErrorCode.InvalidSignature, Fails(Body, $"t={T},v1={VectorV1}", secret: "whsec_other"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySecretIsAConfigurationError(string secret) =>
        Assert.Equal("secret", Assert.Throws<ArgumentException>(() => TdlWebhook.Verify(Body, $"t={T},v1={VectorV1}", secret, timeProvider: At(T))).ParamName);

    [Theory]
    [InlineData(301)]
    [InlineData(-301)]
    public void StaleTimestampFailsInBothDirections(long skew) =>
        Assert.Equal(TdlWebhookErrorCode.StaleTimestamp, Fails(Body, $"t={T},v1={VectorV1}", now: T + skew));

    [Theory]
    [InlineData(300)]
    [InlineData(-300)]
    public void TimestampAtTheEdgeOfTheWindowPasses(long skew) =>
        Assert.Equal(TdlWebhookEvents.Ping, TdlWebhook.Verify(Body, $"t={T},v1={VectorV1}", Secret, timeProvider: At(T + skew)).Event);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingHeaderFails(string? header) =>
        Assert.Equal(TdlWebhookErrorCode.MissingSignature, Fails(Body, header));

    [Theory]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=" + VectorV1)]
    [InlineData("t=,v1=" + VectorV1)]
    [InlineData("v1=" + VectorV1)]
    [InlineData("t=1700000000")]
    [InlineData("t=1700000000,v1=")]
    public void MalformedHeaderFails(string header) =>
        Assert.Equal(TdlWebhookErrorCode.MalformedSignature, Fails(Body, header));

    [Fact]
    public void HeaderPartsSplitOnTheFirstEqualsSign()
    {
        Assert.Equal(TdlWebhookEvents.Ping, TdlWebhook.Verify(Body, $"t={T},v0=abc=def==,v1={VectorV1}", Secret, timeProvider: At(T)).Event);
        Assert.Equal(TdlWebhookErrorCode.InvalidSignature, Fails(Body, $"t={T},v1={VectorV1}=="));
    }

    [Fact]
    public void FirstNonEmptySignatureWins() =>
        Assert.Equal(TdlWebhookEvents.Ping, TdlWebhook.Verify(Body, $" t={T} , v1= , v1={VectorV1}, v1=deadbeef", Secret, timeProvider: At(T)).Event);

    [Fact]
    public void UppercaseHexVerifies() =>
        Assert.Equal(TdlWebhookEvents.Ping, TdlWebhook.Verify(Body, $"t={T},v1={VectorV1.ToUpperInvariant()}", Secret, timeProvider: At(T)).Event);

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"id":"d1"}""")]
    public void SignedButMalformedBodyFails(string body) =>
        Assert.Equal(TdlWebhookErrorCode.MalformedBody, Fails(body, $"t={T},v1={Sign(body)}"));

    [Fact]
    public void UnknownEventsParse()
    {
        const string body = """{"id":"d2","event":"listing.renamed","created_at":"2026-08-14T12:34:56+00:00","listing":{"id":"9","type":"server","name":"S","url":"https://thediscordlist.com/servers/9"},"data":{"from":"A","to":"B"}}""";

        var webhook = TdlWebhook.Verify(body, $"t={T},v1={Sign(body)}", Secret, timeProvider: At(T));

        Assert.Equal("listing.renamed", webhook.Event);
        Assert.Equal("B", webhook.Data.GetProperty("to").GetString());
    }

    [Fact]
    public void KnownEventDataIsTyped()
    {
        const string body = """{"id":"d3","event":"listing.upvoted","created_at":"2026-08-14T12:34:56+00:00","listing":{"id":"1","type":"bot","name":"Bot","url":"https://thediscordlist.com/bots/1"},"data":{"voted_at":"2026-08-14T12:34:56+00:00","total_upvotes":42,"user":{"id":"7","username":"ada","avatar":null}}}""";

        var data = TdlWebhook.Verify(body, $"t={T},v1={Sign(body)}", Secret, timeProvider: At(T)).DataAs<TdlListingUpvoted>();

        Assert.Equal(new TdlListingUpvoted(DateTimeOffset.Parse("2026-08-14T12:34:56+00:00", System.Globalization.CultureInfo.InvariantCulture), 42, new TdlWebhookUser("7", "ada", null)), data);
    }

    [Fact]
    public void BumpReadyDataIsTyped()
    {
        const string body = """{"id":"d4","event":"listing.bump_ready","created_at":"2026-08-14T12:34:56+00:00","listing":{"id":"9","type":"server","name":"S","url":"https://thediscordlist.com/servers/9"},"data":{"ready_at":"2026-08-14T12:34:56+00:00"}}""";

        var webhook = TdlWebhook.Verify(body, $"t={T},v1={Sign(body)}", Secret, timeProvider: At(T));

        Assert.Equal(TdlWebhookEvents.ListingBumpReady, webhook.Event);
        Assert.Equal(new TdlListingBumpReady(DateTimeOffset.Parse("2026-08-14T12:34:56+00:00", System.Globalization.CultureInfo.InvariantCulture)), webhook.DataAs<TdlListingBumpReady>());
    }
}
