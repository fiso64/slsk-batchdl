using Enums;
using Models;


namespace Extractors
{
    public interface IExtractor
    {
        Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config);
        Task RemoveTrackFromSource(Track track) => Task.CompletedTask;
    }

    public static class ExtractorRegistry
    {
        static readonly List<(InputType, Func<string, bool>, Func<IExtractor>)> extractors = new()
        {
            (InputType.CSV,         CsvExtractor.InputMatches,      () => new CsvExtractor()),
            (InputType.YouTube,     YouTubeExtractor.InputMatches,  () => new YouTubeExtractor()),
            (InputType.Spotify,     SpotifyExtractor.InputMatches,  () => new SpotifyExtractor()),
            (InputType.Bandcamp,    BandcampExtractor.InputMatches, () => new BandcampExtractor()),
            (InputType.MusicBrainz, MusicBrainzExtractor.InputMatches,() => new MusicBrainzExtractor()),
            (InputType.Soulseek,    SoulseekExtractor.InputMatches, () => new SoulseekExtractor()),
            (InputType.String,      StringExtractor.InputMatches,   () => new StringExtractor()),
            (InputType.List,        ListExtractor.InputMatches,     () => new ListExtractor()),
        };

        public static (InputType, IExtractor) GetMatchingExtractor(string input, InputType inputType = InputType.None)
        {
            if (string.IsNullOrEmpty(input))
                throw new ArgumentException("Input string can not be null or empty.");

            if (inputType != InputType.None)
            {
                var (t, _, e) = extractors.First(x => x.Item1 == inputType);
                return (t, e());
            }

            foreach ((var type, var inputMatches, var extractor) in extractors)
            {
                if (inputMatches(input))
                {
                    return (type, extractor());
                }
            }

            throw new ArgumentException($"No matching extractor for input '{input}'");
        }
    }
}
