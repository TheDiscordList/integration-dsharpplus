using System.Text.Json;
using DSharpPlus;
using DSharpPlus.Entities;

namespace TheDiscordList.DSharpPlus.Tests;

/// <summary>Real DSharpPlus clients, constructed offline: nothing here connects.</summary>
public sealed class DSharpPlusTests
{
    private static DiscordClient Client(int shardId = 0, int shardCount = 1) =>
        new(new DiscordConfiguration { Token = "offline", ShardId = shardId, ShardCount = shardCount });

    private static TdlOptions Offline(FakeHandler? http = null) => new() { HttpClient = new HttpClient(http ?? new FakeHandler()) };

    [Fact]
    public async Task PlainClientIsASingleCluster()
    {
        using var client = Client();
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());

        Assert.Equal(new TdlTopology(0, 1, 1, true), poster.Topology);
        Assert.Equal(new TdlShard(0, TdlShard.Connecting, null, 0), Assert.Single(poster.Host.Shards));
        Assert.Null(poster.Host.LocalServerCount);
    }

    [Fact]
    public async Task OneShardOfSeveralIsItsOwnCluster()
    {
        using var client = Client(shardId: 1, shardCount: 3);
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());

        Assert.Equal(new TdlTopology(1, 3, 3, false), poster.Topology);
    }

    [Fact]
    public async Task ReadyShardIsReportedAndCounted()
    {
        using var client = Client();
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());
        var host = (DSharpPlusHost)poster.Host;

        host.SetStatus(0, TdlShard.Ready);

        Assert.Equal(TdlShard.Ready, Assert.Single(host.Shards).Status);
        Assert.Equal(0, host.LocalServerCount);

        host.SetStatus(0, TdlShard.Disconnected);

        Assert.Null(host.LocalServerCount);
    }

    [Fact]
    public async Task ShardedClientBeforeStartHasNoShardsYet()
    {
        var client = new DiscordShardedClient(new DiscordConfiguration { Token = "offline" });
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());

        Assert.Empty(poster.Host.Shards);
        Assert.Null(poster.Host.LocalServerCount);
    }

    [Fact]
    public async Task ClusterOptionOverridesDetection()
    {
        using var client = Client(shardId: 1, shardCount: 3);
        var options = Offline();
        options.Cluster = new TdlCluster(0, 2);
        await using var poster = client.AddTheDiscordList("tdl_token", options);

        Assert.Equal(new TdlTopology(0, 2, 3, false), poster.Topology);
    }

    [Fact]
    public async Task MissingTokenIsInertOnARealClient()
    {
        using var client = Client();
        await using var poster = new TdlAutoposter(client, null);

        Assert.Equal("no-token", (await poster.PostStatsAsync()).Skipped);
    }

    [Fact]
    public void CommandsMapToTheWireShape()
    {
        var level = new DiscordApplicationCommandOption(
            "level", "How loud", ApplicationCommandOptionType.Integer, true, [new DiscordApplicationCommandOptionChoice("Level", 3L)]);
        var set = new DiscordApplicationCommandOption("set", "Set it", ApplicationCommandOptionType.SubCommand, options: [level]);
        var slash = new DiscordApplicationCommand("volume", "Volume", [set], defaultMemberPermissions: Permissions.ManageGuild);
        var user = new DiscordApplicationCommand("Inspect", string.Empty, type: ApplicationCommandType.UserContextMenu);
        var everyone = new DiscordApplicationCommand("ping", "Pong", defaultMemberPermissions: Permissions.None);

        var json = JsonSerializer.Serialize(new[] { slash, user, everyone }.Select(DSharpPlusHost.Map), TdlApiClient.Json);

        Assert.Equal(
            """[{"type":1,"name":"volume","description":"Volume","default_member_permissions":"32","options":[{"type":1,"name":"set","description":"Set it","options":"""
            + """[{"type":4,"name":"level","description":"How loud","required":true,"choices":[{"name":"Level","value":3}]}]}]},{"type":2,"name":"Inspect"},"""
            + """{"type":1,"name":"ping","description":"Pong","default_member_permissions":"0"}]""",
            json);
    }
}
