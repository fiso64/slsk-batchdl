using Enums;
using Data;


namespace Extractors
{
    public interface IExtractor
    {
        Task<TrackLists> GetTracks(int maxTracks, int offset, bool reverse);
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
            (InputType.String,      StringExtractor.InputMatches,   () => new StringExtractor()),
        };

        public static (InputType, IExtractor?) GetMatchingExtractor(string input)
        {
            foreach ((var inputType, var inputMatches, var extractor) in extractors)
            {
                if (inputMatches(input))
                {
                    return (inputType, extractor());
                }
            }
            return (InputType.None, null);
        }
    }
}
