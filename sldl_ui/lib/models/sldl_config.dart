/// All configuration options for slsk-batchdl (sldl).
/// Field names map to CLI argument names.
class SldlConfig {
  // --- Soulseek Credentials ---
  String? username;
  String? password;
  int? listenPort;
  int? connectTimeout;
  String? userDescription;
  bool? useRandomLogin;

  // --- General ---
  String? path;
  String? nameFormat;
  String? profile;
  int? concurrentDownloads;
  bool? writePlaylist;
  String? playlistPath;
  bool? noIncompleteExt;
  bool? noSkipExisting;
  bool? noWriteIndex;
  String? indexPath;
  String? skipMusicDir;
  bool? skipNotFound;
  String? onComplete;
  int? maxTracks;
  int? offset;
  bool? reverse;
  bool? skipCheckCond;
  bool? skipCheckPrefCond;

  // --- Search ---
  bool? fastSearch;
  bool? removeFt;
  String? regex;
  bool? artistMaybeWrong;
  bool? desperate;
  int? failsToDownrank;
  int? failsToIgnore;
  bool? ytDlp;
  String? ytDlpArgument;
  int? searchTimeout;
  int? maxStaleTime;
  int? searchesPerTime;
  int? searchesRenewTime;

  // --- Spotify ---
  String? spotifyId;
  String? spotifySecret;
  String? spotifyToken;
  String? spotifyRefresh;
  bool? removeFromSource;

  // --- YouTube ---
  String? youtubeKey;
  bool? getDeleted;
  bool? deletedOnly;

  // --- CSV ---
  String? artistCol;
  String? titleCol;
  String? albumCol;
  String? lengthCol;
  String? ytIdCol;
  String? ytDescCol;
  String? trackCountCol;
  String? timeFormat;
  bool? ytParse;

  // --- File Conditions (Required) ---
  String? format;
  int? lengthTol;
  int? minBitrate;
  int? maxBitrate;
  int? minSamplerate;
  int? maxSamplerate;
  int? minBitdepth;
  int? maxBitdepth;
  bool? strictTitle;
  bool? strictArtist;
  bool? strictAlbum;
  String? bannedUsers;
  bool? strictConditions;

  // --- File Conditions (Preferred) ---
  String? prefFormat;
  int? prefLengthTol;
  int? prefMinBitrate;
  int? prefMaxBitrate;
  int? prefMinSamplerate;
  int? prefMaxSamplerate;
  int? prefMinBitdepth;
  int? prefMaxBitdepth;
  String? prefBannedUsers;

  // --- Album ---
  bool? album;
  bool? interactive;
  String? albumTrackCount;
  String? albumArt;
  bool? albumArtOnly;
  bool? noBrowseFolder;
  String? failedAlbumPath;
  bool? albumParallelSearch;
  int? albumParallelSearchCount;

  // --- Aggregate ---
  bool? aggregate;
  int? aggregateLengthTol;
  int? minSharesAggregate;
  bool? relaxFiltering;

  // --- Printing & Debug ---
  bool? verbose;
  String? logFile;
  bool? noProgress;
  String? printOption;
  String? mockFilesDir;

  // --- Profiles (section name -> key/value map) ---
  Map<String, Map<String, String>> profiles;

  SldlConfig({this.profiles = const {}});

  SldlConfig copy() {
    return SldlConfig(profiles: Map.from(profiles.map((k, v) => MapEntry(k, Map.from(v)))))
      ..username = username
      ..password = password
      ..listenPort = listenPort
      ..connectTimeout = connectTimeout
      ..userDescription = userDescription
      ..useRandomLogin = useRandomLogin
      ..path = path
      ..nameFormat = nameFormat
      ..profile = profile
      ..concurrentDownloads = concurrentDownloads
      ..writePlaylist = writePlaylist
      ..playlistPath = playlistPath
      ..noIncompleteExt = noIncompleteExt
      ..noSkipExisting = noSkipExisting
      ..noWriteIndex = noWriteIndex
      ..indexPath = indexPath
      ..skipMusicDir = skipMusicDir
      ..skipNotFound = skipNotFound
      ..onComplete = onComplete
      ..maxTracks = maxTracks
      ..offset = offset
      ..reverse = reverse
      ..skipCheckCond = skipCheckCond
      ..skipCheckPrefCond = skipCheckPrefCond
      ..fastSearch = fastSearch
      ..removeFt = removeFt
      ..regex = regex
      ..artistMaybeWrong = artistMaybeWrong
      ..desperate = desperate
      ..failsToDownrank = failsToDownrank
      ..failsToIgnore = failsToIgnore
      ..ytDlp = ytDlp
      ..ytDlpArgument = ytDlpArgument
      ..searchTimeout = searchTimeout
      ..maxStaleTime = maxStaleTime
      ..searchesPerTime = searchesPerTime
      ..searchesRenewTime = searchesRenewTime
      ..spotifyId = spotifyId
      ..spotifySecret = spotifySecret
      ..spotifyToken = spotifyToken
      ..spotifyRefresh = spotifyRefresh
      ..removeFromSource = removeFromSource
      ..youtubeKey = youtubeKey
      ..getDeleted = getDeleted
      ..deletedOnly = deletedOnly
      ..artistCol = artistCol
      ..titleCol = titleCol
      ..albumCol = albumCol
      ..lengthCol = lengthCol
      ..ytIdCol = ytIdCol
      ..ytDescCol = ytDescCol
      ..trackCountCol = trackCountCol
      ..timeFormat = timeFormat
      ..ytParse = ytParse
      ..format = format
      ..lengthTol = lengthTol
      ..minBitrate = minBitrate
      ..maxBitrate = maxBitrate
      ..minSamplerate = minSamplerate
      ..maxSamplerate = maxSamplerate
      ..minBitdepth = minBitdepth
      ..maxBitdepth = maxBitdepth
      ..strictTitle = strictTitle
      ..strictArtist = strictArtist
      ..strictAlbum = strictAlbum
      ..bannedUsers = bannedUsers
      ..strictConditions = strictConditions
      ..prefFormat = prefFormat
      ..prefLengthTol = prefLengthTol
      ..prefMinBitrate = prefMinBitrate
      ..prefMaxBitrate = prefMaxBitrate
      ..prefMinSamplerate = prefMinSamplerate
      ..prefMaxSamplerate = prefMaxSamplerate
      ..prefMinBitdepth = prefMinBitdepth
      ..prefMaxBitdepth = prefMaxBitdepth
      ..prefBannedUsers = prefBannedUsers
      ..album = album
      ..interactive = interactive
      ..albumTrackCount = albumTrackCount
      ..albumArt = albumArt
      ..albumArtOnly = albumArtOnly
      ..noBrowseFolder = noBrowseFolder
      ..failedAlbumPath = failedAlbumPath
      ..albumParallelSearch = albumParallelSearch
      ..albumParallelSearchCount = albumParallelSearchCount
      ..aggregate = aggregate
      ..aggregateLengthTol = aggregateLengthTol
      ..minSharesAggregate = minSharesAggregate
      ..relaxFiltering = relaxFiltering
      ..verbose = verbose
      ..logFile = logFile
      ..noProgress = noProgress
      ..printOption = printOption
      ..mockFilesDir = mockFilesDir;
  }

  bool get hasCredentials =>
      username != null && username!.isNotEmpty && password != null && password!.isNotEmpty;

  /// Build the CLI argument list for a download job.
  List<String> toArgs({
    String? input,
    String? inputType,
    bool? albumMode,
    bool? aggregateMode,
    bool? interactiveMode,
    String? extraNameFormat,
  }) {
    final args = <String>[];

    if (input != null && input.isNotEmpty) args.add(input);
    if (inputType != null && inputType.isNotEmpty) {
      args.addAll(['--input-type', inputType]);
    }

    // Force no-progress for clean UI output parsing
    args.add('--no-progress');

    _addString(args, '--user', username);
    _addString(args, '--pass', password);
    _addString(args, '-p', path);
    _addString(args, '--name-format', extraNameFormat ?? nameFormat);
    _addString(args, '--profile', profile);
    _addInt(args, '--concurrent-downloads', concurrentDownloads);
    _addFlag(args, '--write-playlist', writePlaylist);
    _addString(args, '--playlist-path', playlistPath);
    _addFlag(args, '--no-incomplete-ext', noIncompleteExt);
    _addFlag(args, '--no-skip-existing', noSkipExisting);
    _addFlag(args, '--no-write-index', noWriteIndex);
    _addString(args, '--index-path', indexPath);
    _addString(args, '--skip-music-dir', skipMusicDir);
    _addFlag(args, '--skip-not-found', skipNotFound);
    _addInt(args, '--listen-port', listenPort);
    _addInt(args, '--connect-timeout', connectTimeout);
    _addString(args, '--user-description', userDescription);
    _addString(args, '--on-complete', onComplete);
    _addInt(args, '-n', maxTracks);
    _addInt(args, '-o', offset);
    _addFlag(args, '-r', reverse);
    _addFlag(args, '--skip-check-cond', skipCheckCond);
    _addFlag(args, '--skip-check-pref-cond', skipCheckPrefCond);

    // Search
    _addFlag(args, '--fast-search', fastSearch);
    _addFlag(args, '--remove-ft', removeFt);
    _addString(args, '--regex', regex);
    _addFlag(args, '--artist-maybe-wrong', artistMaybeWrong);
    _addFlag(args, '-d', desperate);
    _addInt(args, '--fails-to-downrank', failsToDownrank);
    _addInt(args, '--fails-to-ignore', failsToIgnore);
    _addFlag(args, '--yt-dlp', ytDlp);
    _addString(args, '--yt-dlp-argument', ytDlpArgument);
    _addInt(args, '--search-timeout', searchTimeout);
    _addInt(args, '--max-stale-time', maxStaleTime);
    _addInt(args, '--searches-per-time', searchesPerTime);
    _addInt(args, '--searches-renew-time', searchesRenewTime);

    // Spotify
    _addString(args, '--spotify-id', spotifyId);
    _addString(args, '--spotify-secret', spotifySecret);
    _addString(args, '--spotify-token', spotifyToken);
    _addString(args, '--spotify-refresh', spotifyRefresh);
    _addFlag(args, '--remove-from-source', removeFromSource);

    // YouTube
    _addString(args, '--youtube-key', youtubeKey);
    _addFlag(args, '--get-deleted', getDeleted);
    _addFlag(args, '--deleted-only', deletedOnly);

    // CSV
    _addString(args, '--artist-col', artistCol);
    _addString(args, '--title-col', titleCol);
    _addString(args, '--album-col', albumCol);
    _addString(args, '--length-col', lengthCol);
    _addString(args, '--yt-id-col', ytIdCol);
    _addString(args, '--yt-desc-col', ytDescCol);
    _addString(args, '--album-track-count-col', trackCountCol);
    _addString(args, '--time-format', timeFormat);
    _addFlag(args, '--yt-parse', ytParse);

    // Required conditions
    _addString(args, '--format', format);
    _addInt(args, '--length-tol', lengthTol);
    _addInt(args, '--min-bitrate', minBitrate);
    _addInt(args, '--max-bitrate', maxBitrate);
    _addInt(args, '--min-samplerate', minSamplerate);
    _addInt(args, '--max-samplerate', maxSamplerate);
    _addInt(args, '--min-bitdepth', minBitdepth);
    _addInt(args, '--max-bitdepth', maxBitdepth);
    _addFlag(args, '--strict-title', strictTitle);
    _addFlag(args, '--strict-artist', strictArtist);
    _addFlag(args, '--strict-album', strictAlbum);
    _addString(args, '--banned-users', bannedUsers);
    _addFlag(args, '--strict-conditions', strictConditions);

    // Preferred conditions
    _addString(args, '--pref-format', prefFormat);
    _addInt(args, '--pref-length-tol', prefLengthTol);
    _addInt(args, '--pref-min-bitrate', prefMinBitrate);
    _addInt(args, '--pref-max-bitrate', prefMaxBitrate);
    _addInt(args, '--pref-min-samplerate', prefMinSamplerate);
    _addInt(args, '--pref-max-samplerate', prefMaxSamplerate);
    _addInt(args, '--pref-min-bitdepth', prefMinBitdepth);
    _addInt(args, '--pref-max-bitdepth', prefMaxBitdepth);
    _addString(args, '--pref-banned-users', prefBannedUsers);

    // Album
    final isAlbum = albumMode ?? album ?? false;
    if (isAlbum) args.add('-a');
    final isInteractive = interactiveMode ?? interactive ?? false;
    if (isInteractive) args.add('-t');
    _addString(args, '--album-track-count', albumTrackCount);
    if (albumArt != null && albumArt!.isNotEmpty && albumArt != 'default') {
      args.addAll(['--album-art', albumArt!]);
    }
    _addFlag(args, '--album-art-only', albumArtOnly);
    _addFlag(args, '--no-browse-folder', noBrowseFolder);
    _addString(args, '--failed-album-path', failedAlbumPath);
    _addFlag(args, '--album-parallel-search', albumParallelSearch);
    _addInt(args, '--album-parallel-search-count', albumParallelSearchCount);

    // Aggregate
    final isAggregate = aggregateMode ?? aggregate ?? false;
    if (isAggregate) args.add('-g');
    _addInt(args, '--aggregate-length-tol', aggregateLengthTol);
    _addInt(args, '--min-shares-aggregate', minSharesAggregate);
    _addFlag(args, '--relax-filtering', relaxFiltering);

    // Debug
    _addFlag(args, '-v', verbose);
    _addString(args, '--log-file', logFile);
    _addString(args, '--print', printOption);
    _addString(args, '--mock-files-dir', mockFilesDir);

    return args;
  }

  void _addString(List<String> args, String flag, String? value) {
    if (value != null && value.isNotEmpty) {
      args.addAll([flag, value]);
    }
  }

  void _addInt(List<String> args, String flag, int? value) {
    if (value != null) {
      args.addAll([flag, value.toString()]);
    }
  }

  void _addFlag(List<String> args, String flag, bool? value) {
    if (value == true) {
      args.add(flag);
    }
  }

  /// Convert config to a map of key -> value for writing to sldl.conf.
  /// Only non-null, non-empty values are included.
  Map<String, String> toConfigMap() {
    final map = <String, String>{};

    void add(String key, dynamic value) {
      if (value == null) return;
      if (value is String && value.isEmpty) return;
      if (value is bool) {
        if (value) map[key] = 'true';
      } else {
        map[key] = value.toString();
      }
    }

    add('username', username);
    add('password', password);
    add('path', path);
    add('name-format', nameFormat);
    add('profile', profile);
    add('concurrent-downloads', concurrentDownloads);
    add('write-playlist', writePlaylist);
    add('playlist-path', playlistPath);
    add('no-incomplete-ext', noIncompleteExt);
    add('no-skip-existing', noSkipExisting);
    add('no-write-index', noWriteIndex);
    add('index-path', indexPath);
    add('skip-music-dir', skipMusicDir);
    add('skip-not-found', skipNotFound);
    add('listen-port', listenPort);
    add('connect-timeout', connectTimeout);
    add('user-description', userDescription);
    add('on-complete', onComplete);
    add('fast-search', fastSearch);
    add('remove-ft', removeFt);
    add('regex', regex);
    add('artist-maybe-wrong', artistMaybeWrong);
    add('desperate', desperate);
    add('fails-to-downrank', failsToDownrank);
    add('fails-to-ignore', failsToIgnore);
    add('yt-dlp', ytDlp);
    add('yt-dlp-argument', ytDlpArgument);
    add('search-timeout', searchTimeout);
    add('max-stale-time', maxStaleTime);
    add('searches-per-time', searchesPerTime);
    add('searches-renew-time', searchesRenewTime);
    add('spotify-id', spotifyId);
    add('spotify-secret', spotifySecret);
    add('spotify-token', spotifyToken);
    add('spotify-refresh', spotifyRefresh);
    add('remove-from-source', removeFromSource);
    add('youtube-key', youtubeKey);
    add('get-deleted', getDeleted);
    add('deleted-only', deletedOnly);
    add('artist-col', artistCol);
    add('title-col', titleCol);
    add('album-col', albumCol);
    add('length-col', lengthCol);
    add('yt-id-col', ytIdCol);
    add('yt-desc-col', ytDescCol);
    add('album-track-count-col', trackCountCol);
    add('time-format', timeFormat);
    add('yt-parse', ytParse);
    add('format', format);
    add('length-tol', lengthTol);
    add('min-bitrate', minBitrate);
    add('max-bitrate', maxBitrate);
    add('min-samplerate', minSamplerate);
    add('max-samplerate', maxSamplerate);
    add('min-bitdepth', minBitdepth);
    add('max-bitdepth', maxBitdepth);
    add('strict-title', strictTitle);
    add('strict-artist', strictArtist);
    add('strict-album', strictAlbum);
    add('banned-users', bannedUsers);
    add('strict-conditions', strictConditions);
    add('pref-format', prefFormat);
    add('pref-length-tol', prefLengthTol);
    add('pref-min-bitrate', prefMinBitrate);
    add('pref-max-bitrate', prefMaxBitrate);
    add('pref-min-samplerate', prefMinSamplerate);
    add('pref-max-samplerate', prefMaxSamplerate);
    add('pref-min-bitdepth', prefMinBitdepth);
    add('pref-max-bitdepth', prefMaxBitdepth);
    add('pref-banned-users', prefBannedUsers);
    add('album-track-count', albumTrackCount);
    add('album-art', albumArt);
    add('album-art-only', albumArtOnly);
    add('no-browse-folder', noBrowseFolder);
    add('failed-album-path', failedAlbumPath);
    add('album-parallel-search', albumParallelSearch);
    add('album-parallel-search-count', albumParallelSearchCount);
    add('aggregate-length-tol', aggregateLengthTol);
    add('min-shares-aggregate', minSharesAggregate);
    add('relax-filtering', relaxFiltering);
    add('verbose', verbose);
    add('log-file', logFile);
    add('skip-check-cond', skipCheckCond);
    add('skip-check-pref-cond', skipCheckPrefCond);

    return map;
  }
}
