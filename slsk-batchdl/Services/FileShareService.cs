using System.Collections.Concurrent;
using System.Net;
using Soulseek;

using Directory = System.IO.Directory;
using File = System.IO.File;

/// <summary>
/// Manages file sharing on the Soulseek network. Indexes local music files and provides
/// the delegate implementations (browse, search, directory contents, upload enqueue) that
/// <see cref="SoulseekClientManager"/> wires into the Soulseek client options.
/// </summary>
/// <remarks>
/// Designed to be used with a long-lived <see cref="SoulseekClientManager"/> instance.
/// Call <see cref="RebuildIndex"/> to catalog shared directories,
/// then pass this service to <see cref="SoulseekClientManager.SetFileShareService"/>
/// before connecting. The client manager will automatically wire the delegates and
/// report accurate share counts after login.
/// </remarks>
public class FileShareService
{
    // Soulseek path (backslash) → local path
    private readonly ConcurrentDictionary<string, string> _pathMap = new(StringComparer.OrdinalIgnoreCase);
    // Directory name → list of Soulseek File objects
    private readonly ConcurrentDictionary<string, List<Soulseek.File>> _directoryIndex = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".m4a", ".opus", ".wav", ".aac", ".wma", ".alac", ".ape", ".wv"
    };

    private ISoulseekClient? _client;

    /// <summary>
    /// Sets the active Soulseek client reference, used to resolve the local username
    /// in search responses. Called by <see cref="SoulseekClientManager"/> after login.
    /// </summary>
    public void SetClient(ISoulseekClient client) => _client = client;

    /// <summary>Returns the number of shared directories and files currently indexed.</summary>
    public (int directories, int files) GetShareCounts() => (_directoryIndex.Count, _pathMap.Count);

    /// <summary>
    /// Scans the given directories for music files and rebuilds the internal index.
    /// Replaces any previously indexed content. Non-existent directories are skipped.
    /// </summary>
    public void RebuildIndex(List<string> sharedDirectories)
    {
        _pathMap.Clear();
        _directoryIndex.Clear();

        foreach (var dir in sharedDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            var dirName = new DirectoryInfo(dir).Name;
            IndexDirectory(dir, dirName);
        }

        Logger.Info($"Indexed {_pathMap.Count} files in {_directoryIndex.Count} directories for sharing");
    }

    private void IndexDirectory(string localRoot, string slskRoot)
    {
        int code = 1;
        foreach (var file in Directory.EnumerateFiles(localRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!MusicExtensions.Contains(ext)) continue;

            var relativePath = Path.GetRelativePath(localRoot, file);
            var slskPath = slskRoot + "\\" + relativePath.Replace('/', '\\');
            var slskDir = slskRoot + "\\" + Path.GetRelativePath(localRoot, Path.GetDirectoryName(file)!).Replace('/', '\\');

            _pathMap[slskPath] = file;

            long size = 0;
            try { size = new FileInfo(file).Length; } catch { }

            var slskFile = new Soulseek.File(
                code++,
                slskPath,
                size,
                ext,
                attributeList: null
            );

            _directoryIndex.AddOrUpdate(
                slskDir,
                _ => new List<Soulseek.File> { slskFile },
                (_, list) => { list.Add(slskFile); return list; });
        }
    }

    // --- Soulseek Sharing Delegates ---
    // These methods match the delegate signatures expected by SoulseekClientOptions
    // and are wired automatically by SoulseekClientManager.SetFileShareService().

    /// <summary>Returns all shared directories and files when a remote user browses this client.</summary>
    public Task<BrowseResponse> BrowseResponseResolver(string username, IPEndPoint endpoint)
    {
        var directories = _directoryIndex.Select(kvp =>
            new Soulseek.Directory(kvp.Key, (IEnumerable<Soulseek.File>)kvp.Value)).ToList();
        return Task.FromResult(new BrowseResponse(directories));
    }

    /// <summary>
    /// Evaluates an incoming search query against the shared file index.
    /// Returns matching files or null if no matches are found.
    /// </summary>
    public Task<SearchResponse?> SearchResponseResolver(string username, int token, SearchQuery query)
    {
        if (_pathMap.IsEmpty)
            return Task.FromResult<SearchResponse?>(null);

        var terms = query.Terms.Select(t => t.ToLowerInvariant()).ToList();
        var exclusions = query.Exclusions.Select(e => e.ToLowerInvariant()).ToList();

        var matches = new List<Soulseek.File>();
        foreach (var (slskPath, _) in _pathMap)
        {
            var lower = slskPath.ToLowerInvariant();
            if (terms.All(t => lower.Contains(t)) && !exclusions.Any(e => lower.Contains(e)))
            {
                var dir = Path.GetDirectoryName(slskPath)?.Replace('/', '\\') ?? "";
                if (_directoryIndex.TryGetValue(dir, out var files))
                {
                    var match = files.FirstOrDefault(f =>
                        string.Equals(f.Filename, slskPath, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        matches.Add(match);
                }
            }
        }

        if (matches.Count == 0)
            return Task.FromResult<SearchResponse?>(null);

        var response = new SearchResponse(
            username: _client?.Username ?? "unknown",
            token: token,
            hasFreeUploadSlot: true,
            uploadSpeed: 0,
            queueLength: 0,
            fileList: matches
        );
        return Task.FromResult<SearchResponse?>(response);
    }

    /// <summary>Returns the file listing for a specific shared directory requested by a remote user.</summary>
    public Task<IEnumerable<Soulseek.Directory>> DirectoryContentsResolver(string username, IPEndPoint endpoint, int token, string directoryName)
    {
        var normalizedDir = directoryName.TrimEnd('\\');
        if (_directoryIndex.TryGetValue(normalizedDir, out var files))
        {
            IEnumerable<Soulseek.Directory> result = new[] { new Soulseek.Directory(normalizedDir, (IEnumerable<Soulseek.File>)files) };
            return Task.FromResult(result);
        }

        return Task.FromResult(Enumerable.Empty<Soulseek.Directory>());
    }

    /// <summary>
    /// Validates that a requested file is shared and exists on disk.
    /// Throws <see cref="DownloadEnqueueException"/> if the file is not available.
    /// </summary>
    public Task EnqueueDownloadHandler(string username, IPEndPoint endpoint, string filename)
    {
        if (!_pathMap.TryGetValue(filename, out var localPath) || !System.IO.File.Exists(localPath))
            throw new DownloadEnqueueException("File not shared");

        Logger.Info($"Upload enqueued: {filename} to {username}");
        return Task.CompletedTask;
    }
}
