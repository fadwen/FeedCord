# Fork notes

This is a fork of [Qolors/FeedCord](https://github.com/Qolors/FeedCord) carrying
one local patch. `master` is upstream `master` plus that single commit, and the
image is built from source rather than pulled from a registry.

Everything else tracks upstream unchanged.

## The patch: negotiate compression on feed requests

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
