using Soulseek;

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

        public static MockSoulseekClient FromLocalPaths(bool useTags, params string[] localPaths)
        {
            var files = localPaths.SelectMany(path =>
                System.IO.Directory.Exists(path)
                    ? System.IO.Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    : new[] { path });

            var fileList = files
                .Select((path, i) =>
                {

                    var attributes = new List<Soulseek.FileAttribute>();

                    if (Utils.IsMusicFile(path))
                    {
                        if (useTags)
                        {
                            using (var file = TagLib.File.Create(path))
                            {
                                if (file.Properties != null)
                                {
                                    attributes.Add(new Soulseek.FileAttribute(FileAttributeType.BitRate, file.Properties.AudioBitrate));
                                    attributes.Add(new Soulseek.FileAttribute(FileAttributeType.Length, (int)file.Properties.Duration.TotalSeconds));
                                    attributes.Add(new Soulseek.FileAttribute(FileAttributeType.VariableBitRate, file.Properties.BitsPerSample > 0 ? 1 : 0));

                                    if (file.Properties.AudioSampleRate > 0)
                                        attributes.Add(new Soulseek.FileAttribute(FileAttributeType.SampleRate, file.Properties.AudioSampleRate));

                                    if (file.Properties.BitsPerSample > 0)
                                        attributes.Add(new Soulseek.FileAttribute(FileAttributeType.BitDepth, file.Properties.BitsPerSample));
                                }
                            }
                        }
                        else
                        {
                            // Generate deterministic length from filename
                            var filename = Path.GetFileName(path);
                            var length = Math.Abs(filename.GetHashCode() % 1000) + 1; // 1-1000 seconds
                            attributes.Add(new Soulseek.FileAttribute(FileAttributeType.Length, length));
                        }
                    }

                    return new Soulseek.File(
                        i + 1,
                        path.Replace('/', '\\'),
                        new FileInfo(path).Length,
                        Path.GetExtension(path),
                        attributeList: attributes
                    );
                })
                .ToList();

            var index = new List<SearchResponse>() {
                new SearchResponse(
                    username: "local",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100,
                    queueLength: 0,
                    fileList: fileList
                )
            };

            return new MockSoulseekClient(index);
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
                .Select(g => new Soulseek.Directory(
                    g.Key.Replace('/', '\\'), // Soulseek ALWAYS returns paths with separator \, regardless of OS.
                    g.Select(f => new Soulseek.File(
                        f.Code,
                        f.Filename.Replace('/', '\\'),
                        f.Size,
                        f.Extension,
                        f.Attributes
                    )).ToList()
                ));

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
                query: query,
                token: searchToken,
                state: SearchStates.Completed,
                responseCount: responses.Count,
                fileCount: totalFileCount,
                lockedFileCount: totalLockedFileCount,
                scope: new SearchScope(SearchScopeType.Network)
            );

            return Task.FromResult((search, (IReadOnlyCollection<SearchResponse>)responses));
        }

        private static readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(1, 1);

        public async Task<Transfer> DownloadAsync(string username, string remoteFilename, string localFilename, long? size = null, long startOffset = 0, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            await _downloadSemaphore.WaitAsync(cancellationToken.GetValueOrDefault(CancellationToken.None));
            try
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

                return await DownloadAsyncInternal(username, remoteFilename, StreamFactory, size, startOffset, token, options, cancellationToken);
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }

        public async Task<Transfer> DownloadAsync(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            await _downloadSemaphore.WaitAsync(cancellationToken.GetValueOrDefault(CancellationToken.None));
            try
            {
                return await DownloadAsyncInternal(username, remoteFilename, outputStreamFactory, size, startOffset, token, options, cancellationToken);
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }

        private async Task<Transfer> DownloadAsyncInternal(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions options = null, CancellationToken? cancellationToken = null)
        {
            var transferToken = token ?? Random.Shared.Next();
            long fileSize;
            string sourceFilePath = null;

            if (username == "local")
            {
                // For local user, try to find the actual file in the filesystem
                sourceFilePath = Path.GetFullPath(remoteFilename);
                if (!System.IO.File.Exists(sourceFilePath))
                {
                    throw new FileNotFoundException($"Local file {sourceFilePath} not found");
                }
                fileSize = (long)(size == null || size == -1 ? new FileInfo(sourceFilePath).Length : size);
            }
            else
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
                fileSize = size ?? foundFile.Size;
            }

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
                    options?.StateChanged?.Invoke((TransferStates.Initializing, transfer));
                    //await Task.Delay(100); // Simulate queue wait

                    using var outputStream = await outputStreamFactory();
                    var startTime = DateTime.UtcNow;
                    var bytesTransferred = startOffset;
                    var chunkSize = 16384; // 16KB chunks

                    if (sourceFilePath != null)
                    {
                        // Copy from local file
                        using var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read);
                        if (startOffset > 0)
                        {
                            sourceStream.Seek(startOffset, SeekOrigin.Begin);
                        }
                        var buffer = new byte[chunkSize];
                        int bytesRead;

                        options?.StateChanged?.Invoke((TransferStates.InProgress, transfer));

                        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, chunkSize)) > 0)
                        {
                            await outputStream.WriteAsync(buffer, 0, bytesRead);
                            await outputStream.FlushAsync();
                            bytesTransferred += bytesRead;

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

                            options?.ProgressUpdated?.Invoke((bytesTransferred - bytesRead, transfer));

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
                    }
                    else
                    {
                        // Generate fake data as before
                        var buffer = new byte[chunkSize];
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
                            outputStream.Seek(startOffset, SeekOrigin.Begin);
                        }

                        while (bytesTransferred < fileSize)
                        {
                            await Task.Delay(50); // Simulate network delay
                            var remainingBytes = fileSize - bytesTransferred;
                            var currentChunk = (int)Math.Min(chunkSize, remainingBytes);

                            // Write the actual data
                            await outputStream.WriteAsync(buffer, 0, currentChunk, cancellationToken.GetValueOrDefault());
                            await outputStream.FlushAsync(cancellationToken.GetValueOrDefault());

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