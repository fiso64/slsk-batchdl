using Sockseek.Core.Settings;

namespace Sockseek.Core.UserProfiles;

/// <summary>Immutable daemon-lifetime local profile advertised to allowed peers.</summary>
public sealed record LocalUserProfile(string Description, UserPicture? Picture)
{
    public static async Task<LocalUserProfile> LoadAsync(
        EngineSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string description = UserProfileText.NormalizeDescription(settings.UserDescription);
        UserPicture? picture = null;
        if (!string.IsNullOrWhiteSpace(settings.UserPicturePath))
        {
            try
            {
                picture = await UserPictureCodec.LoadAndNormalizeLocalAsync(
                    settings.UserPicturePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SockseekLog.Daemon.Warn(
                    $"Profile picture '{settings.UserPicturePath}' was not loaded because processing "
                    + $"exceeded {UserPictureCodec.MaximumWorkDuration.TotalSeconds:0} seconds. "
                    + "Continuing without a profile picture.");
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or SixLabors.ImageSharp.ImageProcessingException)
            {
                SockseekLog.Daemon.Warn(
                    $"Profile picture '{settings.UserPicturePath}' was not loaded: {ex.Message} "
                    + "Continuing without a profile picture.");
            }
        }

        return new LocalUserProfile(description, picture);
    }
}
