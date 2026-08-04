using FeedCord.Common;
using FeedCord.Core.Interfaces;
using FeedCord.Helpers;
using FeedCord.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeedCord.Infrastructure.Workers
{
    public class FeedWorker : BackgroundService
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogAggregator _logAggregator;
        private readonly ILogger<FeedWorker> _logger;
        private readonly IFeedManager _feedManager;
        private readonly INotifier _notifier;

        private readonly bool _persistent;
        private readonly string _id;
        private readonly int _delayTime;
        private bool _isInitialized;
        

        public FeedWorker(
            IHostApplicationLifetime lifetime,
            ILogger<FeedWorker> logger,
            IFeedManager feedManager,
            INotifier notifier,
            Config config,
            ILogAggregator logAggregator)
        {
            _lifetime = lifetime;
            _logger = logger;
            _feedManager = feedManager;
            _notifier = notifier;
            _delayTime = config.RssCheckIntervalMinutes;
            _id = config.Id;
            _isInitialized = false;
            _persistent = config.PersistenceOnShutdown;
            _logAggregator = logAggregator;

            logger.LogInformation("{id} Created with check interval {Interval} minutes",
                _id, config.RssCheckIntervalMinutes);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            _lifetime.ApplicationStopping.Register(OnShutdown);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logAggregator.SetStartTime(DateTime.Now);

                try
                {
                    await RunRoutineBackgroundProcessAsync();
                }
                catch (Exception e)
                {
                    _logger.LogCritical("Critical Error in Background Process: {E}", e);
                    throw;
                }

                // Persist after every successful cycle, not only on a graceful
                // shutdown. If the process hangs or is killed ungracefully,
                // OnShutdown never runs and the watermark would otherwise be
                // stuck at whatever the last clean-shutdown save captured -
                // potentially hours or days stale. Saving here means an
                // ungraceful death only ever loses at most one check interval's
                // worth of progress.
                PersistState();

                _logAggregator.SetEndTime(DateTime.Now);

                await _logAggregator.SendToBatchAsync();

                await Task.Delay(TimeSpan.FromMinutes(_delayTime), stoppingToken);
            }
        }

        private async Task RunRoutineBackgroundProcessAsync()
        {
            if (!_isInitialized)
            {
                _logger.LogInformation("{id}: Initializing Url Checks..", _id);
                await _feedManager.InitializeUrlsAsync();
                _isInitialized = true;
            }

            var posts = await _feedManager.CheckForNewPostsAsync();

            if (posts.Count > 0)
            {
                _logger.LogInformation("{id}: Found {PostCount} new posts..", _id, posts.Count);
                await _notifier.SendNotificationsAsync(posts);
            }
        }

        private void OnShutdown()
        {
            PersistState();
        }

        private void PersistState()
        {
            if (!_persistent) return;

            var data = _feedManager.GetAllFeedData();
            SaveDataToCsv(data);
        }

        // Guards against two FeedWorker instances (e.g. two configured Instances)
        // reading/writing feed_dump.csv at the same moment during shutdown.
        private static readonly SemaphoreSlim CsvWriteLock = new(1, 1);

        private void SaveDataToCsv(IReadOnlyDictionary<string, FeedState> data)
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "feed_dump.csv");

            CsvWriteLock.Wait();
            try
            {
                // Read whatever's already there (including rows written by other
                // Instances) so we only ever update our own URLs' rows instead of
                // blindly overwriting the whole file.
                var existing = CsvReader.LoadReferencePosts(filePath);

                foreach (var (key, value) in data)
                {
                    existing[key] = new ReferencePost
                    {
                        IsYoutube = value.IsYoutube,
                        LastRunDate = DateTime.Now
                    };
                }

                using var writer = new StreamWriter(filePath, append: false);
                foreach (var (key, value) in existing)
                {
                    writer.WriteLine($"{key},{value.IsYoutube},{value.LastRunDate:yyyy-MM-ddTHH:mm:ss}");
                }
            }
            finally
            {
                CsvWriteLock.Release();
            }
        }
    }
}
