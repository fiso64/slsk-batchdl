namespace Sockseek.Core.Settings;

public class SpotifySettings
{
    internal SpotifySettings ShallowClone() => (SpotifySettings)MemberwiseClone();

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? Token { get; set; }

    public string? Refresh { get; set; }
}
