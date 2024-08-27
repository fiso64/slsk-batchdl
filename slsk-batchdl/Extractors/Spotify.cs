using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Swan;

using Data;
using Enums;
using System.Security;

namespace Extractors
{
    public class SpotifyExtractor : IExtractor
    {
        private Spotify? spotifyClient;
        public string playlistUri = "";

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input == "spotify-likes" || input.IsInternetUrl() && input.Contains("spotify.com");
        }

        public async Task<TrackLists> GetTracks(int maxTracks, int offset, bool reverse)
        {
            var trackLists = new TrackLists();
            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;

            string playlistName = "";
            bool needLogin = Config.input == "spotify-likes" || Config.removeTracksFromSource;
            var tle = new TrackListEntry();

            static void readSpotifyCreds()
            {
                Console.Write("Spotify client ID:");
                Config.spotifyId = Console.ReadLine();
                Console.Write("Spotify client secret:");
                Config.spotifySecret = Console.ReadLine();
                Console.Write("Spotify token:");
                Config.spotifyToken = Console.ReadLine();
                Console.Write("Spotify refresh token:");
                Config.spotifyRefresh = Console.ReadLine();
                Console.WriteLine();
            }

            if (needLogin && Config.spotifyToken.Length == 0 && (Config.spotifyId.Length == 0 || Config.spotifySecret.Length == 0))
            {
                readSpotifyCreds();
            }

            spotifyClient = new Spotify(Config.spotifyId, Config.spotifySecret, Config.spotifyToken, Config.spotifyRefresh);
            await spotifyClient.Authorize(needLogin, Config.removeTracksFromSource);

            if (Config.input == "spotify-likes")
            {
                Console.WriteLine("Loading Spotify likes");
                var tracks = await spotifyClient.GetLikes(max, off);
                playlistName = "Spotify Likes";
                tle.list.Add(tracks);
            }
            else if (Config.input.Contains("/album/"))
            {
                Console.WriteLine("Loading Spotify album");
                (var source, var tracks) = await spotifyClient.GetAlbum(Config.input);
                playlistName = source.ToString(noInfo: true);
                tle.source = source;

                if (Config.setAlbumMinTrackCount)
                    source.MinAlbumTrackCount = tracks.Count;

                if (Config.setAlbumMaxTrackCount)
                    source.MaxAlbumTrackCount = tracks.Count;
            }
            else if (Config.input.Contains("/artist/"))
            {
                Console.WriteLine("Loading spotify artist");
                throw new NotImplementedException("Spotify artist download currently not supported.");
            }
            else
            {
                var tracks = new List<Track>();

                try
                {
                    Console.WriteLine("Loading Spotify playlist");
                    (playlistName, playlistUri, tracks) = await spotifyClient.GetPlaylist(Config.input, max, off);
                }
                catch (SpotifyAPI.Web.APIException)
                {
                    if (!needLogin && !spotifyClient.UsedDefaultCredentials)
                    {
                        await spotifyClient.Authorize(true, Config.removeTracksFromSource);
                        (playlistName, playlistUri, tracks) = await spotifyClient.GetPlaylist(Config.input, max, off);
                    }
                    else if (!needLogin)
                    {
                        Console.WriteLine("Spotify playlist not found. It may be set to private. Login? [Y/n]");
                        if (Console.ReadLine()?.ToLower().Trim() == "y")
                        {
                            readSpotifyCreds();
                            spotifyClient = new Spotify(Config.spotifyId, Config.spotifySecret);
                            await spotifyClient.Authorize(true, Config.removeTracksFromSource);
                            Console.WriteLine("Loading Spotify playlist");
                            (playlistName, playlistUri, tracks) = await spotifyClient.GetPlaylist(Config.input, max, off);
                        }
                        else
                        {
                            Environment.Exit(0);
                        }
                    }
                    else throw;
                }

                tle.list.Add(tracks);
            }

            Config.defaultFolderName = playlistName.ReplaceInvalidChars(Config.invalidReplaceStr);

            if (reverse)
            {
                trackLists.Reverse();
                trackLists = TrackLists.FromFlattened(trackLists.Flattened(true, false).Skip(offset).Take(maxTracks));
            }

            return trackLists;
        }

        public async Task RemoveTrackFromSource(Track track)
        {
            if (playlistUri.Length > 0 && track.URI.Length > 0)
                await spotifyClient.RemoveTrackFromPlaylist(playlistUri, track.URI);
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

        // default spotify credentials (base64-encoded to keep the bots away)
        public const string encodedSpotifyId = "MWJmNDY5M1bLaH9WJiYjFhNGY0MWJjZWQ5YjJjMWNmZGJiZDI=";
        public const string encodedSpotifySecret = "Y2JlM2QxYTE5MzJkNDQ2MmFiOGUy3shTuf4Y2JhY2M3ZDdjYWU=";
        public bool UsedDefaultCredentials { get; private set; }

        public Spotify(string clientId = "", string clientSecret = "", string token = "", string refreshToken = "")
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
            _clientToken = token;
            _clientRefreshToken = refreshToken;

            if (_clientToken.Length == 0 && (_clientId.Length == 0 || _clientSecret.Length == 0))
            {
                _clientId = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifyId.Replace("1bLaH9", "")));
                _clientSecret = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedSpotifySecret.Replace("3shTuf4", "")));
                UsedDefaultCredentials = true;
            }
        }

        public async Task Authorize(bool login = false, bool needModify = false)
        {
            _client = null;

            if (!login)
            {
                var config = SpotifyClientConfig.CreateDefault();

                var request = new ClientCredentialsRequest(_clientId, _clientSecret);
                var response = await new OAuthClient(config).RequestToken(request);

                _client = new SpotifyClient(config.WithToken(response.AccessToken));
            }
            else
            {
                Swan.Logging.Logger.NoLogging();
                _server = new EmbedIOAuthServer(new Uri("http://localhost:48721/callback"), 48721);
                await _server.Start();

                var existingOk = false;
                if (_clientToken.Length != 0 || _clientRefreshToken.Length != 0)
                {
                    existingOk = await this.TryExistingToken();
                    loggedIn = true;
                    //new OAuthClient(config).RequestToken()
                }
                if (!existingOk)
                {
                    _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
                    _server.ErrorReceived += OnErrorReceived;

                    var scope = new List<string> {
                    Scopes.UserLibraryRead, Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative
                };

                    if (needModify)
                    {
                        scope.Add(Scopes.PlaylistModifyPublic);
                        scope.Add(Scopes.PlaylistModifyPrivate);
                    }

                    var request = new LoginRequest(_server.BaseUri, _clientId, LoginRequest.ResponseType.Code) { Scope = scope };

                    try
                    {
                        BrowserUtil.Open(request.ToUri());
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Unable to open URL, manually open: {0}", request.ToUri());
                    }
                }

                await IsClientReady();
            }
        }

        private async Task<bool> TryExistingToken() 
        {
            if (_clientToken.Length != 0)
            {
                Console.WriteLine("Testing Spotify access with existing token...");
                var client = new SpotifyClient(_clientToken);
                try
                {
                    var me = await client.UserProfile.Current();
                    Console.WriteLine("Spotify access is good!");
                    _client = client;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not make an API call with existing token: {ex}");
                }
            }
            if (_clientRefreshToken.Length != 0)
            {
                Console.WriteLine("Trying to renew access with refresh token...");
            //     var refreshRequest = new TokenSwapRefreshRequest(
            //     new Uri("http://localhost:48721/refresh"),
            //     _clientRefreshToken
            // );
            var refreshRequest = new AuthorizationCodeRefreshRequest(_clientId, _clientSecret, _clientRefreshToken);
                try
                {
                    var oauthClient = new OAuthClient();
                    var refreshResponse = await oauthClient.RequestToken(refreshRequest);
                    Console.WriteLine($"We got a new refreshed access token from server: {refreshResponse.AccessToken}");
                    _clientToken = refreshResponse.AccessToken;
                    _client = new SpotifyClient(_clientToken);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not refresh access token with refresh token: {ex}");
                }
            } else {
                Console.WriteLine("No refresh token present, cannot refresh existing access");
            }

            Console.WriteLine("Not possible to access Spotify API without login! Falling back to login flow...");
            return false;
        }

        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            await _server.Stop();

            var config = SpotifyClientConfig.CreateDefault();
            var tokenResponse = await new OAuthClient(config).RequestToken(
                new AuthorizationCodeTokenRequest(
                    _clientId, _clientSecret, response.Code, new Uri("http://localhost:48721/callback")
                )
            );

            Console.WriteLine("Spotify token: " + tokenResponse.AccessToken);
            _clientToken = tokenResponse.AccessToken;
            Console.WriteLine("Spotify refresh token: " + tokenResponse.RefreshToken);
            _clientRefreshToken = tokenResponse.RefreshToken;

            _client = new SpotifyClient(tokenResponse.AccessToken);
            loggedIn = true;
        }

        private async Task OnErrorReceived(object sender, string error, string state)
        {
            await _server.Stop();
            throw new Exception($"Aborting authorization, error received: {error}");
        }

        public async Task<bool> IsClientReady()
        {
            while (_client == null)
                await Task.Delay(1000);
            return true;
        }

        public async Task<List<Track>> GetLikes(int max = int.MaxValue, int offset = 0)
        {
            if (!loggedIn)
                throw new Exception("Can't get liked music as user is not logged in");

            List<Track> res = new List<Track>();
            int limit = Math.Min(max, 50);

            while (true)
            {
                var tracks = await _client.Library.GetTracks(new LibraryTracksRequest { Limit = limit, Offset = offset });

                foreach (var track in tracks.Items)
                {
                    string[] artists = ((IEnumerable<object>)track.Track.ReadProperty("artists")).Select(a => (string)a.ReadProperty("name")).ToArray();
                    string artist = artists[0];
                    string name = (string)track.Track.ReadProperty("name");
                    string album = (string)track.Track.ReadProperty("album").ReadProperty("name");
                    int duration = (int)track.Track.ReadProperty("durationMs");
                    res.Add(new Track { Album = album, Artist = artist, Title = name, Length = duration / 1000 });
                }

                if (tracks.Items.Count < limit || res.Count >= max)
                    break;

                offset += limit;
                limit = Math.Min(max - res.Count, 50);
            }

            return res;
        }

        public async Task RemoveTrackFromPlaylist(string playlistId, string trackUri)
        {
            var item = new PlaylistRemoveItemsRequest.Item { Uri = trackUri };
            var pr = new PlaylistRemoveItemsRequest();
            pr.Tracks = new List<PlaylistRemoveItemsRequest.Item>() { item };
            try { await _client.Playlists.RemoveItems(playlistId, pr); }
            catch { }
        }

        public async Task<(string?, string?, List<Track>)> GetPlaylist(string url, int max = int.MaxValue, int offset = 0)
        {
            var playlistId = GetPlaylistIdFromUrl(url);
            var p = await _client.Playlists.Get(playlistId);

            List<Track> res = new List<Track>();
            int limit = Math.Min(max, 100);

            while (true)
            {
                var tracks = await _client.Playlists.GetItems(playlistId, new PlaylistGetItemsRequest { Limit = limit, Offset = offset });

                foreach (var track in tracks.Items)
                {
                    try
                    {
                        string[] artists = ((IEnumerable<object>)track.Track.ReadProperty("artists")).Select(a => (string)a.ReadProperty("name")).ToArray();
                        var t = new Track()
                        {
                            Artist = artists[0],
                            Album = (string)track.Track.ReadProperty("album").ReadProperty("name"),
                            Title = (string)track.Track.ReadProperty("name"),
                            Length = (int)track.Track.ReadProperty("durationMs") / 1000,
                            URI = (string)track.Track.ReadProperty("uri"),
                        };
                        res.Add(t);
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (tracks.Items.Count < limit || res.Count >= max)
                    break;

                offset += limit;
                limit = Math.Min(max - res.Count, 100);
            }

            return (p.Name, p.Id, res);
        }

        private string GetPlaylistIdFromUrl(string url)
        {
            var uri = new Uri(url);
            var segments = uri.Segments;
            return segments[segments.Length - 1].TrimEnd('/');
        }

        public async Task<(Track, List<Track>)> GetAlbum(string url)
        {
            var albumId = GetAlbumIdFromUrl(url);
            var album = await _client.Albums.Get(albumId);

            List<Track> tracks = new List<Track>();

            foreach (var track in album.Tracks.Items)
            {
                var t = new Track()
                {
                    Album = album.Name,
                    Artist = track.Artists.First().Name,
                    Title = track.Name,
                    Length = track.DurationMs / 1000,
                    URI = track.Uri,
                };
                tracks.Add(t);
            }

            return (new Track { Album = album.Name, Artist = album.Artists.First().Name, Type = TrackType.Album }, tracks);
        }

        private string GetAlbumIdFromUrl(string url)
        {
            var uri = new Uri(url);
            var segments = uri.Segments;
            return segments[^1].TrimEnd('/');
        }
    }
}
