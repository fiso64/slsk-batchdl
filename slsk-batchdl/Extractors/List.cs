using System.Text;

using Models;
using Enums;

namespace Extractors
{
    public class ListExtractor : IExtractor
    {
        string? listFilePath = null;
        readonly object fileLock = new object();

        public static bool InputMatches(string input)
        {
            return !input.IsInternetUrl();
        }

        public async Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            listFilePath = Utils.ExpandVariables(input);

            if (!File.Exists(listFilePath))
                throw new FileNotFoundException($"List file '{listFilePath}' not found");

            var lines = File.ReadAllLines(listFilePath);

            var trackLists = new TrackLists();

            int step = reverse ? -1 : 1;
            int start = reverse ? lines.Length - 1 : 0;
            int count = 0;
            int added = 0;

            string foldername = Path.GetFileNameWithoutExtension(listFilePath);

            for (int i = start; i < lines.Length && i >= 0; i += step)
            {
                var line = lines[i].Trim();

                if (line.Length == 0 || line.StartsWith('#')) continue;

                if (count++ < offset)
                    continue;

                if (added >= maxTracks)
                    break;

                bool isAlbum = false;

                if (line.StartsWith("a:"))
                {
                    line = line[2..];
                    isAlbum = true;
                }

                var fields = ParseLine(line);

                if (isAlbum)
                {
                    fields[0] = "album://" + fields[0];
                }

                var (type, ex) = ExtractorRegistry.GetMatchingExtractor(fields[0]);

                var tl = await ex.GetTracks(fields[0], int.MaxValue, 0, false, config);

                foreach (var tle in tl.lists)
                {
                    if (fields.Count >= 2)
                    {
                        tle.extractorCond = Config.ParseConditions(fields[1], tle.source);
                    }
                    if (fields.Count >= 3)
                    {
                        tle.extractorPrefCond = Config.ParseConditions(fields[2]);
                    }

                    tle.itemName = foldername;
                    tle.enablesIndexByDefault = true;

                    tle.source.LineNumber = i + 1;
                    tle.source.ItemNumber = offset + added + 1;
                }

                if (tl.Count == 1 && tl[0].source.Type == TrackType.Normal && (type == InputType.String || type == InputType.Soulseek))
                {
                    if (tl[0].list != null && tl[0].list.SelectMany(x => x).Count() == 1)
                    {
                        tl[0].list[0][0].LineNumber = i + 1;
                        tl[0].list[0][0].ItemNumber = offset + added + 1;
                    }
                }

                trackLists.lists.AddRange(tl.lists);

                added++;
            }

            return trackLists;
        }

        static List<string> ParseLine(string input)
        {
            var fields = new List<string>();

            bool inQuotes = false;
            var currentField = new StringBuilder();
            input = input.Replace('\t', ' ');

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ' ' && !inQuotes)
                {
                    if (currentField.Length > 0)
                    {
                        fields.Add(currentField.ToString());
                        currentField.Clear();
                    }

                    while (i < input.Length - 1 && input[i + 1] == ' ')
                    {
                        i++;
                    }
                }
                else
                {
                    currentField.Append(c);
                }
            }

            if (currentField.Length > 0)
            {
                fields.Add(currentField.ToString());
            }

            return fields;
        }

        public async Task RemoveTrackFromSource(Track track)
        {
            lock (fileLock)
            {
                if (File.Exists(listFilePath))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(listFilePath, Encoding.UTF8);
                        int idx = track.LineNumber - 1;

                        if (idx > -1 && idx < lines.Length)
                        {
                            lines[idx] = "";
                            Utils.WriteAllLines(listFilePath, lines, '\n');
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Error removing from source: {e}");
                    }
                }
            }
        }
    }
}
