using System.Text.Json;

namespace TheDiscordList.DSharpPlus;

/// <summary>
/// The shared publisher state machine. Timers are one-shot and re-armed after each run, never
/// periodic: interval drift causes self-inflicted 429s. Nothing here may throw into a timer.
/// </summary>
internal abstract class Publisher : IDisposable
{
    public static readonly TimeSpan PlanReprobe = TimeSpan.FromMinutes(30);

    private const int MaxAttempts = 3;

    private readonly ITimer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<int> _reported = [];
    private string? _disabledReason;
    private long _suspendedUntilTicks;
    private volatile bool _running;

    protected Publisher(TdlAutoposter owner)
    {
        Owner = owner;
        _timer = owner.Time.CreateTimer(_ => _ = TickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public abstract TdlPublisherKind Kind { get; }

    public bool IsDisabled => Volatile.Read(ref _disabledReason) is not null;

    protected TdlAutoposter Owner { get; }

    protected bool Running
    {
        get => _running;
        set => _running = value;
    }

    protected DateTimeOffset Now => Owner.Time.GetUtcNow();

    protected DateTimeOffset SuspendedUntil
    {
        get => new(Interlocked.Read(ref _suspendedUntilTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _suspendedUntilTicks, value.UtcTicks);
    }

    protected string Name => Kind.ToString().ToLowerInvariant();

    public abstract void Start();

    /// <summary>A manual post. It re-arms a disabled publisher and bypasses the plan suspension.</summary>
    public Task<TdlPostResult> PostAsync()
    {
        Volatile.Write(ref _disabledReason, null);
        return GuardedAsync(manual: true);
    }

    /// <summary>Stops the loop and raises <see cref="TdlAutoposter.Disabled"/> once.</summary>
    public void Disable(int? status, string reason)
    {
        Cancel();
        if (Interlocked.CompareExchange(ref _disabledReason, reason, null) is null)
        {
            Owner.RaiseDisabled(new(Kind, status, reason));
        }
    }

    public void ReportUnexpected(Exception exception)
    {
        if (!Owner.Stopping.IsCancellationRequested)
        {
            Owner.RaiseError(new(Kind, null, false, exception.Message));
        }
    }

    public void Dispose() => _timer.Dispose();

    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt), 30_000) * (0.5 + (Random.Shared.NextDouble() * 0.5)));

    protected static TdlPostResult Skip(string reason) => new(false, null, reason);

    protected abstract Task<TdlPostResult> RunAsync(bool manual);

    protected void ScheduleIfRunning(TimeSpan delay)
    {
        if (Running && !Owner.Stopping.IsCancellationRequested)
        {
            Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        }
    }

    protected void Schedule(TimeSpan delay)
    {
        Running = true;
        ScheduleIfRunning(delay);
    }

    /// <summary>
    /// Sends one request, retrying transient failures. A backoff that would outlast the next
    /// tick is dropped instead: that tick carries fresher data, and a failed request still
    /// burns rate-limit budget.
    /// </summary>
    protected async Task<TdlPostResult> SendAsync(string path, object body, bool heartbeat, TimeSpan? nextTickIn = null)
    {
        var outcome = default(Outcome);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            outcome = await Owner.Api.PostAsync(path, body, heartbeat).ConfigureAwait(false);
            if (outcome.Kind != OutcomeKind.Transient || attempt == MaxAttempts - 1)
            {
                break;
            }

            var delay = Backoff(attempt);
            if (nextTickIn is { } next && delay >= next)
            {
                break;
            }

            await Task.Delay(delay, Owner.Time, Owner.Stopping).ConfigureAwait(false);
        }

        return React(outcome);
    }

    protected virtual TdlPostResult React(Outcome outcome)
    {
        var status = outcome.Status;
        switch (outcome.Kind)
        {
            case OutcomeKind.Ok:
                SuspendedUntil = default;
                Owner.RaisePosted(new(Kind, status ?? 200));
                return new(true, status);

            case OutcomeKind.Dropped:
                return Skip("suspended");

            case OutcomeKind.Payload:
                // The publisher stays enabled: the next tick may carry valid data.
                var detail = TdlApiClient.Detail(outcome.Body);
                ReportOnce(status ?? 422, $"TheDiscordList rejected the {Name} payload ({status}){(detail is null ? "." : ": " + detail)}");
                return new(false, status);

            case OutcomeKind.Auth:
                Owner.OnUnauthorized();
                return new(false, 401);

            case OutcomeKind.Plan:
                // A suspension, not a disable: the plan is checked per request, so a bot can gain Gold mid-run.
                SuspendedUntil = Now + PlanReprobe;
                ReportOnce(403, $"The {Name} endpoint needs a higher plan (heartbeats need Gold). Re-probing in 30 minutes.");
                return new(false, 403, "plan");

            case OutcomeKind.Config:
                Disable(status, status == 409 ? "conflict" : "not-found");
                return new(false, status);

            case OutcomeKind.Throttled:
                return new(false, 429, "throttled");

            default:
                Owner.RaiseError(new(Kind, status, true, outcome.Error ?? "Request failed."));
                return new(false, status);
        }
    }

    private async Task TickAsync()
    {
        try
        {
            await GuardedAsync(manual: false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportUnexpected(exception);
        }
    }

    private async Task<TdlPostResult> GuardedAsync(bool manual)
    {
        if (!manual && IsDisabled)
        {
            return Skip("disabled");
        }

        if (!manual && Now < SuspendedUntil)
        {
            ScheduleIfRunning(SuspendedUntil - Now);
            return Skip("plan");
        }

        if (!_gate.Wait(0))
        {
            return Skip("in-flight");
        }

        try
        {
            return await RunAsync(manual).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReportOnce(int status, string message)
    {
        lock (_reported)
        {
            if (!_reported.Add(status))
            {
                return;
            }
        }

        Owner.RaiseError(new(Kind, status, false, message));
    }

    private void Cancel() => Change(Timeout.InfiniteTimeSpan);

    private void Change(TimeSpan due)
    {
        try
        {
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed class StatsPublisher(TdlAutoposter owner) : Publisher(owner)
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Remeasure = TimeSpan.FromSeconds(60);

    /// <summary>An unchanged count is republished this often, so a listing never looks abandoned.</summary>
    public static readonly TimeSpan ForceAfter = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan MinGapFloor = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private DateTimeOffset? _lastPostAt;
    private DateTimeOffset? _debounceCap;
    private int? _lastCount;
    private int? _lastShardCount;

    public override TdlPublisherKind Kind => TdlPublisherKind.Stats;

    private TimeSpan Interval => Owner.Options.Stats.Interval;

    private TimeSpan MinGap => Owner.Options.Stats.MinGap > MinGapFloor ? Owner.Options.Stats.MinGap : MinGapFloor;

    public override void Start()
    {
        var topology = Owner.Topology;
        if (!topology.IsStatsPublisher)
        {
            Owner.Host.Log(TdlLogLevel.Information, $"Stats are posted by cluster 0; this is cluster {topology.ClusterId}.");
            return;
        }

        Schedule(FirstDelay);
    }

    /// <summary>Trailing debounce, capped so a join storm cannot starve the post.</summary>
    public void OnGuildChange()
    {
        if (!Running || IsDisabled)
        {
            return;
        }

        lock (_lock)
        {
            var now = Now;
            _debounceCap ??= now + MinGap;
            var at = now + Owner.Options.Stats.Debounce;
            ScheduleIfRunning((at < _debounceCap.Value ? at : _debounceCap.Value) - now);
        }
    }

    protected override async Task<TdlPostResult> RunAsync(bool manual)
    {
        lock (_lock)
        {
            _debounceCap = null;
        }

        var topology = Owner.Topology;
        if (!topology.IsStatsPublisher)
        {
            return Skip("not-elected");
        }

        // A hard floor: a join storm, a mass reconnect and a manual post all route through it.
        var now = Now;
        if (_lastPostAt is { } last && now < last + MinGap)
        {
            ScheduleIfRunning(last + MinGap - now);
            return Skip("min-gap");
        }

        var count = await MeasureAsync(topology).ConfigureAwait(false);
        if (count is null)
        {
            ScheduleIfRunning(Remeasure);
            return Skip("unmeasurable");
        }

        // Every post re-indexes the listing and fires a bot.stats_reported webhook whether the
        // number moved or not, and the server does not dedupe, so the skip happens here.
        if (count == _lastCount && topology.ShardCount == _lastShardCount && Now < _lastPostAt + ForceAfter)
        {
            ScheduleIfRunning(Interval);
            return Skip("unchanged");
        }

        _lastPostAt = Now;

        // shard_count is always sent: the server stores an absent one as null.
        var body = new StatsBody(count.Value, topology.ShardCount);
        var result = await SendAsync($"/bots/{Owner.BotId}/stats", body, heartbeat: false, Interval).ConfigureAwait(false);
        if (result.Ok)
        {
            _lastCount = count;
            _lastShardCount = topology.ShardCount;
        }

        if (!IsDisabled)
        {
            ScheduleIfRunning(Interval);
        }

        return result;
    }

    private async Task<int?> MeasureAsync(TdlTopology topology)
    {
        if (Owner.Options.ServerCount is { } serverCount)
        {
            try
            {
                var value = await serverCount().ConfigureAwait(false);
                return value >= 0 ? value : null;
            }
            catch (Exception exception)
            {
                Owner.RaiseError(new(Kind, null, true, "TdlOptions.ServerCount threw: " + exception.Message));
                return null;
            }
        }

        if (topology.HasAllShards)
        {
            return Owner.Host.LocalServerCount;
        }

        Owner.WarnOnce(
            "server-count",
            "This process runs only some of the bot's shards, so its guild cache is not the bot's total. "
            + "Set TdlOptions.ServerCount to post stats.");
        return null;
    }
}

internal sealed class HeartbeatPublisher(TdlAutoposter owner) : Publisher(owner)
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);
    private const int JitterMilliseconds = 5000;

    private long _intervalTicks = owner.Options.Heartbeat.Interval.Ticks;

    public override TdlPublisherKind Kind => TdlPublisherKind.Heartbeat;

    private TimeSpan Interval => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));

    public override void Start()
    {
        // Every beat hard-deletes the cluster rows numbered at or above its cluster_count, so
        // an unknown count means silence, never a guess.
        if (Owner.Topology.ClusterCount is null)
        {
            Disable(null, "cluster-count-unknown");
            return;
        }

        Schedule(TimeSpan.Zero);
    }

    /// <summary>An out-of-band beat after a shard transition. Repeated calls coalesce.</summary>
    public void BeatSoon()
    {
        if (!IsDisabled)
        {
            ScheduleIfRunning(TimeSpan.FromSeconds(1));
        }
    }

    protected override async Task<TdlPostResult> RunAsync(bool manual)
    {
        var topology = Owner.Topology;
        if (topology.ClusterCount is not int clusterCount)
        {
            return Skip("cluster-count-unknown");
        }

        var shards = Owner.Host.Shards;
        if (shards.Count == 0)
        {
            ScheduleNext();
            return Skip("no-shards");
        }

        var body = new HeartbeatBody(topology.ClusterId, clusterCount, shards);
        var result = await SendAsync($"/bots/{Owner.BotId}/heartbeat", body, heartbeat: true, Interval).ConfigureAwait(false);
        if (!IsDisabled)
        {
            ScheduleNext();
        }

        return result;
    }

    /// <summary>
    /// The cadence follows the server's <c>next_beat_within_seconds</c>, so a change to its
    /// grace window retunes existing installs without a release.
    /// </summary>
    protected override TdlPostResult React(Outcome outcome)
    {
        if (outcome.Kind == OutcomeKind.Ok && NextBeatWithin(outcome.Body) is { } seconds)
        {
            var proposed = TimeSpan.FromSeconds(seconds / 3).Ticks;
            Interlocked.Exchange(ref _intervalTicks, Math.Clamp(proposed, MinInterval.Ticks, MaxInterval.Ticks));
        }

        return base.React(outcome);
    }

    private static double? NextBeatWithin(string? body)
    {
        try
        {
            using var document = JsonDocument.Parse(body ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("next_beat_within_seconds", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetDouble() is var seconds and > 0
                ? seconds
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ScheduleNext()
    {
        var delay = Interval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, JitterMilliseconds + 1));
        var suspended = SuspendedUntil - Now;
        ScheduleIfRunning(suspended > delay ? suspended : delay);
    }
}

internal sealed class CommandsPublisher(TdlAutoposter owner) : Publisher(owner)
{
    /// <summary>Offset from the first stats post, because both share one server-side rate budget.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    public override TdlPublisherKind Kind => TdlPublisherKind.Commands;

    /// <summary>Once per process: commands change on deploy, and a deploy is a restart.</summary>
    public override void Start()
    {
        if (Owner.Topology.IsStatsPublisher)
        {
            Schedule(Delay);
        }
    }

    protected override async Task<TdlPostResult> RunAsync(bool manual)
    {
        IReadOnlyList<TdlCommand> commands;
        try
        {
            commands = await Owner.Host.GetCommandsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!Owner.Stopping.IsCancellationRequested)
        {
            Owner.RaiseError(new(Kind, null, true, "Could not fetch the global application commands: " + exception.Message));
            return Skip("fetch-failed");
        }

        // An empty array clears the listing's commands, so it is never sent by accident.
        if (commands.Count == 0 && !Owner.Options.Commands.PostEmpty)
        {
            return Skip("empty");
        }

        return await SendAsync($"/bots/{Owner.BotId}/commands", new CommandsBody(commands), heartbeat: false).ConfigureAwait(false);
    }
}

internal sealed record StatsBody(int ServerCount, int ShardCount);

internal sealed record HeartbeatBody(int Cluster, int ClusterCount, IReadOnlyList<TdlShard> Shards);

internal sealed record CommandsBody(IReadOnlyList<TdlCommand> Commands);
