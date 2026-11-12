using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace TheDiscordList.DSharpPlus.Tests;

/// <summary>Every SDK runs the same fixture, so a shared default cannot drift in one of them.</summary>
public sealed class ConformanceTests
{
    private static TimeSpan Seconds(JsonElement parent, string name) => TimeSpan.FromSeconds(parent.GetProperty(name).GetInt32());

    [Fact]
    public void MatchesTheSharedFixture()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "sdk-conformance.json")));

        var signature = fixture.RootElement.GetProperty("webhook_signature");
        var secret = signature.GetProperty("secret").GetString()!;
        Assert.Equal(Seconds(signature, "tolerance_seconds"), TdlWebhook.DefaultTolerance);
        foreach (var vector in signature.GetProperty("vectors").EnumerateArray())
        {
            var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(vector.GetProperty("now").GetInt64()));
            var body = vector.GetProperty("body").GetString()!;
            var header = vector.GetProperty("header").GetString();
            var verified = true;
            try
            {
                TdlWebhook.Verify(body, header, secret, timeProvider: clock);
            }
            catch (TdlWebhookException)
            {
                verified = false;
            }

            Assert.True(vector.GetProperty("valid").GetBoolean() == verified, vector.GetProperty("name").GetString());
        }

        var defaults = fixture.RootElement.GetProperty("defaults");
        Assert.Equal(TdlOptions.DefaultBaseUrl, defaults.GetProperty("base_url").GetString());
        Assert.Equal(Seconds(defaults, "timeout_seconds"), TdlApiClient.Timeout);
        Assert.Equal(Seconds(defaults, "forbidden_reprobe_seconds"), Publisher.PlanReprobe);

        var retry = defaults.GetProperty("retry");
        Assert.Equal(Publisher.MaxAttempts, retry.GetProperty("attempts").GetInt32());
        Assert.Equal(Seconds(retry, "backoff_base_seconds"), Publisher.BackoffBase);
        Assert.Equal(Seconds(retry, "backoff_max_seconds"), Publisher.BackoffMax);

        var rateLimit = defaults.GetProperty("rate_limit");
        Assert.Equal(Seconds(rateLimit, "default_seconds"), TdlApiClient.DefaultRetryAfter);
        Assert.Equal(Seconds(rateLimit, "max_seconds"), TdlApiClient.MaxSuspension);
        Assert.Equal(Seconds(rateLimit, "jitter_max_seconds"), TdlApiClient.RetryJitter);

        var stats = defaults.GetProperty("stats");
        var statsOptions = new TdlStatsOptions();
        Assert.Equal(Seconds(stats, "first_delay_seconds"), StatsPublisher.FirstDelay);
        Assert.Equal(Seconds(stats, "interval_seconds"), statsOptions.Interval);
        Assert.Equal(Seconds(stats, "debounce_seconds"), statsOptions.Debounce);
        Assert.Equal(Seconds(stats, "min_gap_seconds"), statsOptions.MinGap);
        Assert.Equal(Seconds(stats, "force_after_seconds"), StatsPublisher.ForceAfter);

        var heartbeat = defaults.GetProperty("heartbeat");
        Assert.Equal(Seconds(heartbeat, "interval_seconds"), new TdlHeartbeatOptions().Interval);
        Assert.Equal(Seconds(heartbeat, "jitter_max_seconds"), TimeSpan.FromMilliseconds(HeartbeatPublisher.JitterMilliseconds));

        var commands = defaults.GetProperty("commands");
        Assert.Equal(Seconds(commands, "first_delay_seconds"), CommandsPublisher.Delay);
        Assert.Equal(Seconds(commands, "retry_seconds"), CommandsPublisher.RetryDelay);

        // The library never reports "resuming": it does not say when a reconnect is a resume.
        var states = fixture.RootElement.GetProperty("shard_states").EnumerateArray().Select(state => state.GetString()).ToList();
        Assert.Equal(states.Where(state => state != "resuming"), new[] { TdlShard.Ready, TdlShard.Connecting, TdlShard.Disconnected });
    }
}
