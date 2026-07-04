namespace Sockseek.Core;

/// <summary>
/// Event surface for search-service activity that is not tied to a particular
/// download workflow.
/// </summary>
public sealed class SearchEvents
{
    // Fired once per rate-limit window when the search semaphore is exhausted.
    public event Action<DateTimeOffset>? SearchRateLimited;

    // Fired when the rate-limit window resets and searching resumes.
    public event Action? SearchResumed;

    internal void RaiseSearchRateLimited(DateTimeOffset resetsAt) => SearchRateLimited?.Invoke(resetsAt);
    internal void RaiseSearchResumed() => SearchResumed?.Invoke();
}
