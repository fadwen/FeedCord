# Fork notes

This is a fork of [Qolors/FeedCord](https://github.com/Qolors/FeedCord) carrying
three local patches. `master` is upstream `master` plus those commits, and the
image is built from source rather than pulled from a registry.

Everything else tracks upstream unchanged.

## Patch 1: negotiate compression on feed requests

**File:** `FeedCord/src/Startup.cs`

Upstream constructs its `HttpClientHandler` with only `AllowAutoRedirect = true`:

```csharp
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler(){AllowAutoRedirect = true});
```

With `AutomaticDecompression` left at its default (`DecompressionMethods.None`),
`HttpClient` never sends an `Accept-Encoding` header, so every feed is fetched
uncompressed even when the origin would happily gzip it.

The patch adds `AutomaticDecompression = DecompressionMethods.All`, so
gzip/deflate/br are advertised and transparently decoded.

### Measured effect

Three feeds, fetched from the deployment host with and without
`Accept-Encoding`. All three origins do offer gzip:

| Feed | Uncompressed | Compressed | Ratio |
| ---- | ------------ | ---------- | ----- |
| A    | 286 KB       | 90 KB      | 3.2x  |
| B    | 72 KB        | 16 KB      | 4.5x  |
| C    | 13 KB        | 3 KB       | 4.1x  |

**The honest summary: this is a bandwidth optimization, not a latency fix.** On
a healthy connection all three complete in roughly 0.1s either way, so the
transfer time saved is negligible. What it buys is a 3-4x cut in bytes pulled
on every poll cycle, and correspondingly more headroom against the request
timeout on a slow or contended link.

That timeout is worth knowing about: it is hardcoded to 30 seconds in the same
file (`httpClient.Timeout = TimeSpan.FromSeconds(30)`) and is not exposed as
configuration.

## Patch 2: stop the throttle deadlocking the poll loop

**File:** `FeedCord/src/Infrastructure/Http/CustomHttpClient.cs`

Symptom: the worker completes its startup probe, emits a burst of image-scrape
warnings, and then goes permanently silent. The container stays up and healthy,
the process sits at ~0% CPU with no open sockets, and the per-cycle
`Batch Run for <id> finished` line never appears even once. Only a restart
clears it, and it re-hangs on the first poll.

Cause: `Startup.cs` registers a single `SemaphoreSlim(ConcurrentRequests)` as a
**singleton**, shared by every `CustomHttpClient`. Upstream then acquires it
re-entrantly:

- `GetAsyncWithFallback` takes a permit and holds it for the whole method.
- Still holding it, on any non-2xx it calls `TryAlternativeAsync`, which takes
  a **second** permit.
- That in turn calls `GetRobotsUserAgentsAsync` -> `FetchRobotsContentAsync`,
  which takes a **third**.

`SemaphoreSlim` is not reentrant. `FeedManager.CheckForNewPostsAsync` fans out
over every feed with `Task.WhenAll`, so once `ConcurrentRequests` tasks are each
holding a permit inside `GetAsyncWithFallback`, the first one to see a non-2xx
blocks forever waiting for a permit that only the blocked tasks can release.
Every feed stops. Note this is not a network stall -- the hardcoded 30s
`httpClient.Timeout` never fires because no request is outstanding.

The same file also released the semaphore twice on several paths
(`TryAlternativeAsync` released on each success path *and* again in its
`finally`; `PostAsyncWithFallback` released at line 80 *and* in its `finally`),
so the permit count drifted upward over time and the throttle stopped bounding
anything.

The patch routes every outbound request through one of three leaf helpers --
`SendThrottledAsync`, `PostThrottledAsync`, `FetchRobotsContentAsync` -- each of
which acquires a permit immediately *before* its own `try` and releases it once
in `finally`. No permit is ever held across a call that acquires another, and
each acquire has exactly one matching release. `GetAsyncWithFallback` and
`TryAlternativeAsync` no longer touch the semaphore at all.

`PostAsyncWithFallback` also blocked on `.Result` inside an async method while
holding a permit; that is now awaited.

### Verifying

Old and new images run side by side against the same feed set at a 1-minute
interval, counting completed cycles:

```sh
docker logs <container> 2>&1 | grep -c 'Batch Run'
```

Over six minutes the pre-patch image completed 0 cycles with its network
counters frozen after the first fetch; the patched image completed 3 with
traffic climbing throughout.

### Upstream

This is an upstream bug, not something the compression patch introduced --
though enabling `Accept-Encoding` changes the client's header fingerprint, and
the Cloudflare-fronted origins in this feed set are the ones most likely to
answer with a non-2xx, which is the path into the deadlock.

## Patch 3: survive malformed items in a feed

**Files:** `FeedCord/src/Services/Helpers/PostBuilder.cs`,
`FeedCord/src/Services/RssParsingService.cs`

Two commits. The first is a cherry-pick of upstream PR
[#97](https://github.com/Qolors/FeedCord/pull/97) by Morgyn, unmodified,
carried here because the PR has sat open since 2026-05-30 with no maintainer
response. It null-guards the two dispatch checks in `TryBuildPost`:

```csharp
if (feed.Link?.Contains("reddit.com") == true)
else if (post.Id?.Contains("gitlab.com") == true)
```

An RSS item with no `<guid>` leaves `post.Id` null, and the unguarded
`.Contains` threw a `NullReferenceException`.

The second commit is local. It addresses what #97 does not: the cost of *any*
per-item exception, not just those two. `ParseRssFeedAsync` wrapped its whole
item loop in one try/catch returning an empty list, so a single bad entry
discarded **every** post from that feed for that cycle. The per-item work now
sits in its own try/catch -- a failed entry is logged and skipped, the rest of
the feed still returns, and a summary line reports how many were dropped. The
outer catch is unchanged and still covers a document that cannot be read.

This was never silent data loss: `FeedManager.CheckSingleFeedAsync` only
advances `LastPublishDate` when it actually sees posts, so a discarded batch is
retried on the next cycle. But the feed stays fully blocked for as long as the
bad item sits in its window, which looks exactly like the publisher having gone
quiet.

### Verifying

A synthetic three-item feed whose middle entry has no `<guid>`, served over
HTTP to three builds:

| build | items parsed | whole-feed NRE | items skipped |
| ----- | ------------ | -------------- | ------------- |
| pre-patch | 0 of 3 | yes, once per cycle | -- |
| isolation only | 2 of 3 | no | 1 |
| both commits | 3 of 3 | no | 0 |

Against the real 22-feed set both commits are a no-op: 22/22 probed, zero
parse errors, zero skips.

### Note on the feed set

Nothing in the deployed feed list currently triggers this. All 22 feeds carry a
channel-level `<link>`, and no item is missing `guid` or `link`. The patch is
insurance against a third-party publisher shipping a malformed entry, whose
symptom would otherwise be one feed quietly going dark.

## A known upstream issue this does NOT fix

If you are here because a feed stopped posting, this is the more likely cause.

**A feed that fails once at startup is never polled again for the lifetime of
the process.** The relevant chain:

- `FeedWorker` guards initialization with `_isInitialized`, so
  `InitializeUrlsAsync()` runs exactly once per process.
- `FeedManager.GetSuccessCount()` calls `TestUrlAsync(url)` and, on failure,
  `continue`s — the URL is never inserted into `_feedStates`.
- `FeedManager.CheckForNewPostsAsync()` iterates `_feedStates` only.

So the poll loop has no knowledge of a URL that failed its one-shot probe.
There is no retry, no backoff, and no re-probe. Only a container restart brings
it back, and only if the origin happens to be healthy at that moment. The sole
symptom is the startup line reporting a lower count than you configured:

```
Tested successfully for N out of M Urls in Configuration File
```

In practice the failures seen on this deployment were not slow transfers at
all. They were bot challenges from a Cloudflare-fronted origin — `403` with
`cf-mitigated: challenge`, and the 30s being consumed by the challenge rather
than by any payload. Compression cannot help with that, and neither can any
change confined to the HTTP handler. Those feeds stay dropped until restart.

Fixing this properly means re-probing failed URLs on the poll cycle instead of
discarding them at startup. That is a larger change than this fork carries.

## Upstreaming

The compression patch is a reasonable candidate to send upstream: it is a small
strict improvement with no new configuration surface. It has not been
submitted. The startup-probe issue above is worth an issue report in its own
right, and is the more valuable of the two.

## Building

The `Dockerfile` lives in the `FeedCord/` subdirectory, so that subdirectory,
not the repo root, is the build context:

```sh
./rebuild.sh                      # build the image
./rebuild.sh /path/to/composedir  # build, then `docker compose up -d feedcord`
```

Default image tag is `feedcord:local-gzip`; override with `FEEDCORD_IMAGE`, and
the service name with `FEEDCORD_SERVICE`.

Feeds and the Discord webhook come from an `appsettings.json` mounted at
`/app/config/appsettings.json` at runtime. It is deliberately not in this repo.

If you run this under Watchtower, disable it for the container
(`com.centurylinklabs.watchtower.enable=false`) — the image is built locally
and has no registry to be pulled from, so Watchtower would log a failed check
on every pass.

## Syncing with upstream

```sh
git fetch upstream
git rebase upstream/master
git push --force-with-lease origin master
```

The patch is one commit at the tip of `master`, so a rebase either replays it
cleanly or conflicts only within the `ConfigurePrimaryHttpMessageHandler` call.
`upstream` is fetch-only here; its push URL is deliberately set to `DISABLED`.

Because the rebase rewrites `master`, a deployed clone updates with a reset
rather than a fast-forward pull:

```sh
git fetch origin
git reset --hard origin/master
./rebuild.sh /path/to/composedir
```
