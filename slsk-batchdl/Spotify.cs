using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Swan;

public class Spotify
{
    private EmbedIOAuthServer _server;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private SpotifyClient _client;
    private bool loggedIn = false;

    public Spotify(string clientId, string clientSecret)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public async Task Authorize()
    {
        var config = SpotifyClientConfig.CreateDefault();

        var request = new ClientCredentialsRequest(_clientId, _clientSecret);
        var response = await new OAuthClient(config).RequestToken(request);

        _client = new SpotifyClient(config.WithToken(response.AccessToken));
    }

    public async Task AuthorizeLogin()
    {
        Swan.Logging.Logger.NoLogging();
        _server = new EmbedIOAuthServer(new Uri("http://localhost:48721/callback"), 48721);
        await _server.Start();

        _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
        _server.ErrorReceived += OnErrorReceived;

        var request = new LoginRequest(_server.BaseUri, _clientId, LoginRequest.ResponseType.Code)
        {
            Scope = new List<string> { Scopes.UserLibraryRead, Scopes.PlaylistReadPrivate }
        };

        BrowserUtil.Open(request.ToUri());
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
            throw new Exception("Can't get liked music, not logged in");

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
                res.Add(new Track { Album = album, ArtistName = artist, TrackTitle = name, Length = duration / 1000 });
            }

            if (tracks.Items.Count < limit || res.Count >= max)
                break;

            offset += limit;
            limit = Math.Min(max - res.Count, 50);
        }

        return res;
    }


    public async Task<(string?, List<Track>)> GetPlaylist(string url, int max = int.MaxValue, int offset = 0)
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
                string[] artists = ((IEnumerable<object>)track.Track.ReadProperty("artists")).Select(a => (string)a.ReadProperty("name")).ToArray();
                string artist = artists[0];
                string name = (string)track.Track.ReadProperty("name");
                string album = (string)track.Track.ReadProperty("album").ReadProperty("name");
                int duration = (int)track.Track.ReadProperty("durationMs");
                res.Add(new Track { Album = album, ArtistName = artist, TrackTitle = name, Length = duration / 1000 });
            }

            if (tracks.Items.Count < limit || res.Count >= max)
                break;

            offset += limit;
            limit = Math.Min(max - res.Count, 100);
        }

        return (p.Name, res);
    }

    private string GetPlaylistIdFromUrl(string url)
    {
        var uri = new Uri(url);
        var segments = uri.Segments;
        return segments[segments.Length - 1].TrimEnd('/');
    }
}
