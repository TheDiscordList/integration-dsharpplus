namespace TheDiscordList.DSharpPlus;

/// <summary>Configuration for <see cref="TdlAutoposter"/>. Every member has a working default.</summary>
public sealed class TdlOptions
{
    /// <summary>The production API root.</summary>
    public const string DefaultBaseUrl = "https://thediscordlist.com/api";

    /// <summary>The Discord application id. Defaults to the client's own application id once it is ready.</summary>
    public ulong? BotId { get; set; }

    /// <summary>The API root. Defaults to <see cref="DefaultBaseUrl"/>.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>Server-count publisher settings.</summary>
    public TdlStatsOptions Stats { get; } = new();

    /// <summary>Cluster heartbeat publisher settings.</summary>
    public TdlHeartbeatOptions Heartbeat { get; } = new();

    /// <summary>Application command publisher settings.</summary>
    public TdlCommandsOptions Commands { get; } = new();

    /// <summary>
    /// Names this process's cluster when it cannot be detected, for example a sharded client
    /// that runs only some of the bot's shards. Heartbeats stay off until the count is known.
    /// </summary>
    public TdlCluster? Cluster { get; set; }

    /// <summary>
    /// The bot-wide server count, for bots split across processes. Only cluster 0 calls it.
    /// </summary>
    public Func<Task<int>>? ServerCount { get; set; }

    /// <summary>An <see cref="System.Net.Http.HttpClient"/> to send requests through. It is never disposed.</summary>
    public HttpClient? HttpClient { get; set; }

    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>Server-count publisher settings.</summary>
public sealed class TdlStatsOptions
{
    /// <summary>Whether the publisher runs. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The cadence after the first post. Default 5 minutes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Trailing debounce after a guild join or leave. Default 20 seconds.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Hard floor between two stats requests, manual ones included. Default and minimum 60 seconds.</summary>
    public TimeSpan MinGap { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Cluster heartbeat publisher settings.</summary>
public sealed class TdlHeartbeatOptions
{
    /// <summary>Whether the publisher runs. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The starting cadence, clamped to 15 to 60 seconds; the API's response retunes it. Default 30 seconds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Application command publisher settings.</summary>
public sealed class TdlCommandsOptions
{
    /// <summary>Whether the publisher runs. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether an empty command list is sent. An empty list clears the listing's commands,
    /// so the default is <see langword="false"/>.
    /// </summary>
    public bool PostEmpty { get; set; }
}

/// <summary>One process of a bot split across several.</summary>
/// <param name="Id">This process's zero-based cluster index.</param>
/// <param name="Count">How many clusters the bot runs in total.</param>
public readonly record struct TdlCluster(int Id, int Count);
