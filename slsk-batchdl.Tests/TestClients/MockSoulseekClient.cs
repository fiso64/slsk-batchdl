using Soulseek;
using Soulseek.Diagnostics;
using System.Collections.Concurrent;
using System.Net;

namespace Tests.ClientTests
{
    public partial class MockSoulseekClient : ISoulseekClient
    {
        public IReadOnlyCollection<Transfer> Downloads => throw new NotImplementedException();

        public SoulseekClientStates State => SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn;

        public SoulseekClientOptions Options => throw new NotImplementedException();

        private List<Soulseek.SearchResponse> index;

        public MockSoulseekClient(List<Soulseek.SearchResponse> index)
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

        public Task SetSharedCountsAsync(int directories, int files, CancellationToken? cancellationToken = null)
        {
            return Task.CompletedTask;
        }

        public Task<BrowseResponse> BrowseAsync(string username, BrowseOptions? options = null, CancellationToken? cancellationToken = null)
        {
            var user = index.FirstOrDefault(x => x.Username == username);

            if (user == null)
            {
                throw new UserNotFoundException($"User {username} not found");
            }

            var directories = user.Files
                .GroupBy(x => Utils.GetDirectoryNameSlsk(x.Filename))
                .Select(x => new Soulseek.Directory(x.Key, x));

            return Task.FromResult(new BrowseResponse(directories));
        }

        public Task<(Search Search, IReadOnlyCollection<SearchResponse> Responses)> SearchAsync(SearchQuery query, SearchScope scope = null, int? token = null, SearchOptions options = null, CancellationToken? cancellationToken = null)
        {
            return SearchAsyncInternal(query, null, scope, token, options, cancellationToken);
        }

        public Task<Search> SearchAsync(SearchQuery query, Action<SearchResponse> responseHandler, SearchScope scope = null, int? token = null, SearchOptions options = null, CancellationToken? cancellationToken = null)
        {
            return SearchAsyncInternal(query, responseHandler, scope, token, options, cancellationToken).ContinueWith(t => t.Result.Search);
        }

        private Task<(Search Search, IReadOnlyCollection<SearchResponse> Responses)> SearchAsyncInternal(SearchQuery query, Action<SearchResponse>? responseHandler, SearchScope scope = null, int? token = null, SearchOptions options = null, CancellationToken? cancellationToken = null)
        {
            options ??= new SearchOptions();
            var searchToken = token ?? Random.Shared.Next();
            var responses = new List<SearchResponse>();
            var totalFileCount = 0;
            var totalLockedFileCount = 0;

            foreach (var user in index)
            {
                var matchingFiles = new List<Soulseek.File>();

                foreach (var file in user.Files)
                {
                    var path = file.Filename.ToLower();
                    bool matches = query.Terms.All(term => path.Contains(term.ToLower()));

                    if (matches && (options.FileFilter?.Invoke(file) ?? true))
                    {
                        matchingFiles.Add(file);
                    }
                }

                if (matchingFiles.Any())
                {
                    var response = new SearchResponse(
                        username: user.Username,
                        token: searchToken,
                        hasFreeUploadSlot: user.HasFreeUploadSlot,
                        uploadSpeed: user.UploadSpeed,
                        queueLength: user.QueueLength,
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
                        responseHandler?.Invoke(response);
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

        public async Task<Transfer> DownloadAsync(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            var user = index.FirstOrDefault(x => x.Username == username);
            if (user == null)
            {
                throw new UserNotFoundException($"User {username} not found");
            }

            // Find the file in the directories
            Soulseek.File? foundFile = user.Files.FirstOrDefault(x => x.Filename.Equals(remoteFilename, StringComparison.OrdinalIgnoreCase));
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
            await Task.Run(async () =>
            {
                try
                {
                    options?.StateChanged?.Invoke((TransferStates.Queued, transfer));
                    //await Task.Delay(100); // Simulate queue wait

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
                        await stream.WriteAsync(buffer, 0, currentChunk, cancellationToken.GetValueOrDefault());
                        await stream.FlushAsync(cancellationToken.GetValueOrDefault());

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

                        if (cancellationToken?.IsCancellationRequested == true)
                        {
                            transfer = new Transfer(
                                direction: TransferDirection.Download,
                                username: username,
                                filename: remoteFilename,
                                token: transferToken,
                                state: TransferStates.Cancelled,
                                size: fileSize,
                                startOffset: startOffset,
                                bytesTransferred: bytesTransferred,
                                averageSpeed: speed,
                                startTime: startTime
                            );
                            options?.StateChanged?.Invoke((TransferStates.Cancelled, transfer));
                            return;

                        }
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
                    throw; // rethrow to ensure the outer task also faults
                }
            });

            return transfer;
        }
    }
}