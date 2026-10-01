using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheDiscordList.DSharpPlus;

/// <summary>Verifies the webhooks TheDiscordList sends. Needs no Discord client.</summary>
public static class TdlWebhook
{
    /// <summary><c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256&gt;</c>.</summary>
    public const string SignatureHeader = "X-TDL-Signature";

    /// <summary>The event name, also present in the body.</summary>
    public const string EventHeader = "X-TDL-Event";

    /// <summary>A uuid that is stable across every retry of one delivery: deduplicate on it.</summary>
    public const string DeliveryHeader = "X-TDL-Delivery";

    /// <summary>The 1-based attempt number.</summary>
    public const string AttemptHeader = "X-TDL-Attempt";

    /// <summary>How far <c>t</c> may sit from now, in either direction, by default.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

    /// <summary>Verifies a delivery and returns its parsed envelope.</summary>
    /// <param name="rawBody">The body exactly as received. Re-serialising a parsed body can never verify.</param>
    /// <param name="signatureHeader">The <c>X-TDL-Signature</c> header value.</param>
    /// <param name="secret">The endpoint's signing secret.</param>
    /// <param name="tolerance">The freshness window around <c>t</c>. Default 300 seconds.</param>
    /// <param name="timeProvider">The clock. Default <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="TdlWebhookException">Verification failed; <see cref="TdlWebhookException.Code"/> says why.</exception>
    public static TdlWebhookEvent Verify(string rawBody, string? signatureHeader, string secret, TimeSpan? tolerance = null, TimeProvider? timeProvider = null) =>
        Verify(Encoding.UTF8.GetBytes(rawBody), signatureHeader, secret, tolerance, timeProvider);

    /// <inheritdoc cref="Verify(string, string?, string, TimeSpan?, TimeProvider?)"/>
    public static TdlWebhookEvent Verify(ReadOnlySpan<byte> rawBody, string? signatureHeader, string secret, TimeSpan? tolerance = null, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            throw new TdlWebhookException(TdlWebhookErrorCode.MissingSignature, $"Missing {SignatureHeader} header.");
        }

        // Split on the first '=' only: a value may legitimately contain '='.
        string? timestampValue = null;
        string? signature = null;
        foreach (var part in signatureHeader.Split(','))
        {
            var separator = part.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key == "t")
            {
                timestampValue ??= value;
            }
            else if (key == "v1" && signature is null && value.Length > 0)
            {
                signature = value;
            }
        }

        if (signature is null || !long.TryParse(timestampValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var timestamp))
        {
            throw new TdlWebhookException(TdlWebhookErrorCode.MalformedSignature, $"Malformed {SignatureHeader} header.");
        }

        // Two-sided, so a sender clock skewed into the future is caught too. Checked against t,
        // never created_at: that is stamped at enqueue time and can be hours older on a retry.
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        if (Math.Abs(now - timestamp) > (tolerance ?? DefaultTolerance).TotalSeconds)
        {
            throw new TdlWebhookException(TdlWebhookErrorCode.StaleTimestamp, "Webhook timestamp outside the tolerance window.");
        }

        var expected = Encoding.UTF8.GetBytes(Sign(secret, timestamp, rawBody));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signature.ToLowerInvariant()), expected))
        {
            throw new TdlWebhookException(TdlWebhookErrorCode.InvalidSignature, "Webhook signature did not match.");
        }

        TdlWebhookEvent? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TdlWebhookEvent>(rawBody, TdlApiClient.Json);
        }
        catch (JsonException)
        {
            payload = null;
        }

        return payload is { Event: not null }
            ? payload
            : throw new TdlWebhookException(TdlWebhookErrorCode.MalformedBody, "Webhook body was not a valid envelope.");
    }

    private static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "."));
        hmac.AppendData(body);
        return Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>Why a webhook failed verification.</summary>
public enum TdlWebhookErrorCode
{
    /// <summary>No <c>X-TDL-Signature</c> header.</summary>
    MissingSignature,

    /// <summary>The header had no parsable <c>t</c> or no <c>v1</c>.</summary>
    MalformedSignature,

    /// <summary><c>t</c> is outside the tolerance window.</summary>
    StaleTimestamp,

    /// <summary>The HMAC did not match: wrong secret, or the body changed.</summary>
    InvalidSignature,

    /// <summary>The signature matched but the body is not a webhook envelope.</summary>
    MalformedBody,
}

/// <summary>A webhook that failed verification. Answer it with a 400.</summary>
public sealed class TdlWebhookException(TdlWebhookErrorCode code, string message) : Exception(message)
{
    /// <summary>The machine-readable reason.</summary>
    public TdlWebhookErrorCode Code { get; } = code;
}

/// <summary>The envelope every webhook shares. Unknown event names parse like any other.</summary>
/// <param name="Id">A uuid equal to <c>X-TDL-Delivery</c>, stable across retries.</param>
/// <param name="Event">The event name, for example <see cref="TdlWebhookEvents.ListingUpvoted"/>.</param>
/// <param name="CreatedAt">Enqueue time, not send time. Do not use it for freshness.</param>
/// <param name="Listing">The listing the event is about.</param>
/// <param name="Data">The event's own fields. Read them with <see cref="DataAs{T}"/>.</param>
public sealed record TdlWebhookEvent(string Id, string Event, DateTimeOffset CreatedAt, TdlWebhookListing Listing, JsonElement Data)
{
    /// <summary>Deserializes <see cref="Data"/>, for example into <see cref="TdlListingUpvoted"/>.</summary>
    /// <typeparam name="T">The data record for <see cref="Event"/>.</typeparam>
    /// <returns>The typed data.</returns>
    public T? DataAs<T>() => Data.Deserialize<T>(TdlApiClient.Json);
}

/// <summary>The listing a webhook is about.</summary>
/// <param name="Id">The guild id for a server, the application id for a bot.</param>
/// <param name="Type"><c>server</c> or <c>bot</c>.</param>
/// <param name="Name">The listing's name.</param>
/// <param name="Url">The public listing URL.</param>
public sealed record TdlWebhookListing(string Id, string Type, string Name, string Url);

/// <summary>The user behind an event.</summary>
/// <param name="Id">The Discord user id. Nullable even inside a present user.</param>
/// <param name="Username">Snapshotted at last sign-in, not a live Discord handle.</param>
/// <param name="Avatar">A full CDN URL.</param>
public sealed record TdlWebhookUser(string? Id, string Username, string? Avatar);

/// <summary><c>listing.bumped</c> data.</summary>
/// <param name="Points">Points awarded.</param>
/// <param name="BumpedAt">When the bump happened.</param>
/// <param name="NextBumpAt">When the listing can be bumped again.</param>
/// <param name="User">Who bumped it, when known.</param>
public sealed record TdlListingBumped(int Points, DateTimeOffset BumpedAt, DateTimeOffset NextBumpAt, TdlWebhookUser? User);

/// <summary><c>listing.upvoted</c> data.</summary>
/// <param name="VotedAt">When the vote happened.</param>
/// <param name="TotalUpvotes">The listing's new total.</param>
/// <param name="User">Who voted, when known.</param>
public sealed record TdlListingUpvoted(DateTimeOffset VotedAt, int TotalUpvotes, TdlWebhookUser? User);

/// <summary><c>server.joined</c> data. Server listings only; a click, not a confirmed join.</summary>
/// <param name="JoinedAt">When the join hand-off happened.</param>
/// <param name="User">Who clicked, when known.</param>
public sealed record TdlServerJoined(DateTimeOffset JoinedAt, TdlWebhookUser? User);

/// <summary><c>bot.added</c> data. A click on the install hand-off; Discord never confirms completion.</summary>
/// <param name="AddedAt">When the hand-off happened.</param>
/// <param name="User">Who clicked, when known.</param>
public sealed record TdlBotAdded(DateTimeOffset AddedAt, TdlWebhookUser? User);

/// <summary><c>bot.stats_reported</c> data. Also fires on manual edits, not only on your own posts.</summary>
/// <param name="GuildCount">The stored server count.</param>
/// <param name="ShardCount">The stored shard count.</param>
/// <param name="ReportedAt">When it was stored.</param>
public sealed record TdlBotStatsReported(int? GuildCount, int? ShardCount, DateTimeOffset ReportedAt);

/// <summary><c>webhook.ping</c> data.</summary>
/// <param name="Message">A human-readable note.</param>
public sealed record TdlWebhookPing(string Message);

/// <summary>The event names sent today. Answer 2xx to names not listed here too.</summary>
public static class TdlWebhookEvents
{
    /// <summary>Data: <see cref="TdlListingBumped"/>.</summary>
    public const string ListingBumped = "listing.bumped";

    /// <summary>Data: <see cref="TdlListingUpvoted"/>.</summary>
    public const string ListingUpvoted = "listing.upvoted";

    /// <summary>Data: <see cref="TdlServerJoined"/>.</summary>
    public const string ServerJoined = "server.joined";

    /// <summary>Data: <see cref="TdlBotAdded"/>.</summary>
    public const string BotAdded = "bot.added";

    /// <summary>Data: <see cref="TdlBotStatsReported"/>.</summary>
    public const string BotStatsReported = "bot.stats_reported";

    /// <summary>Data: <see cref="TdlWebhookPing"/>.</summary>
    public const string Ping = "webhook.ping";
}
