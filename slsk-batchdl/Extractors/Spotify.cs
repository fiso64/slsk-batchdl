using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Swan;

using Models;
using Jobs;

namespace Extractors
{
    public class SpotifyExtractor : IExtractor
    {
        private Spotify? spotifyClient;
        public string playlistUri = "";

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input == "spotify-likes" || input == "spotify-albums" || input.IsInternetUrl() && input.Contains("spotify.com");
        }

        public async Task<List<QueryJob>> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            var jobs = new List<QueryJob>();
            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;

            bool needLogin = input == "spotify-likes" || input == "spotify-albums" || config.removeTracksFromSource;

            if (needLogin && config.spotifyToken.Length == 0 && (config.spotifyId.Length == 0 || config.spotifySecret.Length == 0))
                throw new Exception("Credentials are required when downloading liked music or removing from source playlists.");

            spotifyClient = new Spotify(config.spotifyId, config.spotifySecret, config.spotifyToken, config.spotifyRefresh);
            await spotifyClient.Authorize(needLogin, config.removeTracksFromSource);

            if (input == "spotify-likes")
            {
                Logger.Info("Loading Spotify likes..");
                var songs = await spotifyClient.GetLikes(max, off);
                var slj   = new SongListQueryJob { ItemName = "Spotify Likes", EnablesIndexByDefault = true };
                foreach (var s in songs) slj.Songs.Add(s);
                jobs.Add(slj);
            }
            else if (input == "spotify-albums")
            {
                Logger.Info("Loading Spotify liked albums..");
                var albumQueue = await spotifyClient.GetAlbums(max, off);
                var albumList  = new AlbumListJob { ItemName = "Spotify Liked Albums", EnablesIndexByDefault = true };
                foreach (var j in albumQueue.Jobs.Cast<AlbumQueryJob>())
                    albumList.Albums.Add(j);
                jobs.Add(albumList);
            }
            else if (input.Contains("/album/"))
            {
                Logger.Info("Loading Spotify album..");
                var job = await spotifyClient.GetAlbumJob(input, config);
                jobs.Add(job);
            }
            else if (input.Contains("/artist/"))
            {
                throw new Exception("Spotify artist download currently not supported.");
            }
            else
            {
                var songs = new List<SongJob>();
                string? playlistName = null;

                try
                {
                    Logger.Info("Loading Spotify playlist");
                    (playlistName, playlistUri, songs) = await spotifyClient.GetPlaylist(input, max, off);
                }
                catch (SpotifyAPI.Web.APIException)
                {
                    if (!needLogin && !spotifyClient.UsedDefaultCredentials)
                    {
                        await spotifyClient.Authorize(true, config.removeTracksFromSource);
                        (playlistName, playlistUri, songs) = await spotifyClient.GetPlaylist(input, max, off);
                    }
                    else if (!needLogin)
                    {
                        throw new Exception("Spotify playlist not found (it may be set to private, but no credentials have been provided).");
                    }
                    else throw;
                }

                var slj = new SongListQueryJob { ItemName = playlistName, EnablesIndexByDefault = true };
                foreach (var s in songs) slj.Songs.Add(s);
                jobs.Add(slj);
            }

            if (reverse)
            {
                JobQueue.ReverseJobList(jobs);
                // Apply offset/limit after reversing
                foreach (var job in jobs)
                {
                    if (job is SongListQueryJob slj)
                    {
                        if (slj.Songs.Count > offset)
                            slj.Songs.RemoveRange(0, offset);
                        else
                            slj.Songs.Clear();

                        if (slj.Songs.Count > maxTracks)
                            slj.Songs.RemoveRange(maxTracks, slj.Songs.Count - maxTracks);
                    }
                    else if (job is AlbumListJob alj)
                    {
                        if (alj.Albums.Count > offset)
                            alj.Albums.RemoveRange(0, offset);
                        else
                            alj.Albums.Clear();

                        if (alj.Albums.Count > maxTracks)
                            alj.Albums.RemoveRange(maxTracks, alj.Albums.Count - maxTracks);
                    }
                }

                // For flat album job lists (e.g. from MusicBrainz): trim the job list itself
                if (jobs.All(j => j is AlbumQueryJob))
                    jobs = jobs.Skip(offset).Take(maxTracks).ToList();
            }

            return jobs;
        }

        public async Task RemoveTrackFromSource(SongJob job)
        {
            try
            {
                if (playlistUri.Length > 0 && job.Query.URI.Length > 0)
                    await spotifyClient.RemoveTrackFromPlaylist(playlistUri, job.Query.URI);
            }
            catch (Exception e)
            {
                Logger.Error($"Error removing from source: {e}");
            }
        }
    }


    public class Spotify
    {
        private EmbedIOAuthServer _server;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private string _clientToken;
        private string _clientRefreshToken;
        private SpotifyClient? _client;
        private bool loggedIn = false;

        public const string encodedSpotifyId = "MWJmNDY5M1bLaH9WJiYjFhNGY0MWJjZWQ5YjJjMWNmZGJiZDI=";
        public const string encodedSpotifySecret = "Y2JlM2QxYTE5MzJkNDQ2MmFiOGUy3shTuf4Y2JhY2M3ZDdjYWU=";
        public bool UsedDefaultCredentials { get; private set; }

        public Spotify(string clientId = "", string clientSecret = "", string token = "", string refreshToken = "")
        {
            _clientId           = clientId;
            _clientSecret       = clientSecret;
            _clientToken        = token;
            _clientRefreshToken = refreshToken;

            if (_clientToken.Length == 0 && (_clientId.Length == 0 || _clientSecret.Length == 0))
            {
                _clientId           = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifyId.Replace("1bLaH9", "")));
                _clientSecret       = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifySecret.Replace("3shTuf4", "")));
                UsedDefaultCredentials = true;
            }
        }

        public async Task Authorize(bool login = false, bool needModify = false)
        {
            _client = null;
            Logger.Debug($"Spotify: Authorizing (login={login}, modify={needModify})");

            if (!login)
            {
                var config   = SpotifyClientConfig.CreateDefault();
                var request  = new ClientCredentialsRequest(_clientId, _clientSecret);
                var response = await new OAuthClient(config).RequestToken(request);
                _client = new SpotifyClient(config.WithToken(response.AccessToken));
            }
            else
            {
                Swan.Logging.Logger.NoLogging();
                _server = new EmbedIOAuthServer(new Uri("http://127.0.0.1:48721/callback"), 48721);
                await _server.Start();
                Logger.Debug($"Spotify: AuthServer started");

                var existingOk = false;
                if (_clientToken.Length != 0 || _clientRefreshToken.Length != 0)
                {
                    existingOk = await this.TryExistingToken();
                    loggedIn   = true;
                }

                if (!existingOk)
                {
                    _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
                    _server.ErrorReceived             += OnErrorReceived;

                    var scope = new List<string>
                    {
                        Scopes.UserLibraryRead, Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative
                    };

                    if (needModify)
                    {
                        scope.Add(Scopes.PlaylistModifyPublic);
                        scope.Add(Scopes.PlaylistModifyPrivate);
                    }

                    var request = new LoginRequest(_server.BaseUri, _clientId, LoginRequest.ResponseType.Code) { Scope = scope };
                    try { BrowserUtil.Open(request.ToUri()); }
                    catch (Exception) { Logger.Info($"Unable to open URL, manually open: {request.ToUri()}"); }
                }

                await IsClientReady();
            }
        }

        private async Task<bool> TryExistingToken()
        {
            if (_clientToken.Length != 0)
            {
                Logger.Debug("Testing Spotify access with existing token...");
                var client = new SpotifyClient(_clientToken);
                try
                {
                    var me = await client.UserProfile.Current();
                    Logger.Debug("Spotify access is good!");
                    _client = client;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Info($"Could not make an API call with existing token: {ex.Message}");
                }
            }

            if (_clientRefreshToken.Length != 0)
            {
                Logger.Info("Trying to renew access with refresh token...");
                var refreshRequest = new AuthorizationCodeRefreshRequest(_clientId, _clientSecret, _clientRefreshToken);
                try
                {
                    var oauthClient    = new OAuthClient();
                    var refreshResponse = await oauthClient.RequestToken(refreshRequest);
                    Logger.Debug($"We got a new refreshed access token from server: {refreshResponse.AccessToken}");
                    _clientToken = refreshResponse.AccessToken;
                    _client      = new SpotifyClient(_clientToken);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Info($"Could not refresh access token with refresh token: {ex}");
                }
            }
            else
            {
                Logger.Info("No refresh token present, cannot refresh existing access");
            }

            Logger.Info("Not possible to access Spotify API without login! Falling back to login flow...");
            return false;
        }

        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            Logger.Debug($"Spotify: Authorization code received");
            await _server.Stop();

            var config        = SpotifyClientConfig.CreateDefault();
            Logger.Debug($"Spotify: Getting token response..");
            var tokenResponse = await new OAuthClient(config).RequestToken(
                new AuthorizationCodeTokenRequest(_clientId, _clientSecret, response.Code, new Uri("http://127.0.0.1:48721/callback")));

            Logger.Debug($"Spotify: Got token");
            Console.WriteLine("spotify-token=" + tokenResponse.AccessToken);
            _clientToken = tokenResponse.AccessToken;
            Console.WriteLine();
            Console.WriteLine("spotify-refresh=" + tokenResponse.RefreshToken);
            Console.WriteLine();
            _clientRefreshToken = tokenResponse.RefreshToken;
            _client             = new SpotifyClient(tokenResponse.AccessToken);
            loggedIn            = true;
        }

        private async Task OnErrorReceived(object sender, string error, string state)
        {
            Logger.DebugError($"Spotify: Auth error: {error}");
            await _server.Stop();
            throw new Exception($"Aborting authorization, error received: {error}");
        }

        public async Task<bool> IsClientReady()
        {
            while (_client == null)
                await Task.Delay(1000);
            return true;
        }

        public async Task<List<SongJob>> GetLikes(int max = int.MaxValue, int offset = 0)
        {
            if (!loggedIn)
                throw new Exception("Can't get liked music as user is not logged in");

            var songs = new List<SongJob>();
            int limit = Math.Min(max, 50);
            int num   = offset + 1;

            while (true)
            {
                var tracks = await _client.Library.GetTracks(new LibraryTracksRequest { Limit = limit, Offset = offset });

                foreach (var track in tracks.Items)
                {
                    string[] artists = ((IEnumerable<object>)track.Track.ReadProperty("artists")).Select(a => (string)a.ReadProperty("name")).ToArray();
                    string artist   = artists[0];
                    string name     = (string)track.Track.ReadProperty("name");
                    string album    = (string)track.Track.ReadProperty("album").ReadProperty("name");
                    int duration    = (int)track.Track.ReadProperty("durationMs");

                    var query = new SongQuery { Album = album, Artist = artist, Title = name, Length = duration / 1000 };
                    songs.Add(new SongJob(query) { ItemNumber = num++ });
                }

                if (tracks.Items.Count < limit || songs.Count >= max) break;
                offset += limit;
                limit   = Math.Min(max - songs.Count, 50);
            }

            return songs;
        }

        public async Task<JobQueue> GetAlbums(int max = int.MaxValue, int offset = 0)
        {
            if (!loggedIn)
                throw new Exception("Can't get liked albums as user is not logged in");

            var queue = new JobQueue();
            int limit = Math.Min(max, 50);
            int num   = offset + 1;

            while (true)
            {
                var albums = await _client.Library.GetAlbums(new LibraryAlbumsRequest { Limit = limit, Offset = offset });

                foreach (var savedAlbum in albums.Items)
                {
                    var album  = savedAlbum.Album;
                    string[] artists = album.Artists.Select(a => a.Name).ToArray();
                    string artist = artists[0];

                    var query = new AlbumQuery
                    {
                        Album         = album.Name,
                        Artist        = artist,
                        MinTrackCount = album.TotalTracks,
                    };

                    var job = new AlbumQueryJob(query)
                    {
                        ItemNumber            = num++,
                        ItemName              = "Spotify Albums",
                        EnablesIndexByDefault = true,
                    };
                    queue.Enqueue(job);
                }

                if (albums.Items.Count < limit || queue.Count >= max) break;
                offset += limit;
                limit   = Math.Min(max - queue.Count, 50);
            }

            Logger.Info($"Found {queue.Count} liked albums on Spotify");
            return queue;
        }

        public async Task RemoveTrackFromPlaylist(string playlistId, string trackUri)
        {
            var item = new PlaylistRemoveItemsRequest.Item { Uri = trackUri };
            var pr   = new PlaylistRemoveItemsRequest();
            pr.Tracks = new List<PlaylistRemoveItemsRequest.Item>() { item };
            try { await _client.Playlists.RemoveItems(playlistId, pr); }
            catch { }
        }

        public async Task<(string?, string?, List<SongJob>)> GetPlaylist(string url, int max = int.MaxValue, int offset = 0)
        {
            var playlistId = GetPlaylistIdFromUrl(url);
            var p          = await _client.Playlists.Get(playlistId);

            var songs = new List<SongJob>();
            int limit = Math.Min(max, 100);
            int num   = offset + 1;

            while (true)
            {
                var tracks = await _client.Playlists.GetItems(playlistId, new PlaylistGetItemsRequest { Limit = limit, Offset = offset });

                foreach (var track in tracks.Items)
                {
                    try
                    {
                        string[] artists = ((IEnumerable<object>)track.Track.ReadProperty("artists")).Select(a => (string)a.ReadProperty("name")).ToArray();
                        var query = new SongQuery
                        {
                            Artist = artists[0],
                            Album  = (string)track.Track.ReadProperty("album").ReadProperty("name"),
                            Title  = (string)track.Track.ReadProperty("name"),
                            Length = (int)track.Track.ReadProperty("durationMs") / 1000,
                            URI    = (string)track.Track.ReadProperty("uri"),
                        };
                        songs.Add(new SongJob(query) { ItemNumber = num++ });
                    }
                    catch { continue; }
                }

                if (tracks.Items.Count < limit || songs.Count >= max) break;
                offset += limit;
                limit   = Math.Min(max - songs.Count, 100);
            }

            return (p.Name, p.Id, songs);
        }

        private string GetPlaylistIdFromUrl(string url)
        {
            var uri      = new Uri(url);
            var segments = uri.Segments;
            return segments[segments.Length - 1].TrimEnd('/');
        }

        public async Task<AlbumQueryJob> GetAlbumJob(string url, Config config)
        {
            var albumId = GetAlbumIdFromUrl(url);
            var album   = await _client.Albums.Get(albumId);

            var songs = new List<SongJob>();
            foreach (var track in album.Tracks.Items)
            {
                var query = new SongQuery
                {
                    Album  = album.Name,
                    Artist = track.Artists.First().Name,
                    Title  = track.Name,
                    Length = track.DurationMs / 1000,
                    URI    = track.Uri,
                };
                songs.Add(new SongJob(query));
            }

            var albumQuery = new AlbumQuery
            {
                Album  = album.Name,
                Artist = album.Artists.First().Name,
            };

            if (config.setAlbumMinTrackCount) albumQuery.MinTrackCount = songs.Count;
            if (config.setAlbumMaxTrackCount) albumQuery.MaxTrackCount = songs.Count;

            return new AlbumQueryJob(albumQuery);
        }

        private string GetAlbumIdFromUrl(string url)
        {
            var uri      = new Uri(url);
            var segments = uri.Segments;
            return segments[^1].TrimEnd('/');
        }
    }
}
