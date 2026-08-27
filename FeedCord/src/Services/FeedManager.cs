using FeedCord.Common;
using FeedCord.Helpers;
using FeedCord.Services.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using FeedCord.Core.Interfaces;
using FeedCord.Services.Helpers;

namespace FeedCord.Services
{
    public class FeedManager : IFeedManager
    {
        private readonly bool _hasAllFilter = false;
        private readonly bool _hasFilterEnabled = false;
        private readonly Config _config;
        private readonly SemaphoreSlim _instancedConcurrentRequests;
        private readonly ICustomHttpClient _httpClient;
        private readonly ILogAggregator _logAggregator;
        private readonly ILogger<FeedManager> _logger;
        private readonly IRssParsingService _rssParsingService;
        private readonly Dictionary<string, ReferencePost> _lastRunReference;
        private readonly ConcurrentDictionary<string, FeedState> _feedStates;

        public FeedManager(
            Config config,
            ICustomHttpClient httpClient,
            IRssParsingService rssParsingService,
            ILogger<FeedManager> logger,
            ILogAggregator logAggregator)
        {
            _config = config;
            _httpClient = httpClient;
            _lastRunReference = CsvReader.LoadReferencePosts(CsvReader.DefaultFilePath);
            _rssParsingService = rssParsingService;
            _logger = logger;
            _logAggregator = logAggregator;
            _feedStates = new ConcurrentDictionary<string, FeedState>();
            _hasFilterEnabled = config.PostFilters?.Any() ?? false;
            _instancedConcurrentRequests = new SemaphoreSlim(config.ConcurrentRequests);

            //TODO --> this sets flag for 'all' in filters - this and all filter logic needs to be moved out of FeedManager and in to it's own helper/service
            if (_hasFilterEnabled && _config.PostFilters != null)
            {
                if (_config.PostFilters.Any(wf => wf.Url == "all"))
                    _hasAllFilter = true;
            }
        }
        public async Task<List<Post>> CheckForNewPostsAsync()
        {
            ConcurrentBag<Post> allNewPosts = new();

            var tasks = _feedStates.Select(async (feed) =>
                await CheckSingleFeedAsync(feed.Key, feed.Value, allNewPosts, _config.DescriptionLimit));

            await Task.WhenAll(tasks);

            _logAggregator.SetNewPostCount(allNewPosts.Count);

            return allNewPosts.ToList();
        }
        public async Task InitializeUrlsAsync()
        {
            var id = _config.Id;
            var validRssUrls = _config.RssUrls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToArray();

            var validYoutubeUrls = _config.YoutubeUrls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToArray();

            var rssCount = await GetSuccessCount(validRssUrls, false);
            var youtubeCount = await GetSuccessCount(validYoutubeUrls, true);
            var successCount = rssCount + youtubeCount;

            var totalUrls = validRssUrls.Length + validYoutubeUrls.Length;

            _logger.LogInformation("{id}: Tested successfully for {UrlCount} out of {TotalUrls} Urls in Configuration File", id, successCount, totalUrls);
        }

        public IReadOnlyDictionary<string, FeedState> GetAllFeedData()
        {
            return _feedStates;
        }
        private async Task<int> GetSuccessCount(string[] urls, bool isYoutube)
        {
            var successCount = 0;

            if (urls.Length == 0 || urls.Length == 1 && string.IsNullOrEmpty(urls[0]))
            {
                return successCount;
            }

            foreach (var url in urls)
            {
                var isSuccess = await TestUrlAsync(url);

                if (!isSuccess)
                {
                    continue;
                }

                if (_lastRunReference.TryGetValue(url, out var value))
                {
                    _feedStates.TryAdd(url, new FeedState
                    {
                        IsYoutube = isYoutube,
                        LastPublishDate = value.LastRunDate,
                        ErrorCount = 0,
                        SeenPosts = new SeenPostSet(value.SeenIds)
                    });

                    successCount++;

                    continue;
                }

                bool successfulAdd;

                if (isYoutube)
                {
                    successfulAdd = _feedStates.TryAdd(url, new FeedState
                    {
                        IsYoutube = true,
                        LastPublishDate = DateTime.Now,
                        ErrorCount = 0
                    });
                }
                else
                {
                    successfulAdd = _feedStates.TryAdd(url, new FeedState
                    {
                        IsYoutube = false,
                        LastPublishDate = DateTime.Now,
                        ErrorCount = 0
                    });
                }

                if (successfulAdd)
                {
                    successCount++;
                }

                else
                {
                    _logger.LogWarning("Failed to initialize URL: {Url}", url);
                }
            }

            return successCount;
        }
        private async Task<bool> TestUrlAsync(string url)
        {
            try
            {
                await _instancedConcurrentRequests.WaitAsync();

                var response = await _httpClient.GetAsyncWithFallback(url);

                if (response is null)
                {
                    _logAggregator.AddUrlResponse(url, -99);
                    return false;
                }

                _logAggregator.AddUrlResponse(url, (int)response.StatusCode);

                response.EnsureSuccessStatusCode();

                return true;
            }
            catch (HttpRequestException ex)
            {
                _logAggregator.AddUrlResponse(url, (int)(ex.StatusCode ?? System.Net.HttpStatusCode.BadRequest));
            }
            catch (Exception)
            {
                _logger.LogWarning("Failed to instantiate URL: {Url}", url);
            }
            finally
            {
                _instancedConcurrentRequests.Release();
            }

            return false;
        }
        private async Task CheckSingleFeedAsync(string url, FeedState feedState, ConcurrentBag<Post> newPosts, int trim)
        {
            List<Post?> posts;

            try
            {
                await _instancedConcurrentRequests.WaitAsync();

                posts = feedState.IsYoutube ?
                    await FetchYoutubeAsync(url) :
                    await FetchRssAsync(url, trim);
            }
            catch (Exception ex)
            {
                HandleFeedError(url, feedState, ex);
                return;
            }
            finally
            {
                _instancedConcurrentRequests.Release();
            }

            var parsed = posts.Where(p => p is not null).Select(p => p!).ToList();

            if (parsed.Count < posts.Count)
            {
                _logger.LogWarning(
                    "Failed to parse {Count} post(s) from {Url}", posts.Count - parsed.Count, url);
            }

            // A post is new when this feed has never handed us its identity
            // before. The date is only a floor now, so an item that shows up
            // hours after its stated publish time -- or that shares a timestamp
            // with one already sent -- is still recognised.
            var freshlyFetched = parsed
                .Where(p => p.PublishDate > feedState.LastPublishDate)
                .Where(p => !feedState.SeenPosts.Contains(GetPostIdentity(p)))
                .ToList();

            if (parsed.Count > 0)
            {
                // The fetch worked, so the feed is healthy even on a cycle that
                // found nothing. This used to clear only when new posts turned
                // up, which left stale errors against a merely quiet feed and
                // let EnableAutoRemove drop it for being quiet.
                feedState.ErrorCount = 0;

                AdvanceFloor(feedState, parsed);
            }

            if (freshlyFetched.Any())
            {
                // Marked before the filters run: a post the filters reject has
                // still been seen, and re-testing it every cycle only repeats
                // the same log line forever.
                foreach (var post in freshlyFetched)
                {
                    feedState.SeenPosts.Add(GetPostIdentity(post));
                }

                foreach (var post in freshlyFetched)
                {
                    //TODO --> Implement Filter checking in to a helper/service & remove from FeedManager
                    if (_hasFilterEnabled && _config.PostFilters != null)
                    {
                        var filter = _config.PostFilters.FirstOrDefault(wf => wf.Url == url);
                        if (filter != null)
                        {
                            var filterFound = FilterConfigs.GetFilterSuccess(post, filter.Filters.ToArray());

                            if (filterFound)
                            {
                                newPosts.Add(post);
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "A new post was omitted because it does not comply to the set filter: {Url}", url);
                            }
                        }
                        else if (_hasAllFilter)
                        {
                            var allFilter = _config.PostFilters.FirstOrDefault(wf => wf.Url == "all");
                            if (allFilter != null)
                            {
                                var filterFound = FilterConfigs.GetFilterSuccess(post, allFilter.Filters.ToArray());

                                if (filterFound)
                                {
                                    newPosts.Add(post);
                                }
                                else
                                {
                                    _logger.LogInformation(
                                        "A new post was omitted because it does not comply to the set filter: {Url}", url);
                                }
                            }
                        }
                    }
                    else
                    {
                        newPosts.Add(post);
                    }
                }
            }
            else
            {
                _logAggregator.AddLatestUrlPost(url, parsed.OrderByDescending(p => p.PublishDate).FirstOrDefault());
            }

        }

        /// <summary>
        /// The floor exists only to stop a feed dumping its backlog on first
        /// sight, and to bound how far back we look once the identity set has
        /// started forgetting things. While that set still remembers everything
        /// it has been told, it is the complete answer and the floor has nothing
        /// to add -- so leave it alone.
        ///
        /// Moving the floor up eagerly would re-create the original bug in
        /// miniature: one transiently truncated response would drag it up to the
        /// newest item and strand everything published behind that.
        ///
        /// Once the set is full, entries do start being evicted, so the floor
        /// takes over as the backstop and advances to the oldest item the feed
        /// still carries. Anything older than that has fallen out of the
        /// document and can no longer be offered to us anyway.
        /// </summary>
        private static void AdvanceFloor(FeedState feedState, List<Post> parsed)
        {
            if (!feedState.SeenPosts.IsFull)
                return;

            // Items whose date failed to parse sit at default(DateTime) and
            // would peg the floor to DateTime.MinValue forever.
            var dated = parsed
                .Where(p => p.PublishDate != default)
                .Select(p => p.PublishDate)
                .ToList();

            if (dated.Count == 0)
                return;

            var oldestStillCarried = dated.Min();

            if (oldestStillCarried > feedState.LastPublishDate)
                feedState.LastPublishDate = oldestStillCarried;
        }

        /// <summary>
        /// The feed's own identity for an item -- &lt;guid&gt; in RSS, &lt;id&gt;
        /// in Atom -- falling back to the link, then to title plus date for a
        /// feed that supplies neither. Flattened to one line because the
        /// persisted CSV is read line by line.
        /// </summary>
        private static string GetPostIdentity(Post post)
        {
            var raw = !string.IsNullOrWhiteSpace(post.Id)
                ? post.Id
                : !string.IsNullOrWhiteSpace(post.Link)
                    ? post.Link
                    : $"{post.Title}@{post.PublishDate:O}";

            return raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
        private async Task<List<Post?>> FetchYoutubeAsync(string url)
        {
            try
            {
                Post? post;

                //TODO --> BETTER HANDLING - TEMP FIX FOR INSERTING XML LINKS IN TO YOUTUBE - WE SKIP PARSING HTML
                if (url.Contains("xml"))
                {

                    post = await _rssParsingService.ParseYoutubeFeedAsync(url);

                    return post == null ? new List<Post?>() : new List<Post?> { post };
                }

                var response = await _httpClient.GetAsyncWithFallback(url);

                if (response is null)
                {
                    throw new Exception();
                }

                response!.EnsureSuccessStatusCode();

                var xmlContent = await GetResponseContentAsync(response);

                post = await _rssParsingService.ParseYoutubeFeedAsync(xmlContent);

                return post == null ? new List<Post?>() : new List<Post?> { post };

            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Failed to fetch or process the RSS feed from {Url}: Response Ended Prematurely - Skipping Url - Exception Message: {Ex}",
                    url, ex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "An unexpected error occurred while checking the RSS feed from {Url} - Exception Message: {Ex}",
                    url, ex);
            }

            return new List<Post?>();
        }

        private async Task<List<Post?>> FetchRssAsync(string url, int trim)
        {
            try
            {

                var response = await _httpClient.GetAsyncWithFallback(url);

                if (response is null)
                {
                    throw new Exception();
                }

                response.EnsureSuccessStatusCode();

                var xmlContent = await GetResponseContentAsync(response);

                return await _rssParsingService.ParseRssFeedAsync(xmlContent, trim);

            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Failed to fetch or process the RSS feed from {Url}: {Ex}", url, ex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("An unexpected error occurred while checking the RSS feed from {Url}: {Ex}", url,
                    ex);
            }
            finally
            {

            }

            return new List<Post?>();
        }
        private async Task<string> GetResponseContentAsync(HttpResponseMessage response)
        {
            if (response.Content.Headers.ContentEncoding.Contains("gzip"))
            {
                await using var decompressedStream = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
                using var reader = new StreamReader(decompressedStream, Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }
            else
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                return EncodingExtractor.ConvertBytesByComparing(bytes, response.Content.Headers);
            }
        }

        private void HandleFeedError(string url, FeedState feedState, Exception ex)
        {
            feedState.ErrorCount++;
            _logger.LogError(ex, "Failed to fetch feed from {Url}. Error count: {ErrorCount}", url, feedState.ErrorCount);

            if (feedState.ErrorCount < 3 || !_config.EnableAutoRemove) return;

            _logger.LogWarning("Removing Url: {Url} after too many errors", url);
            var successRemove = _feedStates.TryRemove(url, out _);

            if (!successRemove)
            {
                _logger.LogWarning("Failed to remove Url: {Url}", url);
            }
        }


    }
}
