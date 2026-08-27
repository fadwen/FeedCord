# Fork notes

This is a fork of [Qolors/FeedCord](https://github.com/Qolors/FeedCord) carrying
six local patches. `master` is upstream `master` plus those commits, and the
image is built from source rather than pulled from a registry. Several are
cherry-picked from other forks and keep their original author.

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

The same file also mismatched acquire and release counts on every path
through `TryAlternativeAsync`, which acquired once per attempt but released
only once inline plus once in its `finally`:

| path | acquires | releases | net |
| ---- | -------- | -------- | --- |
| succeeds on attempt 1 | 1 | 2 | **+1 (over-release)** |
| succeeds on attempt 2 | 2 | 2 | 0 |
| succeeds on robots attempt *k* | 2 + *k* | 2 | **leaks *k*** |
| all attempts fail | 2 + *N* | 1 | **leaks 1 + *N*** |

`PostAsyncWithFallback` similarly released at line 80 *and* in its `finally`.

This leak is what made the hang inevitable rather than merely possible. Pure
reentrancy needs `ConcurrentRequests` feeds to fail at the same moment; the
leak instead shrinks the pool a little on every trip through the fallback path
until it reaches zero, at which point even a single feed blocks. It explains
the observed pattern -- healthy for a while after each restart, then dead --
better than reentrancy alone.

Credit for spotting the leak framing goes to
[sepperlot](https://github.com/sepperlot/FeedCord), who fixed the same bug
independently by keeping one permit for the whole fallback sequence and
stripping the nested acquires. Both approaches are correct. This fork keeps the
narrower one so a permit is only held for the duration of a single request.

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

## Patch 4: make restarts stop losing posts

**File:** `FeedCord/src/Infrastructure/Workers/FeedWorker.cs`

Two commits cherry-picked unchanged from
[sepperlot/FeedCord](https://github.com/sepperlot/FeedCord), retaining their
authorship:

- `157303fd` -- write `feed_dump.csv` via read-merge-write instead of
  append-only, so the file stops growing with duplicate historical rows and
  multiple configured Instances cannot clobber each other's saved rows.
- `fb27bb45` -- persist after every check cycle rather than only from
  `OnShutdown`, and write the timestamp as ISO 8601. `OnShutdown` fires from
  `ApplicationStopping`, which never runs if the process is killed or hangs --
  exactly the failure mode Patch 2 addresses. An ungraceful death now costs at
  most one interval of progress.

The ISO timestamp matters because `CsvReader` parses with
`CultureInfo.InvariantCulture` while the old write used `{DateTime.Now}`, which
formats using the *current* culture. That round-trips only as long as the
container's culture stays invariant.

### The part that is not in the code

`SaveDataToCsv` writes to `AppContext.BaseDirectory`, which is `/app` in the
container. Mounting only `appsettings.json` leaves the watermark in the
container's writable layer, where every `docker compose up -d` recreate --
including every `rebuild.sh` run -- destroys it. Feeds with no persisted entry
baseline to `DateTime.Now`, so everything published during the downtime is
treated as already-seen.

**Setting `PersistenceOnShutdown: true` on its own therefore changes nothing
under Docker.** The compose service must also mount the file, and it must be
created in advance and writable by `APP_UID`. See `FEEDCORD_DEPLOYMENT.md`.

### Verifying

Two containers, identical but for the setting, against a local feed. Baseline a
one-item feed, stop, publish a second item while both are down, restart:

| arm | new posts detected on restart | posted |
| --- | ---------------------------- | ------ |
| `PersistenceOnShutdown: false` (previous config) | none -- listed as "extracted", classified not-new | 0 |
| `true` + mounted `feed_dump.csv` | 1 | 1 |

The watermark also advanced between cycles (`23:13:23` then `23:14:46`),
confirming the per-cycle save rather than shutdown-only.

## Patch 5: space Discord posts in the HTTP client

**Files:** `FeedCord/src/Infrastructure/Http/CustomHttpClient.cs`,
`FeedCord/src/Infrastructure/Notifiers/DiscordNotifier.cs`

`fb0d5748` cherry-picked from [Kamdzy/FeedCord](https://github.com/Kamdzy/FeedCord),
retaining authorship: a dedicated gate that serialises Discord posts and spaces
them 2 seconds apart, independent of the general request throttle.

Adapted on the way in. This fork routes every outbound request through a leaf
helper holding exactly one throttle permit, so the gate sits on
`PostThrottledAsync` -- the single POST choke point -- which also spaces the
channel-type fallback retry, not just the initial post. The gate is taken
*before* the throttle permit and never the other way round, so the two
semaphores cannot deadlock against each other.

The follow-up commit is local. `DiscordNotifier` slept a fixed 10 seconds after
every post, carrying a TODO saying the concern belonged in `CustomHttpClient`.
With the gate in place that sleep is redundant, and because 10s dominated the
2s gate it would have kept the cherry-picked change from ever binding -- which
is its status in the source fork, where the sleep is still present. Removing it
takes a backlog of N posts from 10N seconds to 2N, and stops a cycle that found
one post from paying 10 seconds before finishing.

This is not urgent on its own -- the deployment has never logged a Discord post
failure -- but it pairs with Patch 4: persisted state means a restart can now
deliver a genuine backlog, which is exactly when webhook rate limits bite.

## Patch 6: recognise posts by identity, not by publish date

**Files:** `FeedCord/src/Services/FeedManager.cs`,
`FeedCord/src/Common/SeenPostSet.cs`, `FeedCord/src/Common/FeedState.cs`,
`FeedCord/src/Common/Post.cs`, `FeedCord/src/Common/ReferencePost.cs`,
`FeedCord/src/Helpers/Csv.cs`, `FeedCord/src/Helpers/CsvReader.cs`,
`FeedCord/src/Infrastructure/Workers/FeedWorker.cs`,
`FeedCord/src/Services/Helpers/PostBuilder.cs`,
`FeedCord/src/Infrastructure/Parsers/YoutubeParsingService.cs`

Symptom: the poll loop runs normally, every cycle completes, and the log says
`No new posts found` while the feed plainly has new items. Unlike Patch 2 this
is not a stall — the batch runs, the feed is fetched and parsed, and the posts
are then classified as already-seen.

Reported upstream as [#94](https://github.com/Qolors/FeedCord/issues/94) by
jdcoolha, with the same behaviour independently hit by Corban-Lee, Lucifer1590,
w3bprinz and Cealgair.

### Cause

Upstream kept exactly one piece of per-feed state: a high-water publish date.

```csharp
var freshlyFetched = posts.Where(p => p?.PublishDate > feedState.LastPublishDate).ToList();

if (freshlyFetched.Any())
{
    feedState.LastPublishDate = freshlyFetched.Max(p => p!.PublishDate);
```

There is no record of *which* posts were sent, only of how far the clock got.
That makes three separate things go wrong, all from these two lines:

1. **A back-dated item is invisible forever.** If a publisher adds an item whose
   `pubDate` is behind the newest one already seen, it can never satisfy `>`.
   No amount of waiting or restarting recovers it, because the watermark only
   moves forward.
2. **Items sharing a timestamp are dropped after the first.** The comparison is
   strictly `>`, so a feed whose `pubDate`s carry no time component — every item
   stamped `00:00:00` — posts exactly one item per day no matter how many it
   publishes. This is jdcoolha's original diagnosis and it is correct.
3. **The watermark jumps to the newest item in the batch, not the oldest.**
   Anything that arrives later but is dated between those two is already behind
   the line.

A feed only needs to be slightly irregular to hit (1). The one Cealgair reported,
`https://kaprestridge.github.io/pokebeach-news-feed/feed.xml`, is a GitHub Pages
republish of a scraper whose publish workflow fires at uneven intervals — an
11-hour gap between the `05:45Z` and `17:04Z` runs on 2026-08-27. The newest item
in that build is stamped `Thu, 27 Aug 2026 09:03:00 GMT` against a
`Last-Modified` of `17:05:20 GMT`, so it appeared roughly eight hours after its
own publish time. Uniform lag alone loses nothing; *variable* lag across a feed
that publishes several times a day reliably delivers an item behind a date some
other item has already pushed the watermark to.

### The change

`FeedState` gains a `SeenPostSet`: the identities of posts already handled for
that feed, bounded at 500 entries and evicted oldest-first. Identity is the
feed's own — `<guid>` in RSS, `<id>` in Atom — falling back to the link, and then
to title plus date for a feed that supplies neither. `Post` carries an `Id` for
this purpose, populated in all four `PostBuilder` paths and in the YouTube
parser.

A post is new when its identity has not been seen before. The date survives only
as a *floor*, and its job is now much smaller: stop a newly added feed dumping
its entire backlog on first sight.

The floor deliberately **does not** advance while the identity set still
remembers everything it has been told, which for any realistic feed is always.
Advancing it eagerly would re-create the original bug in miniature — one
transiently truncated response would drag the floor up to the newest item it
contained and strand everything published behind that. Only once the set is full,
and entries genuinely start being evicted, does the floor take over as a backstop
and advance to the oldest item the feed still carries. Anything older than that
has fallen out of the document and cannot be offered to us again anyway.

Identities are recorded *before* `PostFilters` are evaluated. A post the filters
reject has still been seen, and re-testing it every cycle only repeats the same
`omitted because it does not comply` line forever.

Two smaller corrections come with it, both in code this patch had to touch:

- `FeedWorker.SaveDataToCsv` persisted `LastRunDate = DateTime.Now` rather than
  the feed's own watermark. A restart therefore baselined every feed to the
  moment of the last save, so anything a publisher back-dated behind that instant
  was invisible from then on. Verified against `upstream/master` — this is
  upstream's, faithfully carried through the Patch 4 cherry-pick, not something
  Patch 4 introduced. It now saves `value.LastPublishDate`.
- `feedState.ErrorCount` was only cleared on a cycle that found new posts, so a
  healthy but quiet feed accumulated errors against it and `EnableAutoRemove`
  could drop it for being quiet. It now clears on any successful fetch.

`FeedManager` also read `feed_dump.csv` by relative path while `FeedWorker` wrote
it under `AppContext.BaseDirectory`. Those coincide under Docker (`WORKDIR /app`)
but not when the process is started from another directory; both now go through
`CsvReader.DefaultFilePath`.

### The file format

`feed_dump.csv` gains a variable-length tail of post identities:

```
url,isYoutube,lastPublishDate[,seenId...]
```

Identities are publisher-supplied strings that can legitimately contain commas
and quotes, which the old `Split(',')` would mangle, so rows are now written and
read as RFC 4180 (`FeedCord/src/Helpers/Csv.cs`). The reader is line-oriented, so
identities are flattened to a single line before being stored.

**No migration is needed.** A file written before the tail existed has exactly
three columns and loads with an empty identity set. Its stored date still gates
the first cycle after the upgrade, so nothing re-posts a backlog; from the second
cycle on the identity set is doing the work.

### Verifying

A synthetic feed served over HTTP to two containers at a 1-minute interval, one
built from upstream `master` plus Patches 1-5 (`feedcord:local-gzip`) and one
with this patch. Both start against a feed holding a single item dated ten
minutes in the past, so both correctly baseline and post nothing. Items are then
added one at a time, a cycle apart, and the Discord webhook is pointed at a local
sink that records what each arm sent.

Writing T0 for the moment the containers started:

| item added | its `pubDate` | why it matters | pre-patch | patched |
| ---------- | ------------- | -------------- | --------- | ------- |
| A | T0 - 10m | already there at startup | not posted | not posted |
| B | T0 + 5m | ordinary new item | posted | posted |
| C | T0 + 2m | **back-dated behind B** | **dropped** | posted |
| D | T0 + 10m | ordinary new item | posted | posted |
| E | T0 + 10m | **same timestamp as D** | **dropped** | posted |

`old  2 posts` / `new  4 posts`. C is Cealgair's report and E is jdcoolha's, and
the pre-patch arm logs `No new posts found` for both while continuing to complete
every cycle normally. The patched arm sent each item exactly once; repeated
cycles over the following minutes added no duplicates.

Persistence was checked separately, with `PersistenceOnShutdown: true` and
`feed_dump.csv` mounted:

- Identities accumulate in the row as posts are sent, and the date column holds
  the feed's watermark rather than the save time -- `2026-08-27T22:21:51` for a
  save made at `22:21:56`, which is the pre-patch bug not reproducing.
- Restarting the container re-posted nothing (3 posts before, 3 after).
- A hand-written legacy three-column row dated after every item in the feed --
  the realistic upgrade shape, since the old code stored `DateTime.Now` -- loaded
  cleanly and posted nothing.
- A `<guid>` of `urn:fctest:na,sty"quote` round-tripped as
  `"urn:fctest:na,sty""quote"` and was still recognised after a restart,
  exercising the RFC 4180 path that the old `Split(',')` would have mangled.

The image builds clean on `mcr.microsoft.com/dotnet/sdk:9.0`: 0 errors, and the
same 2 pre-existing warnings (`CS8625` on `Post.Labels`, `CS8604` in
`RssParsingService`) as before the patch.

### What this still does not fix

- **Back-dating deeper than the feed's own window**, once the identity set has
  filled and the floor has started advancing. In practice that needs 500 distinct
  posts from one feed *and* an item dated behind the oldest entry the feed still
  carries.
- **The YouTube path**, which reads only the single most recent `<entry>` per
  fetch. Identity tracking is correct there but has nothing extra to work with,
  so a back-dated video is still missed. That is a limitation of
  `FetchYoutubeAsync`, not of the detection logic.
- **Items whose `pubDate` fails to parse** sit at `default(DateTime)` and remain
  below any floor, so they never post. Making them post on identity alone would
  be defensible, but it risks a burst on first sight of such a feed and is left
  out of this patch deliberately.

### Upstream

Offered as [#101](https://github.com/Qolors/FeedCord/pull/101). It fixes a
five-reporter issue with no new configuration surface and the state file stays
backward compatible, but it is a larger change than Patches 1-3 and touches the
file format, so it may well need discussion.

The submitted version differs in one place: upstream has no read-merge-write
persist, so its `SaveDataToCsv` writes the identity tail in the existing
append-only, shutdown-only shape.

## A known upstream issue this does NOT fix

If you are here because a feed stopped posting, check Patch 2 (the loop stalls
entirely) and Patch 6 (the loop runs but classifies everything as old) first.
If neither fits, this is the remaining candidate.

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

Four of the six patches have been offered back to upstream, each as a single
commit on a branch off `upstream/master`:

| Patch | Upstream PR |
| ----- | ----------- |
| 1 — negotiate compression | [#99](https://github.com/Qolors/FeedCord/pull/99) |
| 2 — throttle re-entrancy | [#98](https://github.com/Qolors/FeedCord/pull/98) |
| 3 — isolate item parsing | [#100](https://github.com/Qolors/FeedCord/pull/100) |
| 6 — identity-based detection | [#101](https://github.com/Qolors/FeedCord/pull/101) |

Patches 4 and 5 are cherry-picks from other forks and are theirs to submit.

Patch 6 needed adapting on the way out: it sits on top of Patch 4's
read-merge-write persist here, but upstream's `SaveDataToCsv` is still
append-only and still runs only from `OnShutdown`, so the submitted version
writes the identity tail in that shape instead. The PR notes that pairing it
with a read-merge-write would be worth doing if a per-cycle persist ever lands
upstream.

The startup-probe issue above is worth an issue report in its own right, and is
arguably more valuable than any of these.

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
