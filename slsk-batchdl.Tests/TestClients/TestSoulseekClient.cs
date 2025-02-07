using Soulseek;
using Soulseek.Diagnostics;
using System.Collections.Concurrent;
using System.Net;

public partial class TestSoulseekClient : ISoulseekClient
{
    public IReadOnlyCollection<Transfer> Downloads => throw new NotImplementedException();

    public SoulseekClientStates State => SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn;

    public SoulseekClientOptions Options => throw new NotImplementedException();

    private List<(Soulseek.SearchResponse, List<Soulseek.Directory>)> index;

    public TestSoulseekClient(List<(Soulseek.SearchResponse, List<Soulseek.Directory>)> index)
    {
        this.index = index;
    }

    public Task ConnectAsync(string username, string password, CancellationToken? cancellationToken = null)
    {
        return ConnectAsync("", 0, username, password, cancellationToken);
    }

    public Task ConnectAsync(string address, int port, string username, string password, CancellationToken? cancellationToken = null)
    {
        return Task.CompletedTask;
    }

    public Task<BrowseResponse> BrowseAsync(string username, BrowseOptions? options = null, CancellationToken? cancellationToken = null)
    {
        var userFiles = index.FirstOrDefault(x => x.Item1.Username == username);
        
        if (userFiles.Item2 == null)
        {
            throw new UserNotFoundException($"User {username} not found");
        }

        return Task.FromResult(new BrowseResponse(userFiles.Item2));
    }

    public Task<(Search Search, IReadOnlyCollection<SearchResponse> Responses)> SearchAsync(SearchQuery query, SearchScope scope = null, int? token = null, SearchOptions options = null, CancellationToken? cancellationToken = null)
    {
        options ??= new SearchOptions();
        var searchToken = token ?? Random.Shared.Next();
        var responses = new List<SearchResponse>();
        var totalFileCount = 0;
        var totalLockedFileCount = 0;

        foreach (var userFiles in index)
        {
            var matchingFiles = new List<Soulseek.File>();
            
            foreach (var directory in userFiles.Item2)
            {
                foreach (var file in directory.Files)
                {
                    var path = $"{directory.Name}\\{file.Filename}".ToLower();
                    bool matches = query.Terms.All(term => path.Contains(term.ToLower()));
                    
                    if (matches && (options.FileFilter?.Invoke(file) ?? true))
                    {
                        matchingFiles.Add(file);
                    }
                }
            }

            if (matchingFiles.Any())
            {
                var response = new SearchResponse(
                    username: userFiles.Item1.Username,
                    token: searchToken,
                    hasFreeUploadSlot: userFiles.Item1.HasFreeUploadSlot,
                    uploadSpeed: userFiles.Item1.UploadSpeed,
                    queueLength: userFiles.Item1.QueueLength,
                    fileList: matchingFiles
                );

                if (!options.FilterResponses || 
                    (matchingFiles.Count >= options.MinimumResponseFileCount &&
                    response.QueueLength <= options.MaximumPeerQueueLength &&
                    response.UploadSpeed >= options.MinimumPeerUploadSpeed &&
                    (options.ResponseFilter?.Invoke(response) ?? true)))
                {
                    responses.Add(response);
                    totalFileCount += response.FileCount;
                    totalLockedFileCount += response.LockedFileCount;
                    options.ResponseReceived?.Invoke((null, response));
                }

                if (responses.Count >= options.ResponseLimit)
                    break;
            }
        }

        var search = new Search(
            searchText: query.SearchText,
            token: searchToken,
            state: SearchStates.Completed,
            responseCount: responses.Count,
            fileCount: totalFileCount,
            lockedFileCount: totalLockedFileCount
        );

        return Task.FromResult((search, (IReadOnlyCollection<SearchResponse>)responses));
    }

    public Task<Search> SearchAsync(SearchQuery query, Action<SearchResponse> responseHandler, SearchScope scope = null, int? token = null, SearchOptions options = null, CancellationToken? cancellationToken = null)
    {
        var searchOptions = options ?? new SearchOptions(responseReceived: (tuple) => responseHandler?.Invoke(tuple.Response));
        return SearchAsync(query, scope, token, searchOptions, cancellationToken).ContinueWith(t => t.Result.Search);
    }

    public Task<Transfer> DownloadAsync(string username, string remoteFilename, string localFilename, long? size = null, long startOffset = 0, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
    {
        async Task<Stream> StreamFactory()
        {
            var directory = Path.GetDirectoryName(localFilename);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            return System.IO.File.Create(localFilename);
        }

        return DownloadAsync(username, remoteFilename, StreamFactory, size, startOffset, token, options, cancellationToken);
    }

    public Task<Transfer> DownloadAsync(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
    {
        var userFiles = index.FirstOrDefault(x => x.Item1.Username == username);
        if (userFiles.Item2 == null)
        {
            throw new UserNotFoundException($"User {username} not found");
        }

        // Find the file in the directories
        Soulseek.File foundFile = null;
        foreach (var directory in userFiles.Item2)
        {
            foundFile = directory.Files.FirstOrDefault(f => 
                $"{directory.Name}\\{f.Filename}".Equals(remoteFilename, StringComparison.OrdinalIgnoreCase));
            if (foundFile != null) break;
        }

        if (foundFile == null)
        {
            throw new FileNotFoundException($"File {remoteFilename} not found for user {username}");
        }

        var transferToken = token ?? Random.Shared.Next();
        var fileSize = size ?? foundFile.Size;

        var transfer = new Transfer(
            direction: TransferDirection.Download,
            username: username,
            filename: remoteFilename,
            token: transferToken,
            state: TransferStates.Queued,
            size: fileSize,
            startOffset: startOffset
        );

        // Simulate the download process asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                options?.StateChanged?.Invoke((TransferStates.Queued, transfer));
                await Task.Delay(100); // Simulate queue wait

                using var stream = await outputStreamFactory();
                var startTime = DateTime.UtcNow;
                var bytesTransferred = startOffset;
                var chunkSize = 16384; // 16KB chunks
                var buffer = new byte[chunkSize];

                // Fill buffer with repeating pattern
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = (byte)(i % 256);
                }

                transfer = new Transfer(
                    direction: TransferDirection.Download,
                    username: username,
                    filename: remoteFilename,
                    token: transferToken,
                    state: TransferStates.InProgress,
                    size: fileSize,
                    startOffset: startOffset,
                    bytesTransferred: bytesTransferred,
                    startTime: startTime
                );

                options?.StateChanged?.Invoke((TransferStates.InProgress, transfer));

                // Skip to start offset if needed
                if (startOffset > 0)
                {
                    stream.Seek(startOffset, SeekOrigin.Begin);
                }

                while (bytesTransferred < fileSize)
                {
                    await Task.Delay(50); // Simulate network delay
                    var remainingBytes = fileSize - bytesTransferred;
                    var currentChunk = (int)Math.Min(chunkSize, remainingBytes);

                    // Write the actual data
                    await stream.WriteAsync(buffer, 0, currentChunk);
                    await stream.FlushAsync();
                    
                    bytesTransferred += currentChunk;

                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesTransferred / elapsed.TotalSeconds;

                    transfer = new Transfer(
                        direction: TransferDirection.Download,
                        username: username,
                        filename: remoteFilename,
                        token: transferToken,
                        state: TransferStates.InProgress,
                        size: fileSize,
                        startOffset: startOffset,
                        bytesTransferred: bytesTransferred,
                        averageSpeed: speed,
                        startTime: startTime
                    );

                    options?.ProgressUpdated?.Invoke((bytesTransferred - currentChunk, transfer));
                }

                transfer = new Transfer(
                    direction: TransferDirection.Download,
                    username: username,
                    filename: remoteFilename,
                    token: transferToken,
                    state: TransferStates.Completed,
                    size: fileSize,
                    startOffset: startOffset,
                    bytesTransferred: bytesTransferred,
                    averageSpeed: bytesTransferred / (DateTime.UtcNow - startTime).TotalSeconds,
                    startTime: startTime,
                    endTime: DateTime.UtcNow
                );

                options?.StateChanged?.Invoke((TransferStates.Completed, transfer));
            }
            catch (Exception ex)
            {
                transfer = new Transfer(
                    direction: TransferDirection.Download,
                    username: username,
                    filename: remoteFilename,
                    token: transferToken,
                    state: TransferStates.Errored,
                    size: fileSize,
                    startOffset: startOffset,
                    exception: ex
                );

                options?.StateChanged?.Invoke((TransferStates.Errored, transfer));
            }
        });

        return Task.FromResult(transfer);
    }
}
