using System.Collections.Concurrent;
using System.Reflection;

namespace TheDiscordList.DSharpPlus;

/// <summary>
/// Publishes a bot's server count, cluster heartbeats and application commands to
/// TheDiscordList. Nothing in it throws into the host process: background failures are
/// reported through <see cref="Error"/> and <see cref="Disabled"/>, and every manual post
/// returns a <see cref="TdlPostResult"/>.
/// </summary>
public sealed partial class TdlAutoposter : IAsyncDisposable
{
    private readonly TdlApiClient? _api;
    private readonly HttpClient? _ownedHttp;
    private readonly StatsPublisher? _stats;
    private readonly HeartbeatPublisher? _heartbeat;
    private readonly CommandsPublisher? _commands;
    private readonly ConcurrentDictionary<string, byte> _warned = new();
    private readonly List<Action> _detach = [];
    private readonly CancellationTokenSource _stopping = new();
    private ulong? _botId;
    private int _started;

    internal TdlAutoposter(ITdlHost host, string? token, TdlOptions? options)
    {
        Host = host;
        Options = options ?? new TdlOptions();
        _botId = Options.BotId;

        token = token?.Trim();
        if (token is not null && token.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            token = token["Bearer ".Length..].TrimStart();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            WarnOnce("no-token", "No stats token supplied; auto-posting is disabled.");
            return;
        }

        var http = Options.HttpClient ?? (_ownedHttp = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) }));
        _api = new TdlApiClient(http, Options.BaseUrl, token, UserAgent, Options.TimeProvider, _stopping.Token);

        if (Options.Stats.Enabled)
        {
            _stats = new StatsPublisher(this);
        }

        if (Options.Heartbeat.Enabled)
        {
            _heartbeat = new HeartbeatPublisher(this);
        }

        if (Options.Commands.Enabled)
        {
            _commands = new CommandsPublisher(this);
        }
    }

    /// <summary>Raised after the API accepted a request.</summary>
    public event Action<TdlPosted>? Posted;

    /// <summary>Raised when a request fails. Without a handler, the failure is logged instead.</summary>
    public event Action<TdlError>? Error;

    /// <summary>Raised when a publisher stops itself. A manual post re-arms it.</summary>
    public event Action<TdlDisabled>? Disabled;

    /// <summary>The package version.</summary>
    public static string Version { get; } =
        typeof(TdlAutoposter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    internal static string UserAgent => $"TheDiscordList-{LibraryName}/{Version} (+{RepositoryUrl})";

    internal ITdlHost Host { get; }

    internal TdlOptions Options { get; }

    internal TdlApiClient Api => _api!;

    internal TimeProvider Time => Options.TimeProvider;

    /// <summary>Monotonic, so a wall-clock jump never stretches or cuts a suspension or a gap.</summary>
    internal TimeSpan Now => Time.GetElapsedTime(0);

    internal CancellationToken Stopping => _stopping.Token;

    internal ulong? BotId => _botId;

    internal bool IsInert => _api is null;

    internal TdlTopology Topology => Options.Cluster is { } cluster
        ? Host.Topology with { ClusterId = cluster.Id, ClusterCount = cluster.Count, HasAllShards = cluster.Count == 1 }
        : Host.Topology;

    /// <summary>Posts the server count now. Never throws; subject to the 60 second floor.</summary>
    public Task<TdlPostResult> PostStatsAsync() => ManualAsync(_stats);

    /// <summary>Sends this process's heartbeat now. Never throws.</summary>
    public Task<TdlPostResult> PostHeartbeatAsync() => ManualAsync(_heartbeat);

    /// <summary>Posts the global application commands now, for example after re-registering them. Never throws.</summary>
    public Task<TdlPostResult> PostCommandsAsync() => ManualAsync(_commands);

    /// <summary>Stops every timer and detaches every client handler. Not restartable.</summary>
    public ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }

        _stopping.Cancel();
        foreach (var detach in _detach)
        {
            detach();
        }

        _stats?.Dispose();
        _heartbeat?.Dispose();
        _commands?.Dispose();
        _ownedHttp?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Called by the adapter on every ready. Starts the publishers once.</summary>
    internal void Start(ulong botId)
    {
        if (IsInert || _stopping.IsCancellationRequested)
        {
            return;
        }

        _botId ??= botId;
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            _heartbeat?.BeatSoon();
            return;
        }

        var topology = Topology;
        var clusters = topology.ClusterCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unknown number of";
        Host.Log(TdlLogLevel.Information, $"Publishing for bot {_botId} as cluster {topology.ClusterId} of {clusters} clusters, {topology.ShardCount} shard(s) in total.");

        // Heartbeat first: a booting bot must read "connecting", not dead.
        _heartbeat?.Start();
        _stats?.Start();
        _commands?.Start();
    }

    internal void OnGuildsChanged() => _stats?.OnGuildChange();

    internal void OnShardTransition() => _heartbeat?.BeatSoon();

    internal void OnUnauthorized()
    {
        WarnOnce("unauthorized", "The stats token was rejected (401). Every publisher is disabled until a manual post re-arms it.");
        _stats?.Disable(401, "unauthorized");
        _heartbeat?.Disable(401, "unauthorized");
        _commands?.Disable(401, "unauthorized");
    }

    internal void WarnOnce(string key, string message)
    {
        if (_warned.TryAdd(key, 0))
        {
            Host.Log(TdlLogLevel.Warning, message);
        }
    }

    internal void RaisePosted(TdlPosted posted) => Guard(() => Posted?.Invoke(posted));

    internal void RaiseError(TdlError error)
    {
        var handler = Error;
        if (handler is null)
        {
            Host.Log(TdlLogLevel.Warning, $"{error.Publisher}: {error.Message}");
            return;
        }

        Guard(() => handler(error));
    }

    internal void RaiseDisabled(TdlDisabled disabled)
    {
        var message = disabled.Reason switch
        {
            "not-found" => $"TheDiscordList does not know bot {_botId}, or it is a server listing (404). Check TdlOptions.BotId.",
            "conflict" => "The listing is not configured for slash commands (409).",
            "cluster-count-unknown" => "Could not determine how many clusters this bot runs, and a wrong cluster_count deletes "
                + "sibling clusters server-side. Set TdlOptions.Cluster = new(id, count) to enable heartbeats.",
            _ => null,
        };

        if (message is not null)
        {
            Host.Log(TdlLogLevel.Warning, $"{disabled.Publisher} disabled: {message}");
        }

        Guard(() => Disabled?.Invoke(disabled));
    }

    private async Task<TdlPostResult> ManualAsync(Publisher? publisher)
    {
        if (IsInert)
        {
            return new(false, null, "no-token");
        }

        if (_stopping.IsCancellationRequested)
        {
            return new(false, null, "disposed");
        }

        if (publisher is null)
        {
            return new(false, null, "disabled");
        }

        if (_botId is null)
        {
            return new(false, null, "not-ready");
        }

        try
        {
            return await publisher.PostAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            publisher.ReportUnexpected(exception);
            return new(false, null, _stopping.IsCancellationRequested ? "disposed" : "error");
        }
    }

    private void Guard(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception exception)
        {
            Host.Log(TdlLogLevel.Warning, "An event handler threw: " + exception.Message);
        }
    }
}
