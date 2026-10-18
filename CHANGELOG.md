# Changelog

## Unreleased

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
