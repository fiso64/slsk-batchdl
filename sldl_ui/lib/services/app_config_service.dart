import 'dart:convert';
import 'dart:io';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Stores UI-specific settings that are separate from sldl.conf.
/// This includes the sldl executable path, first-run status, and
/// platform credentials such as Spotify OAuth tokens.
class AppConfigService {
  static const _keyFirstRun = 'first_run';
  static const _keySldlPath = 'sldl_path';
  static const _keySetupDoneSpotify = 'setup_done_spotify';
  static const _keySetupDoneYoutube = 'setup_done_youtube';
  static const _keyLastConnected = 'last_connected';
  static const _keyThemeMode = 'theme_mode';

  late SharedPreferences _prefs;
  late String _appConfigDir;

  Future<void> init() async {
    _prefs = await SharedPreferences.getInstance();
    final appDir = await getApplicationSupportDirectory();
    _appConfigDir = appDir.path;
  }

  // ---- First run ----

  bool get isFirstRun => _prefs.getBool(_keyFirstRun) ?? true;

  Future<void> setFirstRunDone() async {
    await _prefs.setBool(_keyFirstRun, false);
  }

  // ---- sldl executable path ----

  String? get sldlPath => _prefs.getString(_keySldlPath);

  Future<void> setSldlPath(String path) async {
    await _prefs.setString(_keySldlPath, path);
  }

  /// Auto-detect sldl executable. Looks in:
  /// 1. Stored path in preferences
  /// 2. Same directory as this app
  /// 3. System PATH
  Future<String?> autoDetectSldlPath() async {
    // 1. Check stored preference
    final stored = sldlPath;
    if (stored != null && stored.isNotEmpty && await File(stored).exists()) {
      return stored;
    }

    // 2. Same directory as the app executable
    final exeDir = File(Platform.resolvedExecutable).parent.path;
    final execName = Platform.isWindows ? 'sldl.exe' : 'sldl';
    final localPath = '$exeDir/$execName';
    if (await File(localPath).exists()) {
      await setSldlPath(localPath);
      return localPath;
    }

    // 3. Try finding in PATH via `which`/`where`
    try {
      final result = await Process.run(
        Platform.isWindows ? 'where' : 'which',
        [Platform.isWindows ? 'sldl.exe' : 'sldl'],
      );
      if (result.exitCode == 0) {
        final found = (result.stdout as String).trim().split('\n').first.trim();
        if (found.isNotEmpty && await File(found).exists()) {
          await setSldlPath(found);
          return found;
        }
      }
    } catch (_) {}

    return null;
  }

  // ---- Setup status ----

  bool get spotifySetupDone => _prefs.getBool(_keySetupDoneSpotify) ?? false;
  bool get youtubeSetupDone => _prefs.getBool(_keySetupDoneYoutube) ?? false;

  Future<void> setSpotifySetupDone(bool value) async {
    await _prefs.setBool(_keySetupDoneSpotify, value);
  }

  Future<void> setYoutubeSetupDone(bool value) async {
    await _prefs.setBool(_keySetupDoneYoutube, value);
  }

  // ---- Connection tracking ----

  bool get wasLastConnected => _prefs.getBool(_keyLastConnected) ?? false;

  Future<void> setConnected(bool connected) async {
    await _prefs.setBool(_keyLastConnected, connected);
  }

  // ---- Theme ----

  String get themeMode => _prefs.getString(_keyThemeMode) ?? 'system';

  Future<void> setThemeMode(String mode) async {
    await _prefs.setString(_keyThemeMode, mode);
  }

  // ---- App config dir path ----

  String get appConfigDir => _appConfigDir;
}
