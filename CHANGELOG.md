# Changelog

## Unreleased

## 0.2.1

- A shard whose socket closes reads `disconnected` only when DSharpPlus will not
  reconnect it, and `connecting` otherwise, so a routine reconnect no longer opens
  a public incident. If that cannot be read it reads `connecting`, rather than
  staying `ready`.
- A shard's `guilds` is left out until that shard is ready, so a restart's first
  beat can no longer zero the public server count.
- The commands post comes back 5 minutes after a server error, a rate limit, a
  plan suspension or a failed fetch from Discord, until it lands or a terminal
  `4xx` ends it.
- A `PostCommandsAsync` that lands ends a pending retry, which would only post the
  same list again.
- A scheduled post that gets a `429` retries once the window lifts, if that lands
  before its next tick. A manual post still returns `throttled` at once.
- A heartbeat `429` no longer suspends stats and commands.
- A burst of shard transitions can no longer postpone the heartbeat.
- The release workflow pins its actions to commit SHAs and refuses a tag that is
  not on `main` or does not match the csproj version.

## 0.2.0

- `TdlWebhook.Verify` throws `ArgumentException` on an empty or blank secret
  instead of checking signatures against an empty key. `examples/BasicBot`
  maps its webhook route only when `TDL_WEBHOOK_SECRET` is set.
- The `listing.bump_ready` webhook event: `TdlWebhookEvents.ListingBumpReady`
  and `TdlListingBumpReady`.
- Out-of-band heartbeats after a shard transition wait at least 10 seconds
  after the previous beat.
- Only network errors, timeouts, `408`, `425` and `5xx` are retried, and never
  past the next scheduled post. Any other `4xx` is reported once.
- A `429` suspension is capped at one hour, and every suspension and gap runs
  on a monotonic clock, so a wall-clock jump no longer stretches or cuts it.
- A pasted `Bearer ` prefix on the stats token is stripped, and the starting
  heartbeat interval is clamped to 15-60 seconds.

## 0.1.0 - 2026-10-15

- First release: `AddTheDiscordList` / `TdlAutoposter` for DSharpPlus 4.x `DiscordClient`
  and `DiscordShardedClient`, publishing server count, cluster heartbeats and global
  application commands to TheDiscordList.
- `TdlWebhook.Verify` for inbound webhook signatures, with typed event data.
