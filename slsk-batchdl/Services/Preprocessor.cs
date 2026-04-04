using Jobs;
using Models;
using System.Text.RegularExpressions;

namespace Services
{
    public static class Preprocessor
    {
        /// <summary>
        /// Applies all configured preprocessing transformations to song.Query.
        /// Sets song.Query = new SongQuery(song.Query) { ... } (copy-and-replace — no mutation of original).
        /// No-op when song.Query.IsDirectLink is true.
        /// </summary>
        public static void PreprocessSong(SongJob song, Config config)
        {
            if (song.Query.IsDirectLink) return;

            var q = song.Query;
            string artist = q.Artist;
            string title  = q.Title;
            string album  = q.Album;
            bool   artistMaybeWrong = q.ArtistMaybeWrong;

            if (config.removeFt)
            {
                title  = title.RemoveFt();
                artist = artist.RemoveFt();
            }

            if (config.removeBrackets)
            {
                title = title.RemoveSquareBrackets();
            }

            if (config.regex != null)
            {
                foreach (var (toReplace, replaceBy) in config.regex)
                {
                    title  = Regex.Replace(title,  toReplace.Title,  replaceBy.Title,  RegexOptions.IgnoreCase);
                    artist = Regex.Replace(artist, toReplace.Artist, replaceBy.Artist, RegexOptions.IgnoreCase);
                    album  = Regex.Replace(album,  toReplace.Album,  replaceBy.Album,  RegexOptions.IgnoreCase);
                }
            }

            if (config.parseTitleTemplate.Length > 0 && title.Length > 0)
            {
                var updated = new SongQuery(q) { Artist = artist, Title = title, Album = album };
                TrackTemplateParser.TryUpdateSongQuery(title, config.parseTitleTemplate, ref updated);
                artist = updated.Artist;
                title  = updated.Title;
                album  = updated.Album;
            }

            if (config.extractArtist && title.Length > 0)
            {
                (var parsedArtist, var parsedTitle) = Utils.SplitArtistAndTitle(title);
                if (parsedArtist != null)
                {
                    artist = parsedArtist;
                    title  = parsedTitle;
                }
            }

            if (config.artistMaybeWrong)
                artistMaybeWrong = true;

            song.Query = new SongQuery(q)
            {
                Artist          = artist.Trim(),
                Title           = title.Trim(),
                Album           = album.Trim(),
                ArtistMaybeWrong = artistMaybeWrong,
            };
        }

        /// <summary>
        /// Applies all configured preprocessing transformations to job.Query.
        /// Sets job.Query = new AlbumQuery(job.Query) { ... }.
        /// No-op when job.Query.IsDirectLink is true.
        /// Also applies minAlbumTrackCount / maxAlbumTrackCount from config.
        /// </summary>
        public static void PreprocessAlbum(AlbumJob job, Config config)
        {
            if (job.Query.IsDirectLink) return;

            var q = job.Query;
            string artist = q.Artist;
            string album  = q.Album;
            bool   artistMaybeWrong = q.ArtistMaybeWrong;
            int    minTrackCount    = q.MinTrackCount;
            int    maxTrackCount    = q.MaxTrackCount;

            if (config.removeFt)
                artist = artist.RemoveFt();

            if (config.regex != null)
            {
                foreach (var (toReplace, replaceBy) in config.regex)
                {
                    artist = Regex.Replace(artist, toReplace.Artist, replaceBy.Artist, RegexOptions.IgnoreCase);
                    album  = Regex.Replace(album,  toReplace.Album,  replaceBy.Album,  RegexOptions.IgnoreCase);
                }
            }

            if (config.artistMaybeWrong)
                artistMaybeWrong = true;

            if (config.minAlbumTrackCount > 0)
                minTrackCount = config.minAlbumTrackCount;

            if (config.maxAlbumTrackCount != -1)
                maxTrackCount = config.maxAlbumTrackCount;

            job.Query = new AlbumQuery(q)
            {
                Artist          = artist.Trim(),
                Album           = album.Trim(),
                ArtistMaybeWrong = artistMaybeWrong,
                MinTrackCount   = minTrackCount,
                MaxTrackCount   = maxTrackCount,
            };
        }

        /// <summary>
        /// Preprocesses all songs/albums in a job according to its Config.
        /// Called per-job during the main loop, just before download begins.
        /// </summary>
        public static void PreprocessJob(DownloadJob job)
        {
            if (!job.PreprocessTracks) return;

            var config = job.Config;

            switch (job)
            {
                case SongListJob slj:
                    foreach (var song in slj.Songs)
                        PreprocessSong(song, config);
                    break;

                case AlbumJob aj:
                    PreprocessAlbum(aj, config);
                    break;

                case AggregateJob ag:
                    foreach (var song in ag.Songs)
                        PreprocessSong(song, config);
                    break;

                case AggregateAlbumJob aaj:
                    // AggregateAlbumJob only has an AlbumQuery, preprocess artist/album.
                    // Synthesise a temporary AlbumJob to reuse PreprocessAlbum.
                    var tempAlbum = new AlbumJob(aaj.Query) { Config = config };
                    PreprocessAlbum(tempAlbum, config);
                    aaj.Query = tempAlbum.Query;
                    break;
            }
        }
    }
}
