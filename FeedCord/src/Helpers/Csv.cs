using System.Text;

namespace FeedCord.Helpers
{
    /// <summary>
    /// Minimal RFC 4180 field handling for feed_dump.csv.
    ///
    /// The file gained a variable-length tail of post identities (guids, or
    /// links where a feed omits the guid). Those are arbitrary publisher-supplied
    /// strings that can legitimately contain commas and quotes, which a naive
    /// Split(',') mangles. Fields are therefore quoted on write and parsed
    /// properly on read.
    ///
    /// The reader is line-oriented, so no field may contain a line break.
    /// Identities are normalised to a single line before they get here.
    /// </summary>
    public static class Csv
    {
        private static readonly char[] NeedsQuoting = { ',', '"', '\r', '\n' };

        public static string FormatLine(IEnumerable<string> fields)
        {
            return string.Join(",", fields.Select(Escape));
        }

        private static string Escape(string? field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            if (field.IndexOfAny(NeedsQuoting) < 0)
                return field;

            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        public static List<string> ParseLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inQuotes)
                {
                    if (c != '"')
                    {
                        current.Append(c);
                    }
                    else if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // Doubled quote inside a quoted field is a literal quote.
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else if (c == '"' && current.Length == 0)
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString());

            return fields;
        }
    }
}
