namespace FeedCord.Common
{
    public record Post(
        string Title,
        string ImageUrl,
        string Description,
        string Link,
        string Tag,
        DateTime PublishDate,
        string Author,
        string[] Labels = null,
        // The feed's own identity for this item: <guid> in RSS, <id> in Atom.
        // Used to recognise a post we have already sent, so detection does not
        // rest on the publish date alone. Empty when the feed omits it, in which
        // case FeedManager falls back to the link.
        string Id = ""
        );
}
