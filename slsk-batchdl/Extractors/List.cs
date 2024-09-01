using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Data;
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

        public async Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse)
        {
            if (!File.Exists(input))
                throw new FileNotFoundException("List file not found");

            listFilePath = input;

            var lines = File.ReadAllLines(input);

            var trackLists = new TrackLists();

            int step = reverse ? -1 : 1;
            int start = reverse ? lines.Length - 1 : 0;
            int count = 0;
            int added = 0;

            string foldername = Path.GetFileNameWithoutExtension(input);

            for (int i = start; i < lines.Length && i >= 0; i += step)
            {
                var line = lines[i].Trim();
                
                if (line.Length == 0 || line.StartsWith('#')) continue;

                if (count++ < offset)
                    continue;

                if (added >= maxTracks)
                    break;

                var fields = ParseLine(line);

                var (_, ex) = ExtractorRegistry.GetMatchingExtractor(fields[0]);

                var tl = await ex.GetTracks(fields[0], int.MaxValue, 0, false);

                foreach (var tle in tl.lists)
                {
                    if (fields.Count >= 2)
                        tle.additionalConds = Config.ParseConditions(fields[1]);
                    if (fields.Count >= 3)
                        tle.additionalPrefConds = Config.ParseConditions(fields[2]);

                    tle.defaultFolderName = foldername;
                }

                if (tl.lists.Count == 1)
                    tl[0].source.CsvRow = i;

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

                        if (track.CsvRow > -1 && track.CsvRow < lines.Length)
                        {
                            lines[track.CsvRow] = "";
                            Utils.WriteAllLines(listFilePath, lines, '\n');
                        }
                    }
                    catch (Exception e)
                    {
                        Printing.WriteLine($"Error removing from source: {e}", debugOnly: true);
                    }
                }
            }
        }
    }
}
