using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Swan;

public class Spotify
{
    private EmbedIOAuthServer _server;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private SpotifyClient? _client;
    private bool loggedIn = false;

    // default spotify credentials (base64-encoded to keep the bots away)
    public const string encodedSpotifyId = "MWJmNDY5M1bLaH9WJiYjFhNGY0MWJjZWQ5YjJjMWNmZGJiZDI=";
    public const string encodedSpotifySecret = "Y2JlM2QxYTE5MzJkNDQ2MmFiOGUy3shTuf4Y2JhY2M3ZDdjYWU=";
    public bool UsedDefaultCredentials { get; private set; }

    public Spotify(string clientId="", string clientSecret="")
    {
        _clientId = clientId;
        _clientSecret = clientSecret;

        if (_clientId == "" || _clientSecret == "")
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

            BrowserUtil.Open(request.ToUri());

            await IsClientReady();
        }
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

        return (new Track { Album = album.Name, Artist = album.Artists.First().Name, IsAlbum = true }, tracks);
    }

    private string GetAlbumIdFromUrl(string url)
    {
        var uri = new Uri(url);
        var segments = uri.Segments;
        return segments[segments.Length - 1].TrimEnd('/');
    }

}
