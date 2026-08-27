using FeedCord.Common;
using System.Globalization;

namespace FeedCord.Helpers
{
    public static class CsvReader
    {
        /// <summary>
        /// Canonical location of the persisted state file. FeedManager used to
        /// open this by relative path while FeedWorker wrote it under
        /// AppContext.BaseDirectory; those coincide under Docker (WORKDIR /app)
        /// but not when the process is started from another directory.
        /// </summary>
        public static string DefaultFilePath =>
            Path.Combine(AppContext.BaseDirectory, "feed_dump.csv");

        /// <summary>
        /// Row layout is
        ///
        ///     url,isYoutube,lastPublishDate[,seenId...]
        ///
        /// with a variable-length tail of already-sent post identities. Files
        /// written before that tail existed have exactly three columns and load
        /// with an empty identity set, which is harmless: the stored date still
        /// gates the first cycle, so an upgrade does not re-post a backlog.
        /// </summary>
        public static Dictionary<string, ReferencePost> LoadReferencePosts(string filePath)
        {
            var dictionary = new Dictionary<string, ReferencePost>();

            if (!File.Exists(filePath))
            {
                return dictionary;
            }

            try
            {
                using var reader = new StreamReader(filePath);

                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = Csv.ParseLine(line);

                    if (parts.Count < 3)
                    {
                        continue;
                    }

                    var url = parts[0].Trim();
                    if (!bool.TryParse(parts[1], out var isYoutube))
                    {
                        continue;
                    }

                    if (!DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastRunDate))
                    {
                        continue;
                    }

                    var seenIds = parts
                        .Skip(3)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToArray();

                    dictionary[url] = new ReferencePost
                    {
                        IsYoutube = isYoutube,
                        LastRunDate = lastRunDate,
                        SeenIds = seenIds
                    };
                }
            }
            catch
            {
                return dictionary;
            }

            return dictionary;
        }
    }
}
