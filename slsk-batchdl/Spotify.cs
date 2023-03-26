using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Swan;

namespace Spotify
{
    public class Client
    {
        private EmbedIOAuthServer _server;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private SpotifyClient _client;

        public Client(string clientId, string clientSecret)
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
            _server = new EmbedIOAuthServer(new Uri("http://localhost:5000/callback"), 5000);
            await _server.Start();

            _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
            _server.ErrorReceived += OnErrorReceived;

            var request = new LoginRequest(_server.BaseUri, _clientId, LoginRequest.ResponseType.Code)
            {
                Scope = new List<string> { Scopes.UserReadEmail }
            };
            BrowserUtil.Open(request.ToUri());
        }

        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            await _server.Stop();

            var config = SpotifyClientConfig.CreateDefault();
            var tokenResponse = await new OAuthClient(config).RequestToken(
                new AuthorizationCodeTokenRequest(
                    _clientId, _clientSecret, response.Code, new Uri("http://localhost:5000/callback")
                )
            );
            _client = new SpotifyClient(tokenResponse.AccessToken);
        }

        private async Task OnErrorReceived(object sender, string error, string state)
        {
            Console.WriteLine($"Aborting authorization, error received: {error}");
            await _server.Stop();
        }

        public async Task<bool> IsClientReady()
        {
            while (_client == null)
                await Task.Delay(1000);
            return true;
        }

        public async Task<(string?, List<(string, string, int)> )> GetPlaylist(string url)
        {
            var playlistId = GetPlaylistIdFromUrl(url);
            var p = await _client.Playlists.Get(playlistId);
            var tracks = await _client.Playlists.GetItems(playlistId);
            List<(string, string, int)> res = new List<(string, string, int)>();

            foreach (var track in tracks.Items)
            {
                string[] artists = ((IEnumerable<object>)track.Track.ReadProperty("artists")).Select(a => (string)a.ReadProperty("name")).ToArray();
                string artist = artists[0];
                string name = (string)track.Track.ReadProperty("name");
                int duration = (int)track.Track.ReadProperty("durationMs");
                res.Add((artist, name, duration / 1000));
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
}
