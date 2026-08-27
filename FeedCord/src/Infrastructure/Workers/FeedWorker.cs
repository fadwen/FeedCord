using FeedCord.Common;
using FeedCord.Helpers;
using FeedCord.Core.Interfaces;
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
            if (!_persistent) return;
            
            var data = _feedManager.GetAllFeedData();
            SaveDataToCsv(data);
        }

        private void SaveDataToCsv(IReadOnlyDictionary<string, FeedState> data)
        {
            var filePath = CsvReader.DefaultFilePath;
            using var writer = new StreamWriter(filePath, append: true);

            foreach (var (key, value) in data)
            {
                var fields = new List<string>
                {
                    key,
                    value.IsYoutube.ToString(),
                    // The feed's own watermark, not the wall clock. Saving
                    // DateTime.Now here meant a restart baselined every feed to
                    // the moment of the last save, so anything a publisher
                    // back-dated behind that instant was invisible from then on.
                    //
                    // ISO 8601 also round-trips: CsvReader parses with
                    // InvariantCulture, which DateTime.Now's default formatting
                    // matches only while the process culture happens to be
                    // invariant. A row that fails to parse is skipped whole,
                    // taking its identities with it.
                    value.LastPublishDate.ToString("yyyy-MM-ddTHH:mm:ss")
                };

                fields.AddRange(value.SeenPosts.Snapshot());

                writer.WriteLine(Csv.FormatLine(fields));
            }
        }
    }
}
