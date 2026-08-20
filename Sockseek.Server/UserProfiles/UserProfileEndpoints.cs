using Sockseek.Api;
using Sockseek.Core.UserProfiles;

namespace Sockseek.Server.UserProfiles;

internal static class UserProfileEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/users/{username}/profile", GetProfileAsync)
            .RequireOperator()
            .WithTags("User Browsing")
            .WithSummary("Gets a composite remote Soulseek user profile.")
            .Produces<UserProfileDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/users/{username}/picture", GetPictureAsync)
            .RequireOperator()
            .WithTags("User Browsing")
            .WithSummary("Gets a validated remote Soulseek user profile picture.")
            .Produces<byte[]>(
                StatusCodes.Status200OK,
                contentType: "image/jpeg",
                additionalContentTypes: ["image/png", "image/gif", "image/webp"])
            .Produces(StatusCodes.Status304NotModified)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> GetProfileAsync(
        string username,
        EngineSupervisor supervisor,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        UserProfileService? service = supervisor.UserProfiles;
        if (service is null)
            return Unavailable();
        try
        {
            return Results.Ok(await service.GetAsync(
                username,
                refresh,
                cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static async Task<IResult> GetPictureAsync(
        string username,
        HttpRequest request,
        HttpResponse response,
        EngineSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        UserProfileService? service = supervisor.UserProfiles;
        if (service is null)
            return Unavailable();
        try
        {
            UserPicture? picture = await service.GetPictureAsync(
                username,
                cancellationToken).ConfigureAwait(false);
            if (picture is null)
            {
                return Results.NotFound(new ApiErrorDto(
                    "The user has no available profile picture.",
                    "picture-unavailable"));
            }

            response.Headers.ETag = picture.ETag;
            response.Headers.CacheControl = "private, max-age=30, must-revalidate";
            response.Headers.XContentTypeOptions = "nosniff";
            if (MatchesEtag(request.Headers.IfNoneMatch, picture.ETag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            response.ContentLength = picture.Bytes.Length;
            return Results.Bytes(picture.Bytes, picture.MediaType);
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    private static bool MatchesEtag(
        Microsoft.Extensions.Primitives.StringValues values,
        string etag)
        => values
            .SelectMany(value => (value ?? "").Split(','))
            .Select(value => value.Trim())
            .Any(value => value == "*" || string.Equals(value, etag, StringComparison.Ordinal));

    private static IResult Failure(Exception exception)
        => exception switch
        {
            UserProfileAccessDeniedException => Results.NotFound(
                new ApiErrorDto("The Soulseek user was not found.", "user-not-found")),
            ArgumentException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "invalid-username")),
            UserProfileUnavailableException or InvalidOperationException => Unavailable(),
            _ => Unavailable(),
        };

    private static IResult Unavailable()
        => Results.Json(
            new ApiErrorDto("Soulseek profiles are unavailable.", "soulseek-unavailable"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
