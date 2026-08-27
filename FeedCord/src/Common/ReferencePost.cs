

namespace FeedCord.Common
{
    public class ReferencePost
    {
        public bool IsYoutube { get; set; }
        public DateTime LastRunDate { get; init; }
        public IReadOnlyList<string> SeenIds { get; init; } = Array.Empty<string>();
    }
}
