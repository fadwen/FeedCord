
![FeedCord Banner](https://github.com/Qolors/FeedCord/blob/master/FeedCord/docs/images/FeedCord.png)
---

# FeedCord: Self-hosted RSS Reader for Discord

FeedCord is designed to be a 'turn key' automated RSS feed reader with the main focus on Discord Servers. 

Use it for increasing community engagement and activity or just for your own personal use. The combination of FeedCord and Discord's Forum Channels can really shine to make a vibrant news feed featuring gallery-style display alongside custom threads, creating an engaging space for your private community discussions.

---

## About this fork

This is a fork of **[Qolors/FeedCord](https://github.com/Qolors/FeedCord)**, and it exists for exactly one reason: upstream has gone quiet. There have been no commits since [1 July 2025](https://github.com/Qolors/FeedCord/commits/master), community pull requests have sat unreviewed for months, and the open issue asking [whether the project will continue](https://github.com/Qolors/FeedCord/issues/95) has not been answered.

**All credit for FeedCord belongs to [Qolors](https://github.com/Qolors).** The idea, the design, and essentially all of the code are theirs — this fork is a small stack of patches sitting on top of their work. Thank you for building it, and for releasing it under the MIT license so the rest of us could keep it running.

This is not a competing project and is not trying to become one. Fixes made here are offered back upstream wherever they apply cleanly — currently [#98](https://github.com/Qolors/FeedCord/pull/98) and [#99](https://github.com/Qolors/FeedCord/pull/99) — and if upstream picks up again, that is the better home for this work.

What this fork changes is summarised in the [fork changelog](#fork-changes) and documented in full in [PATCHES.md](PATCHES.md).

---

### An example of what FeedCord can bring to your server

---

![FeedCord Gallery 1](https://github.com/Qolors/FeedCord/blob/master/FeedCord/docs/images/gallery1.png)

![FeedCord Gallery 2](https://github.com/Qolors/FeedCord/blob/master/FeedCord/docs/images/gallery2.png)

A showing of one channel. Run as many of these as you want!


---

## Running FeedCord

FeedCord is very simple to get up and running. It only takes a few steps:

- Create a Discord Webhook
- Create and Edit a local file or two

Provided below is a quick guide to get up and running.


## Quick Setup

### 1. Create a new folder with a new file named `appsettings.json` inside with the following content:

```json
{
  "Instances": [
    {
      "Id": "My First News Feed",
      "YoutubeUrls": [
        ""
      ],
      "RssUrls": [
        ""
      ],
      "Forum": false,
      "DiscordWebhookUrl": "...",
      "RssCheckIntervalMinutes": 25,
      "EnableAutoRemove": false,
      "Color": 8411391,
      "DescriptionLimit": 250,
      "MarkdownFormat": false,
      "PersistenceOnShutdown": true
    }
  ],
  "ConcurrentRequests": 40
}
```
There is currently 17 properties you can configure. You can read more in depth explanation of the file structure as well as view all properties and their purpose [here](https://github.com/Qolors/FeedCord/blob/master/FeedCord/docs/reference.md)

---

### 2. Create a new Webhook in Discord (Visual Steps Provided)

![Discord Webhook](https://github.com/Qolors/FeedCord/blob/master/FeedCord/docs/images/webhooks.png)


### Quick Note

Be sure to populate your `appsettings.json` *"DiscordWebhookUrl"* property with your newly created Webhook

Before you actually run FeedCord, make sure you have populated your `appsettings.json` with RSS and YouTube feeds.

**RSS Feeds**

- For new users that aren't bringing their own list check out [awesome-rss-feeds](https://github.com/plenaryapp/awesome-rss-feeds) and add some that interest you
- Each url is entered by line seperating by comma. It should look like this in your `appsettings.json` file:

```json
"RssUrls": [
       "https://examplesrssfeed1.com/rss",
       "https://examplesrssfeed2.com/rss",
       "https://examplesrssfeed3.com/rss",
     ]
```

**YouTube Feeds**

- You can bring your favorite YouTube channels as well to be notified of new uploads
- FeedCord parses from the channel's base url so simply navigate to the channel home page and use that url.
- Example here if I was interested in Unbox Therapy & Tyler1:

***NOTE***

If a YouTube link keeps failing at retrieving the RSS Link - Directly use the xml formatted YouTube link. It is more reliable.

The format for that looks like: `"https://www.youtube.com/feeds/videos.xml?channel_id={YOUR_CHANNEL_ID_HERE}"`

You can use online web tools like [tunepocket](https://www.tunepocket.com/youtube-channel-id-finder/?srsltid=AfmBOorSH1Ye9r1erCzY2qaqV_pUa23U8wG-DeAMAhGfGZ9dbMY5RE2j) to get the Id for the channel.

```json
"YoutubeUrls": [
       "https://www.youtube.com/@unboxtherapy",
       "https://www.youtube.com/@TYLER1LOL",
       "https://www.youtube.com/feeds/videos.xml?channel_id={YOUR_CHANNEL_ID_HERE}"
     ]
```

### Running FeedCord

Now that your file is set up, you have two ways to run FeedCord

### Docker (Recommended)

```
docker pull qolors/feedcord:latest
```
Be sure to update the volume path to your `appsettings.json` 
```
docker run --name FeedCord -v "/path/to/your/appsettings.json:/app/config/appsettings.json" qolors/feedcord:latest
```

> **Fork note — mount `feed_dump.csv` if you set `PersistenceOnShutdown: true`.**
>
> The watermark of the last-seen post per feed is written to
> `AppContext.BaseDirectory`, which is `/app` in the container. With only
> `appsettings.json` mounted, that file lives in the container's writable layer
> and is destroyed by any `docker run --rm`, `docker compose up --force-recreate`,
> or image update. On the next start, feeds with no persisted entry baseline
> their watermark to "now", so everything published while the container was down
> is treated as already-seen and never posted.
>
> Enabling the setting alone does not survive a redeploy. Mount the file too, and
> create it beforehand so Docker does not create a directory in its place:
>
> ```sh
> touch /path/to/feed_dump.csv
> docker run --name FeedCord \
>   -v "/path/to/appsettings.json:/app/config/appsettings.json" \
>   -v "/path/to/feed_dump.csv:/app/feed_dump.csv" \
>   feedcord
> ```
>
> The file must be writable by the container's user (`APP_UID`, 1654 by default).
> This fork also saves the watermark after every check cycle rather than only on
> a graceful shutdown, so a container that is killed or hangs loses at most one
> interval's progress instead of everything since the last clean stop.

### Build From Source

Install the [.NET SDK](dotnet.microsoft.com/download)

Clone this repo
```
git clone https://github.com/Qolors/FeedCord
```
Change Directory
```
cd FeedCord
```
Restore Dependencies
```
dotnet restore
```
Build
```
dotnet build
```
Run with your `appsettings.json` (provide your own path)
```
dotnet run -- path\to\your\appsettings.json
```


With the above steps completed, FeedCord should now be running and posting updates from your RSS feeds directly to your Discord channel.

> **Fork note — posting cadence.** Upstream sleeps a fixed 10 seconds after every
> post, including the last one in a batch. This fork instead spaces posts 2
> seconds apart inside the HTTP client, which is where the original code's own
> TODO said the concern belonged. A backlog of N posts takes roughly 2N seconds
> rather than 10N, which matters when persisted state lets a restart deliver a
> real backlog.

---

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Fork changes

Changes carried in this fork that are not in upstream `master`. Each has a full technical write-up in [PATCHES.md](PATCHES.md).

<details open>
 <summary>Unreleased — on top of upstream [3.0.0]</summary>

### Fixed

- **The poll loop could stall permanently.** A singleton `SemaphoreSlim` was acquired re-entrantly in `CustomHttpClient`, so the first non-2xx response could deadlock every feed until the container was restarted. ([`3df4626`](https://github.com/fadwen/FeedCord/commit/3df4626)) — offered upstream as [#98](https://github.com/Qolors/FeedCord/pull/98)
- **Feed items missing `link` or `id` threw during parsing.** ([`a6507a5`](https://github.com/fadwen/FeedCord/commit/a6507a5)) — cherry-picked from [Morgyn](https://github.com/Morgyn)'s upstream PR [#97](https://github.com/Qolors/FeedCord/pull/97)
- **One malformed item discarded every post from that feed for the cycle.** Item parsing is now isolated per entry, so a bad entry is logged and skipped instead of blocking the feed. ([`ca54997`](https://github.com/fadwen/FeedCord/commit/ca54997))
- **`feed_dump.csv` was destroyed by container recreation, and multiple Instances clobbered each other's rows.** Persistence is now a semaphore-guarded read-merge-write. ([`0c7d6f3`](https://github.com/fadwen/FeedCord/commit/0c7d6f3)) — cherry-picked from [Sepperlot](https://github.com/sepperlot) ([`157303f`](https://github.com/sepperlot/FeedCord/commit/157303fde5cea71fb5e9cbaf60154cc31fc17809))
- **State was saved only on a graceful shutdown.** It is now persisted after every check cycle, so an ungraceful stop loses at most one interval's progress. ([`298b4ab`](https://github.com/fadwen/FeedCord/commit/298b4ab)) — cherry-picked from [Sepperlot](https://github.com/sepperlot) ([`fb27bb4`](https://github.com/sepperlot/FeedCord/commit/fb27bb45dce83169a96c3f19a626a110060f164b))

### Changed

- **Feed requests now negotiate compression.** `AutomaticDecompression` is enabled, so `Accept-Encoding` is sent and feeds transfer 3–4x smaller. ([`25fdb0b`](https://github.com/fadwen/FeedCord/commit/25fdb0b)) — offered upstream as [#99](https://github.com/Qolors/FeedCord/pull/99)
- **Discord posts are spaced 2 seconds apart in the HTTP client** instead of 10 seconds in the notifier, taking a backlog of N posts from roughly 10N seconds to 2N. ([`8b3b4d0`](https://github.com/fadwen/FeedCord/commit/8b3b4d0), cherry-picked from [Kamdzy](https://github.com/Kamdzy) ([`fb0d574`](https://github.com/Kamdzy/FeedCord/commit/fb0d5748e2f23c6dc2509a9ff0ef1d179af81656)), and [`4c591ea`](https://github.com/fadwen/FeedCord/commit/4c591ea) removing the old sleep)
- **`feed_dump.csv` timestamps are written as ISO 8601.** Old and new rows both still read correctly, so no migration is needed. ([`298b4ab`](https://github.com/fadwen/FeedCord/commit/298b4ab))

</details>


## Upstream releases

Releases from [Qolors/FeedCord](https://github.com/Qolors/FeedCord), preserved as-is.

<details>
 <summary>[3.0.0] - 2025-02-10</summary>

### Added

- Restart persistence to catch up on missed posts if it had shutdown
- UserAgent cycling for failed get requests with retry attempts
- Multiple retry attempts on getting a post image
- Control over allowed concurrent HTTP requests FeedCord can make
- Separate handling of Reddit Feeds
- Markdown Support
- Building from source

### Changed

- README
- Large codebase refactoring

### Fixed

- Atom Feeds not returning a description
- Failed posting to Discord due to title length

</details>

<details>
 <summary>[2.1.1] - 2024-04-25</summary>

 ### Added

 - Added author being sourced from feed items
 - Added GZIP support for feeds
 
</details>


<details>
 <summary>[2.1.0] - 2024-02-28</summary>

 ### Added
 
 - Added Support for grabbing multiple new posts if the feed has multiple new posts since the last check.

 ### Changed
 
 - Improved Documentation for easier setup and understanding
 - Improved Logging for better readability
 - Posting now has a hard-coded 10 second buffer so large feeds respect Discord's rate limits

</details>


<details>
  <summary>[2.0.1] - 2024-02-19</summary>

  ### Added

  - Added Support for Reddit Feed & Better Atom Parsing Feeds

</details>

<details>
  <summary>[2.0.0] - 2024-01-30</summary>

  ### Added

  - Added Support for Multiple Webhook Urls & Configurations
  - Added Support for Discord's Forum Channels
  
  ### Changed

  - Configuration File formatting has changed to support multiple Webhook URLs
  - Slight improvements to Logging
  - Some Configuration properties are now optional rather than required

</details>


<details>
  <summary>[1.3.0] - 2024-01-20</summary>

  ### Added

  - Added Description Length Configuration

  ### Changed

  - Improved RSS & ATOM Parsing with implementing [FeedReader](https://github.com/arminreiter/FeedReader) library

  ### Fixed

  - RSS/ATOM Feeds returning errors because of parsing issues

</details>


<details>
  <summary>[1.2.1] - 2024-01-17</summary>

  ### Changed

  - Made Youtube URLs an optional addition rather than required

</details>

<details>
  <summary>[1.2.0] - 2023-10-25</summary>
  
  ### Added

  - Added Support for Youtube Channel Feeds in configuration file.
  - Added an optional Auto Remove option in configuration file for bad URL Feeds to get booted out of the list after multiple failed attempts.

  ### Changed

  - Improved container logging messages for better readability.

  ### Fixed

  - Color setting in configuration now properly works for the embed message
  - Fixed the handling of errors and removed from logging to reduce spam.
  - Fixed a known logging index error.

</details>

<details>
  <summary>[1.1.0] - 2023-10-16</summary>
  
  ### Added

  - Broke up `RssProcessorService` class to follow SOLID principles, adding a new service class `OpenGraphService` to handle meta tags.
  - Added `Helper` namespace & `StringHelper` class, which includes the `StripTags` method for potential reuse and improved organization.

  ### Changed

  - Enhanced the RSS feed background service for more efficient feed checks, reducing chances of delays.
  - Customized the `HttpClient` to set default request headers, ensuring better compatibility with certain RSS feeds.
  - Refined feed processing logic to include concurrent processing, beneficial for users with a large number of RSS feeds.
  - ReadMe to show this change log and multiple OS images.

  ### Fixed

  - Improved RSS feed initialization, ensuring only valid feeds are added to the tracking list.
  - Overhauled logs to not contain as much spam and allow for better readability.

</details>

<details>
  <summary>[1.0.0] - 2023-10-15</summary>
  
  ### Added
  - Initial Project Release

</details>


---
