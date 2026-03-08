import 'dart:io';
import 'package:path_provider/path_provider.dart';
import '../models/sldl_config.dart';

/// Reads and writes the sldl.conf configuration file in INI format.
class ConfigService {
  static const _configFileName = 'sldl.conf';

  /// Returns the platform-default config file path for sldl.
  Future<String> getDefaultConfigPath() async {
    if (Platform.isWindows) {
      final appData = Platform.environment['APPDATA'];
      if (appData != null) {
        return '${appData}\\sldl\\$_configFileName';
      }
    } else if (Platform.isLinux || Platform.isMacOS) {
      final home = Platform.environment['HOME'] ?? '';
      final xdgConfig = Platform.environment['XDG_CONFIG_HOME'];
      if (xdgConfig != null && xdgConfig.isNotEmpty) {
        return '$xdgConfig/sldl/$_configFileName';
      }
      return '$home/.config/sldl/$_configFileName';
    }
    final appSupport = await getApplicationSupportDirectory();
    return '${appSupport.path}/$_configFileName';
  }

  /// Load the sldl config from the default path (or the provided [path]).
  Future<SldlConfig> loadConfig([String? path]) async {
    final configPath = path ?? await getDefaultConfigPath();
    final file = File(configPath);

    if (!await file.exists()) {
      return SldlConfig();
    }

    final lines = await file.readAsLines();
    return _parseConfigLines(lines);
  }

  /// Load config from a specific file path.
  Future<SldlConfig> loadConfigFromPath(String configPath) async {
    return loadConfig(configPath);
  }

  /// Save the config to the default path (or the provided [path]).
  Future<void> saveConfig(SldlConfig config, [String? path]) async {
    final configPath = path ?? await getDefaultConfigPath();
    final file = File(configPath);

    // Ensure the directory exists
    await file.parent.create(recursive: true);

    // Read existing lines to preserve comments and profile sections
    List<String> existingLines = [];
    if (await file.exists()) {
      existingLines = await file.readAsLines();
    }

    final newContent = _buildConfigContent(config, existingLines);
    await file.writeAsString(newContent);
  }

  SldlConfig _parseConfigLines(List<String> lines) {
    final config = SldlConfig();
    final profileMap = <String, Map<String, String>>{};
    String? currentProfile;
    Map<String, String>? currentProfileMap;

    for (final rawLine in lines) {
      final line = rawLine.trim();

      // Skip comments and blank lines
      if (line.isEmpty || line.startsWith('#')) continue;

      // Profile section header
      if (line.startsWith('[') && line.endsWith(']')) {
        final profileName = line.substring(1, line.length - 1).trim();
        currentProfile = profileName;
        currentProfileMap = {};
        profileMap[profileName] = currentProfileMap;
        continue;
      }

      // Key = value pair
      final eqIdx = line.indexOf('=');
      if (eqIdx < 1) continue;

      final key = line.substring(0, eqIdx).trim();
      final value = line.substring(eqIdx + 1).trim();

      if (currentProfile != null && currentProfileMap != null) {
        // Inside a profile section
        currentProfileMap[key] = value;
      } else {
        // Main config section
        _applyKeyValue(config, key, value);
      }
    }

    config.profiles = profileMap;
    return config;
  }

  void _applyKeyValue(SldlConfig config, String key, String value) {
    final v = value.trim();
    bool? parseBool() {
      final lower = v.toLowerCase();
      if (lower == 'true') return true;
      if (lower == 'false') return false;
      return null;
    }

    int? parseInt() => int.tryParse(v);

    switch (key) {
      case 'username':
        config.username = v;
        break;
      case 'password':
        config.password = v;
        break;
      case 'path':
        config.path = v;
        break;
      case 'name-format':
        config.nameFormat = v;
        break;
      case 'profile':
        config.profile = v;
        break;
      case 'concurrent-downloads':
        config.concurrentDownloads = parseInt();
        break;
      case 'write-playlist':
        config.writePlaylist = parseBool();
        break;
      case 'playlist-path':
        config.playlistPath = v;
        break;
      case 'no-incomplete-ext':
        config.noIncompleteExt = parseBool();
        break;
      case 'no-skip-existing':
        config.noSkipExisting = parseBool();
        break;
      case 'no-write-index':
        config.noWriteIndex = parseBool();
        break;
      case 'index-path':
        config.indexPath = v;
        break;
      case 'skip-music-dir':
        config.skipMusicDir = v;
        break;
      case 'skip-not-found':
        config.skipNotFound = parseBool();
        break;
      case 'listen-port':
        config.listenPort = parseInt();
        break;
      case 'connect-timeout':
        config.connectTimeout = parseInt();
        break;
      case 'user-description':
        config.userDescription = v;
        break;
      case 'on-complete':
        config.onComplete = v;
        break;
      case 'fast-search':
        config.fastSearch = parseBool();
        break;
      case 'remove-ft':
        config.removeFt = parseBool();
        break;
      case 'regex':
        config.regex = v;
        break;
      case 'artist-maybe-wrong':
        config.artistMaybeWrong = parseBool();
        break;
      case 'desperate':
        config.desperate = parseBool();
        break;
      case 'fails-to-downrank':
        config.failsToDownrank = parseInt();
        break;
      case 'fails-to-ignore':
        config.failsToIgnore = parseInt();
        break;
      case 'yt-dlp':
        config.ytDlp = parseBool();
        break;
      case 'yt-dlp-argument':
        config.ytDlpArgument = v;
        break;
      case 'search-timeout':
        config.searchTimeout = parseInt();
        break;
      case 'max-stale-time':
        config.maxStaleTime = parseInt();
        break;
      case 'searches-per-time':
        config.searchesPerTime = parseInt();
        break;
      case 'searches-renew-time':
        config.searchesRenewTime = parseInt();
        break;
      case 'spotify-id':
        config.spotifyId = v;
        break;
      case 'spotify-secret':
        config.spotifySecret = v;
        break;
      case 'spotify-token':
        config.spotifyToken = v;
        break;
      case 'spotify-refresh':
        config.spotifyRefresh = v;
        break;
      case 'remove-from-source':
        config.removeFromSource = parseBool();
        break;
      case 'youtube-key':
        config.youtubeKey = v;
        break;
      case 'get-deleted':
        config.getDeleted = parseBool();
        break;
      case 'deleted-only':
        config.deletedOnly = parseBool();
        break;
      case 'artist-col':
        config.artistCol = v;
        break;
      case 'title-col':
        config.titleCol = v;
        break;
      case 'album-col':
        config.albumCol = v;
        break;
      case 'length-col':
        config.lengthCol = v;
        break;
      case 'yt-id-col':
        config.ytIdCol = v;
        break;
      case 'yt-desc-col':
        config.ytDescCol = v;
        break;
      case 'album-track-count-col':
        config.trackCountCol = v;
        break;
      case 'time-format':
        config.timeFormat = v;
        break;
      case 'yt-parse':
        config.ytParse = parseBool();
        break;
      case 'format':
        config.format = v;
        break;
      case 'length-tol':
        config.lengthTol = parseInt();
        break;
      case 'min-bitrate':
        config.minBitrate = parseInt();
        break;
      case 'max-bitrate':
        config.maxBitrate = parseInt();
        break;
      case 'min-samplerate':
        config.minSamplerate = parseInt();
        break;
      case 'max-samplerate':
        config.maxSamplerate = parseInt();
        break;
      case 'min-bitdepth':
        config.minBitdepth = parseInt();
        break;
      case 'max-bitdepth':
        config.maxBitdepth = parseInt();
        break;
      case 'strict-title':
        config.strictTitle = parseBool();
        break;
      case 'strict-artist':
        config.strictArtist = parseBool();
        break;
      case 'strict-album':
        config.strictAlbum = parseBool();
        break;
      case 'banned-users':
        config.bannedUsers = v;
        break;
      case 'strict-conditions':
        config.strictConditions = parseBool();
        break;
      case 'pref-format':
        config.prefFormat = v;
        break;
      case 'pref-length-tol':
        config.prefLengthTol = parseInt();
        break;
      case 'pref-min-bitrate':
        config.prefMinBitrate = parseInt();
        break;
      case 'pref-max-bitrate':
        config.prefMaxBitrate = parseInt();
        break;
      case 'pref-min-samplerate':
        config.prefMinSamplerate = parseInt();
        break;
      case 'pref-max-samplerate':
        config.prefMaxSamplerate = parseInt();
        break;
      case 'pref-min-bitdepth':
        config.prefMinBitdepth = parseInt();
        break;
      case 'pref-max-bitdepth':
        config.prefMaxBitdepth = parseInt();
        break;
      case 'pref-banned-users':
        config.prefBannedUsers = v;
        break;
      case 'album-track-count':
        config.albumTrackCount = v;
        break;
      case 'album-art':
        config.albumArt = v;
        break;
      case 'album-art-only':
        config.albumArtOnly = parseBool();
        break;
      case 'no-browse-folder':
        config.noBrowseFolder = parseBool();
        break;
      case 'failed-album-path':
        config.failedAlbumPath = v;
        break;
      case 'album-parallel-search':
        config.albumParallelSearch = parseBool();
        break;
      case 'album-parallel-search-count':
        config.albumParallelSearchCount = parseInt();
        break;
      case 'aggregate-length-tol':
        config.aggregateLengthTol = parseInt();
        break;
      case 'min-shares-aggregate':
        config.minSharesAggregate = parseInt();
        break;
      case 'relax-filtering':
        config.relaxFiltering = parseBool();
        break;
      case 'verbose':
        config.verbose = parseBool();
        break;
      case 'log-file':
        config.logFile = v;
        break;
      case 'skip-check-cond':
        config.skipCheckCond = parseBool();
        break;
      case 'skip-check-pref-cond':
        config.skipCheckPrefCond = parseBool();
        break;
    }
  }

  String _buildConfigContent(SldlConfig config, List<String> existingLines) {
    final sb = StringBuffer();
    final newMap = config.toConfigMap();
    final keysWritten = <String>{};

    // Track which lines are in profile sections in existing content
    String? currentSection;
    final sectionLines = <String, List<String>>{};

    for (final line in existingLines) {
      final trimmed = line.trim();
      if (trimmed.startsWith('[') && trimmed.endsWith(']')) {
        currentSection = trimmed.substring(1, trimmed.length - 1);
        sectionLines[currentSection] = [];
      } else if (currentSection != null) {
        sectionLines[currentSection]?.add(line);
      }
    }

    // Write comments and main section key-values from existing file structure
    currentSection = null;
    for (final line in existingLines) {
      final trimmed = line.trim();

      if (trimmed.startsWith('[') && trimmed.endsWith(']')) {
        currentSection = trimmed.substring(1, trimmed.length - 1);
        break; // Stop before profile sections; we'll write them below
      }

      if (trimmed.isEmpty || trimmed.startsWith('#')) {
        sb.writeln(line);
        continue;
      }

      final eqIdx = trimmed.indexOf('=');
      if (eqIdx >= 1) {
        final key = trimmed.substring(0, eqIdx).trim();
        if (newMap.containsKey(key)) {
          // Update existing key
          sb.writeln('$key = ${newMap[key]}');
          keysWritten.add(key);
        }
        // If not in newMap, the key was cleared — skip it (remove from file)
      }
    }

    // Write any new keys that weren't in the existing file
    for (final entry in newMap.entries) {
      if (!keysWritten.contains(entry.key)) {
        sb.writeln('${entry.key} = ${entry.value}');
      }
    }

    // Write profile sections
    for (final profileEntry in config.profiles.entries) {
      sb.writeln('');
      sb.writeln('[${profileEntry.key}]');
      for (final kv in profileEntry.value.entries) {
        sb.writeln('${kv.key} = ${kv.value}');
      }
    }

    return sb.toString();
  }
}
