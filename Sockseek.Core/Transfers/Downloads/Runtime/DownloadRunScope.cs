using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;

namespace Sockseek.Core.Transfers.Downloads.Runtime;

internal sealed class DownloadRunScope : IDisposable, IAsyncDisposable
{
    private readonly EngineSettings settings;
    private readonly SoulseekClientManager clientManager;
    private readonly ActiveDownloadTracker activeDownloads;
    private readonly DownloadedFileCache downloadedFiles;
    private readonly UserSuccessTracker userSuccesses;
    private readonly DownloadEvents events;
    private readonly SearchEvents searchEvents;
    private readonly StaleDownloadCoordinator staleDownloads;
    private readonly CancellationTokenSource appCts = new();
    private readonly SemaphoreSlim jobSemaphore;
    private readonly SemaphoreSlim extractorSemaphore;
    private Searcher? searcher;
    private Downloader? downloader;
    private Task? staleDownloadTask;
    private bool servicesInitialized;
    private bool disposed;

    public DownloadRunScope(
        EngineSettings settings,
        SoulseekClientManager clientManager,
        ActiveDownloadTracker activeDownloads,
        DownloadedFileCache downloadedFiles,
        UserSuccessTracker userSuccesses,
        DownloadEvents events,
        SearchEvents searchEvents,
        StaleDownloadCoordinator staleDownloads)
    {
        this.settings = settings;
        this.clientManager = clientManager;
        this.activeDownloads = activeDownloads;
        this.downloadedFiles = downloadedFiles;
        this.userSuccesses = userSuccesses;
        this.events = events;
        this.searchEvents = searchEvents;
        this.staleDownloads = staleDownloads;
        jobSemaphore = new SemaphoreSlim(settings.ConcurrentJobs);
        extractorSemaphore = new SemaphoreSlim(settings.ConcurrentExtractors);
    }

    public CancellationToken Token => appCts.Token;
    public bool IsCancellationRequested => appCts.IsCancellationRequested;

    public Searcher Searcher => searcher
        ?? throw new InvalidOperationException("Engine search services have not been initialized.");

    public Downloader Downloader => downloader
        ?? throw new InvalidOperationException("Engine download services have not been initialized.");

    public async Task EnsureServicesInitializedAsync(CancellationToken ct, bool automaticStaleChecksEnabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (servicesInitialized)
            return;

        try
        {
            await clientManager.EnsureConnectedAndLoggedInAsync(settings, ct);
        }
        catch (SoulseekConnectionUnavailableException) when (clientManager.HasFatalError)
        {
            throw;
        }
        catch (Exception ex)
        {
            SockseekLog.Soulseek.Error(ex, "Initial Soulseek login failed. Reconnection will be attempted automatically in the background");
        }

        await clientManager.WaitUntilReadyAsync(ct);
        var client = clientManager.Client ?? throw new InvalidOperationException("Soulseek client is not available after login.");
        searcher = new Searcher(client, userSuccesses, events, settings.SearchesPerTime, settings.SearchRenewTime, settings.ConcurrentSearches, searchEvents);
        downloader = new Downloader(client, clientManager, activeDownloads, downloadedFiles, events, staleDownloads);

        if (automaticStaleChecksEnabled)
            staleDownloadTask ??= Task.Run(() => staleDownloads.RunAsync(appCts.Token), appCts.Token);

        SockseekLog.Jobs.Debug("Soulseek services initialized");
        servicesInitialized = true;
    }

    public async Task WithJobSlot(CancellationToken ct, Func<Task> action)
    {
        await jobSemaphore.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            jobSemaphore.Release();
        }
    }

    public async Task<T> WithJobSlot<T>(CancellationToken ct, Func<Task<T>> action)
    {
        await jobSemaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            jobSemaphore.Release();
        }
    }

    public async Task<T> WithExtractorSlot<T>(CancellationToken ct, Func<Task<T>> action)
    {
        await extractorSemaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            extractorSemaphore.Release();
        }
    }

    public void Cancel()
    {
        if (!appCts.IsCancellationRequested)
            appCts.Cancel();
    }

    public async Task CancelAsync()
    {
        if (!appCts.IsCancellationRequested)
            await appCts.CancelAsync();

        await WaitForStaleDownloadTaskAsync();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Cancel();
        WaitForStaleDownloadTask();
        DisposeManaged();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        await CancelAsync();
        DisposeManaged();
        GC.SuppressFinalize(this);
    }

    private void DisposeManaged()
    {
        searcher?.Dispose();
        jobSemaphore.Dispose();
        extractorSemaphore.Dispose();
        appCts.Dispose();
        disposed = true;
    }

    private async Task WaitForStaleDownloadTaskAsync()
    {
        if (staleDownloadTask == null)
            return;

        try { await staleDownloadTask; }
        catch (OperationCanceledException) when (appCts.IsCancellationRequested) { }
    }

    private void WaitForStaleDownloadTask()
    {
        if (staleDownloadTask == null)
            return;

        try { staleDownloadTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (appCts.IsCancellationRequested) { }
    }
}
