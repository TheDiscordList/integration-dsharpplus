using System.Net.Http;

namespace TheDiscordList.DSharpPlus.Tests;

public sealed class RequestShapingTests
{
    [Fact]
    public async Task StatsRequestCarriesTheContractHeadersAndBody()
    {
        await using var h = new Harness();
        h.Host.LocalServerCount = 42;

        var result = await h.Poster.PostStatsAsync();

        Assert.Equal(new TdlPostResult(true, 200), result);
        var sent = Assert.Single(h.Http.Sent);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("https://thediscordlist.com/api/bots/123/stats", sent.Url);
        Assert.Equal("Bearer tdl_token", sent.Authorization);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal($"TheDiscordList-DSharpPlus/{TdlAutoposter.Version} (+https://github.com/TheDiscordList/integration-dsharpplus)", sent.UserAgent);
        Assert.Equal("""{"server_count":42,"shard_count":1}""", sent.Body);
        Assert.Equal(new TdlPosted(TdlPublisherKind.Stats, 200), Assert.Single(h.Posted));
    }

    [Fact]
    public async Task BaseUrlIsConfigurable()
    {
        await using var h = new Harness(options => options.BaseUrl = "http://localhost:8080/api/");

        await h.Poster.PostStatsAsync();

        Assert.Equal("http://localhost:8080/api/bots/123/stats", Assert.Single(h.Http.Sent).Url);
    }

    [Fact]
    public async Task HeartbeatOmitsAnUnknownLatency()
    {
        await using var h = new Harness();
        h.Host.Topology = new(1, 3, 6, false);
        h.Host.Shards = [new(2, TdlShard.Ready, 87, 1500), new(3, TdlShard.Connecting, null, 0)];

        var result = await h.Poster.PostHeartbeatAsync();

        Assert.True(result.Ok);
        var sent = Assert.Single(h.Http.Sent);
        Assert.Equal("https://thediscordlist.com/api/bots/123/heartbeat", sent.Url);
        Assert.Equal(
            """{"cluster":1,"cluster_count":3,"shards":[{"id":2,"status":"ready","latency_ms":87,"guilds":1500},{"id":3,"status":"connecting","guilds":0}]}""",
            sent.Body);
    }

    [Fact]
    public async Task CommandsOmitAbsentOptionalFields()
    {
        await using var h = new Harness();
        h.Host.Commands =
        [
            new(1, "ping", "Replies with pong", false, null, null),
            new(2, "Inspect", null, null, null, null),
            new(1, "config", "Configure the bot", null, "32",
            [
                new(1, "set", "Set a value", null, null,
                [
                    new(3, "key", "Which key", true, [new("Prefix", "prefix"), new("Level", 3L)], null),
                ]),
            ]),
        ];

        await h.Poster.PostCommandsAsync();

        Assert.Equal(
            """{"commands":[{"type":1,"name":"ping","description":"Replies with pong","nsfw":false},{"type":2,"name":"Inspect"},"""
            + """{"type":1,"name":"config","description":"Configure the bot","default_member_permissions":"32","options":[{"type":1,"name":"set","description":"Set a value","options":"""
            + """[{"type":3,"name":"key","description":"Which key","required":true,"choices":[{"name":"Prefix","value":"prefix"},{"name":"Level","value":3}]}]}]}]}""",
            Assert.Single(h.Http.Sent).Body);
    }
}

public sealed class StatusHandlingTests
{
    [Fact]
    public async Task UnauthorizedDisablesEveryPublisherAndLogsOnce()
    {
        await using var h = new Harness();
        h.Http.Respond = _ => FakeHandler.Json(401, """{"message":"Unauthenticated."}""");

        h.Ready();
        await Harness.Until(() => h.Disabled.Count == 3);
        await h.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10));

        Assert.Single(h.Http.Sent);
        Assert.All(h.Disabled, disabled => Assert.Equal("unauthorized", disabled.Reason));
        Assert.Single(h.Host.Logs, log => log.Contains("401", StringComparison.Ordinal));

        h.Http.Respond = _ => FakeHandler.Json(200, "{}");
        Assert.True((await h.Poster.PostHeartbeatAsync()).Ok);
    }

    [Fact]
    public async Task PlanErrorSuspendsHeartbeatsAndReprobesAfterThirtyMinutes()
    {
        await using var h = new Harness();
        h.Http.Respond = sent => sent.Url.EndsWith("/heartbeat", StringComparison.Ordinal)
            ? FakeHandler.Json(403, """{"message":"Heartbeats need the Gold plan."}""")
            : FakeHandler.Json(200, "{}");

        h.Ready();
        await Harness.Until(() => h.Http.To("heartbeat").Count == 1);
        await h.Advance(TimeSpan.FromMinutes(29), TimeSpan.FromSeconds(30));

        Assert.Single(h.Http.To("heartbeat"));
        Assert.Empty(h.Disabled);

        await h.Advance(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30));

        await Harness.Until(() => h.Http.To("heartbeat").Count == 2);
        Assert.Single(h.Errors, error => error.Status == 403);
        Assert.Empty(h.Disabled);
    }

    [Fact]
    public async Task NotFoundDisablesThePublisherWithoutBlamingTheToken()
    {
        await using var h = new Harness();
        h.Http.Respond = sent => sent.Url.EndsWith("/stats", StringComparison.Ordinal)
            ? FakeHandler.Json(404, """{"message":"Not found."}""")
            : FakeHandler.Json(200, "{}");

        h.Ready();
        await h.Advance(TimeSpan.FromSeconds(6));
        await Harness.Until(() => !h.Disabled.IsEmpty);
        h.Host.LocalServerCount = 11;
        await h.Advance(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(30));

        Assert.Single(h.Http.To("stats"));
        Assert.Equal(new TdlDisabled(TdlPublisherKind.Stats, 404, "not-found"), Assert.Single(h.Disabled));
        Assert.DoesNotContain(h.Host.Logs, log => log.Contains("token", StringComparison.OrdinalIgnoreCase));

        h.Http.Respond = _ => FakeHandler.Json(200, "{}");
        Assert.True((await h.Poster.PostStatsAsync()).Ok);
        Assert.Equal(2, h.Http.To("stats").Count);
    }

    [Fact]
    public async Task ConflictDisablesTheCommandsPublisher()
    {
        await using var h = new Harness();
        h.Http.Respond = sent => sent.Url.EndsWith("/commands", StringComparison.Ordinal)
            ? FakeHandler.Json(409, """{"message":"Slash commands are not enabled."}""")
            : FakeHandler.Json(200, "{}");

        var result = await h.Poster.PostCommandsAsync();

        Assert.Equal(new TdlPostResult(false, 409), result);
        Assert.Equal(new TdlDisabled(TdlPublisherKind.Commands, 409, "conflict"), Assert.Single(h.Disabled));
    }

    [Fact]
    public async Task PayloadErrorIsReportedOnceAndKeepsThePublisherRunning()
    {
        await using var h = new Harness(options => options.Stats.Interval = TimeSpan.FromMinutes(2));
        h.Http.Respond = _ => FakeHandler.Json(422, """{"message":"The server count field must be at least 0."}""");

        h.Ready();
        await h.Advance(TimeSpan.FromSeconds(6));
        await Harness.Until(() => h.Http.To("stats").Count == 1);
        await h.Advance(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10));
        await Harness.Until(() => h.Http.To("stats").Count == 2);

        var error = Assert.Single(h.Errors, error => error.Publisher == TdlPublisherKind.Stats);
        Assert.Equal(422, error.Status);
        Assert.Contains("must be at least 0", error.Message, StringComparison.Ordinal);
        Assert.Empty(h.Disabled);
    }

    [Fact]
    public async Task RateLimitSuspendsStatsAndCommandsButNotHeartbeats()
    {
        await using var h = new Harness();
        h.Http.Respond = _ => FakeHandler.Json(429, """{"message":"Too Many Attempts."}""", ("Retry-After", "120"));

        Assert.Equal(new TdlPostResult(false, 429, "throttled"), await h.Poster.PostStatsAsync());
        h.Http.Respond = _ => FakeHandler.Json(200, "{}");

        Assert.Equal(new TdlPostResult(false, null, "suspended"), await h.Poster.PostCommandsAsync());
        Assert.True((await h.Poster.PostHeartbeatAsync()).Ok);
        Assert.Empty(h.Http.To("commands"));

        await h.Advance(TimeSpan.FromSeconds(123), TimeSpan.FromSeconds(10));

        Assert.True((await h.Poster.PostCommandsAsync()).Ok);
    }

    [Fact]
    public async Task RateLimitResetHeaderExtendsTheSuspension()
    {
        await using var h = new Harness();
        var reset = Harness.Epoch.AddSeconds(300).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        h.Http.Respond = _ => FakeHandler.Json(429, "{}", ("Retry-After", "1"), ("X-RateLimit-Reset", reset));

        await h.Poster.PostStatsAsync();
        h.Http.Respond = _ => FakeHandler.Json(200, "{}");
        await h.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("suspended", (await h.Poster.PostCommandsAsync()).Skipped);

        await h.Advance(TimeSpan.FromSeconds(293), TimeSpan.FromSeconds(10));

        Assert.True((await h.Poster.PostCommandsAsync()).Ok);
    }

    [Fact]
    public async Task ServerErrorsAreRetriedWithBackoff()
    {
        await using var h = new Harness();
        var statuses = new Queue<int>([502, 503, 200]);
        h.Http.Respond = _ => FakeHandler.Json(statuses.Dequeue(), "{}");

        var result = await h.Drive(h.Poster.PostStatsAsync());

        Assert.Equal(new TdlPostResult(true, 200), result);
        Assert.Equal(3, h.Http.Sent.Count);
    }

    [Fact]
    public async Task PersistentServerErrorsStopAfterThreeAttempts()
    {
        await using var h = new Harness();
        h.Http.Respond = _ => FakeHandler.Json(500, "{}");

        var result = await h.Drive(h.Poster.PostStatsAsync());

        Assert.Equal(new TdlPostResult(false, 500), result);
        Assert.Equal(3, h.Http.Sent.Count);
        Assert.True(Assert.Single(h.Errors).Retryable);
    }

    [Fact]
    public async Task NetworkFailuresAreRetried()
    {
        await using var h = new Harness();
        var calls = 0;
        h.Http.Respond = _ => ++calls == 1 ? throw new HttpRequestException("Connection refused") : FakeHandler.Json(200, "{}");

        var result = await h.Drive(h.Poster.PostHeartbeatAsync());

        Assert.True(result.Ok);
        Assert.Equal(2, h.Http.Sent.Count);
    }

    [Fact]
    public async Task BackoffDoublesAndIsCapped()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(Publisher.Backoff(0).TotalMilliseconds, 500, 1000);
            Assert.InRange(Publisher.Backoff(1).TotalMilliseconds, 1000, 2000);
            Assert.InRange(Publisher.Backoff(10).TotalMilliseconds, 15_000, 30_000);
        }

        await Task.CompletedTask;
    }
}

public sealed class SafetyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingTokenIsInert(string? token)
    {
        await using var h = new Harness(token: token);

        h.Ready();
        await h.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10));

        Assert.Empty(h.Http.Sent);
        Assert.Equal(new TdlPostResult(false, null, "no-token"), await h.Poster.PostStatsAsync());
        Assert.Equal("no-token", (await h.Poster.PostHeartbeatAsync()).Skipped);
        Assert.Equal("no-token", (await h.Poster.PostCommandsAsync()).Skipped);
        Assert.Single(h.Host.Logs, log => log.Contains("No stats token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownClusterCountNeverSendsHeartbeats()
    {
        await using var h = new Harness(options => options.ServerCount = () => Task.FromResult(100));
        h.Host.Topology = new(0, null, 4, false);

        h.Ready();
        await h.Advance(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5));

        Assert.Empty(h.Http.To("heartbeat"));
        Assert.Equal(new TdlDisabled(TdlPublisherKind.Heartbeat, null, "cluster-count-unknown"), Assert.Single(h.Disabled));
        Assert.Contains(h.Host.Logs, log => log.Contains("TdlOptions.Cluster", StringComparison.Ordinal));
        Assert.Equal("cluster-count-unknown", (await h.Poster.PostHeartbeatAsync()).Skipped);
        Assert.Empty(h.Http.To("heartbeat"));
        Assert.Equal("""{"server_count":100,"shard_count":4}""", Assert.Single(h.Http.To("stats")).Body);
    }

    [Fact]
    public async Task ClusterOverrideEnablesHeartbeats()
    {
        await using var h = new Harness(options => options.Cluster = new TdlCluster(2, 3));
        h.Host.Topology = new(0, null, 4, false);

        h.Ready();
        await h.Advance(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));

        Assert.StartsWith("""{"cluster":2,"cluster_count":3,""", h.Http.To("heartbeat")[0].Body, StringComparison.Ordinal);
        Assert.Empty(h.Http.To("stats"));
        Assert.Empty(h.Http.To("commands"));
    }

    [Fact]
    public async Task SomeShardsWithoutAServerCountPostNoStats()
    {
        await using var h = new Harness(options => options.Cluster = new TdlCluster(0, 2));

        h.Ready();
        await h.Advance(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));

        Assert.Empty(h.Http.To("stats"));
        Assert.Equal("unmeasurable", (await h.Poster.PostStatsAsync()).Skipped);
        Assert.Contains(h.Host.Logs, log => log.Contains("TdlOptions.ServerCount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualPostWithoutABotIdWaitsForReady()
    {
        await using var h = new Harness(options => options.BotId = null);

        Assert.Equal("not-ready", (await h.Poster.PostStatsAsync()).Skipped);
        Assert.Empty(h.Http.Sent);
    }

    [Fact]
    public async Task ThrowingEventHandlersAreContained()
    {
        await using var h = new Harness();
        h.Poster.Posted += _ => throw new InvalidOperationException("boom");

        Assert.True((await h.Poster.PostStatsAsync()).Ok);
        Assert.Contains(h.Host.Logs, log => log.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeStopsEveryTimer()
    {
        var h = new Harness();
        h.Ready();
        await Harness.Until(() => h.Http.To("heartbeat").Count == 1);

        await h.Poster.DisposeAsync();
        await h.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10));

        Assert.Single(h.Http.Sent);
        Assert.Equal("disposed", (await h.Poster.PostStatsAsync()).Skipped);
    }
}

public sealed class CadenceTests
{
    [Fact]
    public async Task StatsPostFiveSecondsAfterReadyAndHonourTheMinimumGap()
    {
        await using var h = new Harness();

        h.Ready();
        await h.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(h.Http.To("stats"));

        await h.Advance(TimeSpan.FromSeconds(1));
        await Harness.Until(() => h.Http.To("stats").Count == 1);

        h.Host.LocalServerCount = 11;
        Assert.Equal("min-gap", (await h.Poster.PostStatsAsync()).Skipped);
        Assert.Single(h.Http.To("stats"));
    }

    [Fact]
    public async Task GuildChangesAreDebounced()
    {
        await using var h = new Harness();
        h.Ready();
        await h.Advance(TimeSpan.FromSeconds(6));
        await h.Advance(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10));
        Assert.Single(h.Http.To("stats"));

        h.Host.LocalServerCount = 11;
        h.Poster.OnGuildsChanged();
        h.Poster.OnGuildsChanged();
        h.Poster.OnGuildsChanged();
        await h.Advance(TimeSpan.FromSeconds(19));
        Assert.Single(h.Http.To("stats"));

        await h.Advance(TimeSpan.FromSeconds(2));
        await Harness.Until(() => h.Http.To("stats").Count == 2);
        Assert.Equal("""{"server_count":11,"shard_count":1}""", h.Http.To("stats")[1].Body);
    }

    [Fact]
    public async Task UnchangedCountIsRepublishedOnlyEveryThirtyMinutes()
    {
        await using var h = new Harness();
        h.Ready();
        await h.Advance(TimeSpan.FromSeconds(6));
        await h.Advance(TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(30));
        Assert.Single(h.Http.To("stats"));

        await h.Advance(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(30));
        await Harness.Until(() => h.Http.To("stats").Count == 2);
    }

    [Fact]
    public async Task CommandsPostOnceTenSecondsAfterReady()
    {
        await using var h = new Harness();
        h.Ready();
        await h.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(h.Http.To("commands"));

        await h.Advance(TimeSpan.FromSeconds(1));
        await Harness.Until(() => h.Http.To("commands").Count == 1);
        await h.Advance(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        Assert.Single(h.Http.To("commands"));
    }

    [Fact]
    public async Task EmptyCommandListIsSentOnlyWhenAskedFor()
    {
        await using var h = new Harness();
        h.Host.Commands = [];

        Assert.Equal("empty", (await h.Poster.PostCommandsAsync()).Skipped);
        Assert.Empty(h.Http.Sent);

        h.Options.Commands.PostEmpty = true;
        Assert.True((await h.Poster.PostCommandsAsync()).Ok);
        Assert.Equal("""{"commands":[]}""", Assert.Single(h.Http.Sent).Body);
    }

    [Fact]
    public async Task HeartbeatCadenceFollowsTheServer()
    {
        await using var h = new Harness();
        h.Http.Respond = _ => FakeHandler.Json(200, """{"cluster":0,"status":"operational","shard_count":1,"next_beat_within_seconds":60}""");

        h.Ready();
        await Harness.Until(() => h.Http.To("heartbeat").Count == 1);
        await h.Advance(TimeSpan.FromSeconds(26));

        await Harness.Until(() => h.Http.To("heartbeat").Count == 2);
    }

    [Fact]
    public async Task ShardTransitionsBeatWithinASecond()
    {
        await using var h = new Harness();
        h.Ready();
        await Harness.Until(() => h.Http.To("heartbeat").Count == 1);

        h.Poster.OnShardTransition();
        h.Poster.OnShardTransition();
        await h.Advance(TimeSpan.FromSeconds(1));

        await Harness.Until(() => h.Http.To("heartbeat").Count == 2);
    }

    [Fact]
    public async Task OnlyClusterZeroPostsStatsAndCommands()
    {
        await using var h = new Harness();
        h.Host.Topology = new(1, 2, 2, false);

        h.Ready();
        await h.Advance(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));

        Assert.Empty(h.Http.To("stats"));
        Assert.Empty(h.Http.To("commands"));
        Assert.NotEmpty(h.Http.To("heartbeat"));
        Assert.Equal("not-elected", (await h.Poster.PostStatsAsync()).Skipped);
    }
}

public sealed class TopologyTests
{
    [Theory]
    [InlineData(new[] { 0 }, 1, true, 0, 1, 1, true)]
    [InlineData(new[] { 0, 1, 2 }, 3, false, 0, 1, 3, true)]
    [InlineData(new[] { 2 }, 4, true, 2, 4, 4, false)]
    [InlineData(new[] { 0, 1 }, 4, false, 0, null, 4, false)]
    [InlineData(new[] { 2, 3 }, 4, false, 2, null, 4, false)]
    [InlineData(new[] { 0 }, null, true, 0, null, 1, false)]
    public void DetectsTheClusterFromLocalAndTotalShards(int[] local, int? total, bool processPerShard, int clusterId, int? clusterCount, int shardCount, bool hasAllShards)
    {
        Assert.Equal(new TdlTopology(clusterId, clusterCount, shardCount, hasAllShards), TdlTopology.Detect(local, total, processPerShard));
    }
}
