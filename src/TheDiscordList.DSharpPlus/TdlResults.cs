namespace TheDiscordList.DSharpPlus;

/// <summary>The three things <see cref="TdlAutoposter"/> publishes.</summary>
public enum TdlPublisherKind
{
    /// <summary><c>POST /bots/{id}/stats</c>.</summary>
    Stats,

    /// <summary><c>POST /bots/{id}/heartbeat</c>.</summary>
    Heartbeat,

    /// <summary><c>POST /bots/{id}/commands</c>.</summary>
    Commands,
}

/// <summary>What one publish did. Returned, never thrown.</summary>
/// <param name="Ok">Whether the API accepted the request.</param>
/// <param name="Status">The HTTP status, or <see langword="null"/> when nothing was sent or the network failed.</param>
/// <param name="Skipped">
/// Why nothing was sent, when nothing was: <c>no-token</c>, <c>not-ready</c>, <c>disabled</c>,
/// <c>suspended</c>, <c>throttled</c>, <c>plan</c>, <c>min-gap</c>, <c>unchanged</c>,
/// <c>unmeasurable</c>, <c>not-elected</c>, <c>no-shards</c>, <c>cluster-count-unknown</c>,
/// <c>empty</c>, <c>fetch-failed</c>, <c>in-flight</c>, <c>disposed</c>.
/// </param>
public sealed record TdlPostResult(bool Ok, int? Status, string? Skipped = null);

/// <summary>Raised after the API accepted a request.</summary>
/// <param name="Publisher">Which publisher sent it.</param>
/// <param name="Status">The 2xx status.</param>
public sealed record TdlPosted(TdlPublisherKind Publisher, int Status);

/// <summary>Raised when a request failed, or local work around it did.</summary>
/// <param name="Publisher">Which publisher failed.</param>
/// <param name="Status">The HTTP status, or <see langword="null"/> for network and local failures.</param>
/// <param name="Retryable">Whether a later attempt may succeed unchanged.</param>
/// <param name="Message">A readable description.</param>
public sealed record TdlError(TdlPublisherKind Publisher, int? Status, bool Retryable, string Message);

/// <summary>Raised when a publisher stops itself. A manual post re-arms it.</summary>
/// <param name="Publisher">Which publisher stopped.</param>
/// <param name="Status">The HTTP status that caused it, if any.</param>
/// <param name="Reason"><c>unauthorized</c>, <c>not-found</c>, <c>conflict</c> or <c>cluster-count-unknown</c>.</param>
public sealed record TdlDisabled(TdlPublisherKind Publisher, int? Status, string Reason);
