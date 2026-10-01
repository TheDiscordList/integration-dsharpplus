using System.Collections.Concurrent;
using System.Globalization;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.Logging;

namespace TheDiscordList.DSharpPlus;

public sealed partial class TdlAutoposter
{
    internal const string LibraryName = "DSharpPlus";
    internal const string RepositoryUrl = "https://github.com/TheDiscordList/integration-dsharpplus";

    /// <summary>
    /// Starts publishing for a <see cref="DiscordClient"/>. Call it before <c>ConnectAsync()</c>:
    /// the first <c>Ready</c> starts the publishers. Logs go to <c>client.Logger</c>.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="token">The listing's stats token. Missing or blank logs a warning and returns an inert poster.</param>
    /// <param name="options">Optional settings.</param>
    public TdlAutoposter(DiscordClient client, string? token, TdlOptions? options = null)
        : this(new DSharpPlusHost(client.Logger, () => [client], () => client, processPerShard: true), token, options)
    {
        if (IsInert)
        {
            return;
        }

        client.Ready += OnReady;
        client.Resumed += OnResumed;
        client.SocketOpened += OnSocketOpened;
        client.SocketClosed += OnSocketClosed;
        client.GuildCreated += OnGuildCreated;
        client.GuildDeleted += OnGuildDeleted;
        _detach.Add(() =>
        {
            client.Ready -= OnReady;
            client.Resumed -= OnResumed;
            client.SocketOpened -= OnSocketOpened;
            client.SocketClosed -= OnSocketClosed;
            client.GuildCreated -= OnGuildCreated;
            client.GuildDeleted -= OnGuildDeleted;
        });
    }

    /// <summary>
    /// Starts publishing for a <see cref="DiscordShardedClient"/>. Call it before <c>StartAsync()</c>:
    /// the first shard's <c>Ready</c> starts the publishers. Logs go to <c>client.Logger</c>.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="token">The listing's stats token. Missing or blank logs a warning and returns an inert poster.</param>
    /// <param name="options">Optional settings.</param>
    public TdlAutoposter(DiscordShardedClient client, string? token, TdlOptions? options = null)
        : this(new DSharpPlusHost(client.Logger, () => client.ShardClients?.Values ?? [], () => client.ShardClients.Values.MinBy(shard => shard.ShardId)!, processPerShard: false), token, options)
    {
        if (IsInert)
        {
            return;
        }

        client.Ready += OnReady;
        client.Resumed += OnResumed;
        client.SocketOpened += OnSocketOpened;
        client.SocketClosed += OnSocketClosed;
        client.GuildCreated += OnGuildCreated;
        client.GuildDeleted += OnGuildDeleted;
        _detach.Add(() =>
        {
            client.Ready -= OnReady;
            client.Resumed -= OnResumed;
            client.SocketOpened -= OnSocketOpened;
            client.SocketClosed -= OnSocketClosed;
            client.GuildCreated -= OnGuildCreated;
            client.GuildDeleted -= OnGuildDeleted;
        });
    }

    private DSharpPlusHost Shards => (DSharpPlusHost)Host;

    /// <summary>Returns at once: a gateway handler that blocks stalls the client.</summary>
    private Task OnReady(DiscordClient sender, ReadyEventArgs args)
    {
        Shards.SetStatus(sender.ShardId, TdlShard.Ready);
        _ = StartFromAsync(sender);
        OnShardTransition();
        return Task.CompletedTask;
    }

    private Task OnResumed(DiscordClient sender, ReadyEventArgs args) => Transition(sender, TdlShard.Ready);

    private Task OnSocketOpened(DiscordClient sender, SocketEventArgs args) => Transition(sender, TdlShard.Connecting);

    private Task OnSocketClosed(DiscordClient sender, SocketCloseEventArgs args) => Transition(sender, TdlShard.Disconnected);

    private Task OnGuildCreated(DiscordClient sender, GuildCreateEventArgs args) => GuildsChanged();

    private Task OnGuildDeleted(DiscordClient sender, GuildDeleteEventArgs args) => GuildsChanged();

    private Task Transition(DiscordClient shard, string status)
    {
        Shards.SetStatus(shard.ShardId, status);
        OnShardTransition();
        return Task.CompletedTask;
    }

    private Task GuildsChanged()
    {
        OnGuildsChanged();
        return Task.CompletedTask;
    }

    private async Task StartFromAsync(DiscordClient shard)
    {
        try
        {
            var botId = BotId ?? shard.CurrentApplication?.Id ?? (await shard.GetCurrentApplicationAsync().ConfigureAwait(false)).Id;
            Start(botId);
        }
        catch (Exception exception)
        {
            WarnOnce("bot-id", $"Could not read the application id ({exception.Message}). Set TdlOptions.BotId.");
        }
    }
}

/// <summary>Creates a <see cref="TdlAutoposter"/> from a client.</summary>
public static class TdlDSharpPlusExtensions
{
    /// <summary>Publishes this client's stats, heartbeats and commands to TheDiscordList.</summary>
    /// <param name="client">The client.</param>
    /// <param name="token">The listing's stats token.</param>
    /// <param name="options">Optional settings.</param>
    /// <returns>The running poster. Dispose it to stop.</returns>
    public static TdlAutoposter AddTheDiscordList(this DiscordClient client, string? token, TdlOptions? options = null) => new(client, token, options);

    /// <inheritdoc cref="AddTheDiscordList(DiscordClient, string?, TdlOptions?)"/>
    public static TdlAutoposter AddTheDiscordList(this DiscordShardedClient client, string? token, TdlOptions? options = null) => new(client, token, options);
}

internal sealed class DSharpPlusHost(ILogger logger, Func<IEnumerable<DiscordClient>> shards, Func<DiscordClient> restClient, bool processPerShard) : ITdlHost
{
    private static readonly EventId LogEvent = new(1900, "TheDiscordList");

    /// <summary>DSharpPlus exposes no connection state, so it is tracked from gateway events.</summary>
    private readonly ConcurrentDictionary<int, string> _status = new();

    public TdlTopology Topology
    {
        get
        {
            var local = shards().ToList();
            var total = local.Count > 0 && local[0].ShardCount > 0 ? local[0].ShardCount : (int?)null;
            return TdlTopology.Detect(local.Select(shard => shard.ShardId).ToList(), total, processPerShard);
        }
    }

    public IReadOnlyList<TdlShard> Shards => shards().Select(Report).ToList();

    public int? LocalServerCount
    {
        get
        {
            var local = shards().ToList();
            return local.Count > 0 && local.All(shard => Status(shard) == TdlShard.Ready)
                ? local.Sum(shard => shard.Guilds.Count)
                : null;
        }
    }

    public void SetStatus(int shardId, string status) => _status[shardId] = status;

    public async Task<IReadOnlyList<TdlCommand>> GetCommandsAsync()
    {
        var commands = await restClient().GetGlobalApplicationCommandsAsync().ConfigureAwait(false);
        return commands.Select(Map).ToList();
    }

    public void Log(TdlLogLevel level, string message) =>
        logger.Log(level == TdlLogLevel.Warning ? LogLevel.Warning : LogLevel.Information, LogEvent, "[TheDiscordList] {Message}", message);

    internal static TdlCommand Map(DiscordApplicationCommand command) => new(
        (int)command.Type,
        command.Name,
        string.IsNullOrEmpty(command.Description) ? null : command.Description,
        null,
        command.DefaultMemberPermissions is { } permissions ? ((long)permissions).ToString(CultureInfo.InvariantCulture) : null,
        Map(command.Options));

    private static List<TdlCommandOption>? Map(IEnumerable<DiscordApplicationCommandOption>? options)
    {
        var list = options?.Select(option => new TdlCommandOption(
            (int)option.Type,
            option.Name,
            option.Description,
            option.Required,
            option.Choices is { Count: > 0 } choices ? choices.Select(choice => new TdlCommandChoice(choice.Name, choice.Value)).ToList() : null,
            Map(option.Options))).ToList();
        return list is { Count: > 0 } ? list : null;
    }

    private string Status(DiscordClient shard) => _status.TryGetValue(shard.ShardId, out var status) ? status : TdlShard.Connecting;

    private TdlShard Report(DiscordClient shard)
    {
        var status = Status(shard);
        var latency = status == TdlShard.Ready ? TdlShard.Latency(shard.Ping) : null;
        return new TdlShard(shard.ShardId, status, latency, shard.Guilds.Count);
    }
}
