

namespace FeedCord.Common
{
    public class FeedState
    {
        public bool IsYoutube { get; init; }

        /// <summary>
        /// Floor for how far back this feed is willing to look. It exists only
        /// to stop a newly added feed dumping its whole backlog on first run;
        /// which individual posts have already been sent is tracked by
        /// <see cref="SeenPosts"/>.
        /// </summary>
        public DateTime LastPublishDate { get; set; }

        public int ErrorCount { get; set; }

        public SeenPostSet SeenPosts { get; init; } = new();
    }
}
