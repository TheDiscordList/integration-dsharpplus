namespace TheDiscordList.DSharpPlus;

/// <summary>What the publishers need from a Discord library, and nothing more.</summary>
internal interface ITdlHost
{
    TdlTopology Topology { get; }

    IReadOnlyList<TdlShard> Shards { get; }

    /// <summary>The guild total across local shards, or <see langword="null"/> while any of them is not ready.</summary>
    int? LocalServerCount { get; }

    Task<IReadOnlyList<TdlCommand>> GetCommandsAsync();

    void Log(TdlLogLevel level, string message);
}

internal enum TdlLogLevel
{
    Information,
    Warning,
}

internal sealed record TdlTopology(int ClusterId, int? ClusterCount, int ShardCount, bool HasAllShards)
{
    public bool IsStatsPublisher => ClusterId == 0;

    /// <summary>
    /// <paramref name="processPerShard"/> is true for a plain (non-sharded) client: one shard
    /// of several in a process is the one-process-per-shard layout, so that shard is the cluster.
    /// </summary>
    public static TdlTopology Detect(IReadOnlyCollection<int> localShardIds, int? totalShards, bool processPerShard)
    {
        var local = localShardIds.Count;
        if (totalShards is int total && local >= total)
        {
            return new(0, 1, total, true);
        }

        if (totalShards is int shards && processPerShard && local == 1)
        {
            return new(localShardIds.First(), shards, shards, false);
        }

        // Some shards run elsewhere and nothing says how many processes there are. A guessed
        // cluster_count would delete sibling clusters server-side, so it stays unknown.
        var clusterId = local == 0 || localShardIds.Contains(0) ? 0 : localShardIds.Min();
        return new(clusterId, null, totalShards ?? Math.Max(local, 1), false);
    }
}

internal sealed record TdlShard(int Id, string Status, int? LatencyMs, int Guilds)
{
    public const string Ready = "ready";
    public const string Connecting = "connecting";
    public const string Disconnected = "disconnected";

    /// <summary>The API rejects anything above ten minutes.</summary>
    public static int? Latency(int milliseconds) => milliseconds > 0 ? Math.Min(milliseconds, 600_000) : null;
}

internal sealed record TdlCommand(
    int? Type,
    string Name,
    string? Description,
    bool? Nsfw,
    string? DefaultMemberPermissions,
    IReadOnlyList<TdlCommandOption>? Options);

internal sealed record TdlCommandOption(
    int Type,
    string Name,
    string Description,
    bool? Required,
    IReadOnlyList<TdlCommandChoice>? Choices,
    IReadOnlyList<TdlCommandOption>? Options);

internal sealed record TdlCommandChoice(string Name, object Value);
