import 'dart:async';
import 'package:flutter/foundation.dart';
import '../models/download_item.dart';
import '../models/sldl_config.dart';
import '../services/app_config_service.dart';
import '../services/config_service.dart';
import '../services/process_service.dart';

enum ConnectionStatus { unknown, connecting, connected, failed }

class AppProvider extends ChangeNotifier {
  final AppConfigService appConfigService;
  final ConfigService configService;
  final ProcessService _processService = ProcessService();

  SldlConfig config;
  String? sldlExecutablePath;
  ConnectionStatus connectionStatus = ConnectionStatus.unknown;

  final List<DownloadItem> _queue = [];
  DownloadItem? _currentItem;
  bool _isProcessingQueue = false;
  StreamSubscription<ProcessEvent>? _currentSub;

  // Callbacks for dialogs
  void Function()? onLoginRequired;
  void Function()? onConnectionLost;

  AppProvider({
    required this.appConfigService,
    required this.configService,
    required SldlConfig initialConfig,
  }) : config = initialConfig;

  List<DownloadItem> get queue => List.unmodifiable(_queue);
  DownloadItem? get currentItem => _currentItem;
  bool get isRunning => _processService.isRunning;
  bool get hasCredentials => config.hasCredentials;

  // ── Initialization ────────────────────────────────────────────────────────

  Future<void> initialize() async {
    sldlExecutablePath = await appConfigService.autoDetectSldlPath();
    notifyListeners();
  }

  // ── Config management ─────────────────────────────────────────────────────

  Future<void> reloadConfig() async {
    config = await configService.loadConfig();
    notifyListeners();
  }

  Future<void> saveConfig(SldlConfig newConfig) async {
    config = newConfig;
    await configService.saveConfig(config);
    notifyListeners();
  }

  Future<void> saveCredentials(String username, String password) async {
    config.username = username;
    config.password = password;
    await configService.saveConfig(config);
    notifyListeners();
  }

  void updateSldlPath(String path) {
    sldlExecutablePath = path;
    appConfigService.setSldlPath(path);
    notifyListeners();
  }

  // ── Download queue management ─────────────────────────────────────────────

  void enqueue(DownloadItem item) {
    _queue.add(item);
    notifyListeners();
    _processNextIfIdle();
  }

  void removeFromQueue(String id) {
    final idx = _queue.indexWhere((i) => i.id == id);
    if (idx < 0) return;
    final item = _queue[idx];
    if (item.status == DownloadStatus.running) {
      cancelCurrent();
    } else {
      _queue.removeAt(idx);
      notifyListeners();
    }
  }

  void cancelCurrent() {
    _processService.cancel();
    _currentItem?.status = DownloadStatus.cancelled;
    _currentItem?.completedAt = DateTime.now();
    _isProcessingQueue = false;
    _currentSub?.cancel();
    _currentSub = null;
    _currentItem = null;
    notifyListeners();
    _processNextIfIdle();
  }

  void clearCompleted() {
    _queue.removeWhere((i) =>
        i.status == DownloadStatus.succeeded ||
        i.status == DownloadStatus.failed ||
        i.status == DownloadStatus.cancelled);
    notifyListeners();
  }

  void _processNextIfIdle() {
    if (_isProcessingQueue) return;
    final next = _queue.firstWhereOrNull((i) => i.status == DownloadStatus.queued);
    if (next == null) return;
    _startDownload(next);
  }

  void _startDownload(DownloadItem item) {
    final exePath = sldlExecutablePath;
    if (exePath == null || exePath.isEmpty) {
      item.status = DownloadStatus.failed;
      item.addLog('Error: sldl executable path not configured. Go to Settings to set it.');
      notifyListeners();
      return;
    }

    _isProcessingQueue = true;
    _currentItem = item;
    item.status = DownloadStatus.running;
    item.startedAt = DateTime.now();
    notifyListeners();

    final stream = _processService.run(exePath, item, config);
    _currentSub = stream.listen(
      (event) => _handleProcessEvent(item, event),
      onDone: () {
        _isProcessingQueue = false;
        _currentItem = null;
        _currentSub = null;
        if (item.status == DownloadStatus.running) {
          // Process ended without explicit completion — treat as done
          item.status = item.failedCount > 0 && item.succeededCount == 0
              ? DownloadStatus.failed
              : DownloadStatus.succeeded;
          item.completedAt = DateTime.now();
        }
        notifyListeners();
        _processNextIfIdle();
      },
      onError: (e) {
        item.addLog('Error: $e');
        item.status = DownloadStatus.failed;
        item.completedAt = DateTime.now();
        _isProcessingQueue = false;
        _currentItem = null;
        _currentSub = null;
        notifyListeners();
        _processNextIfIdle();
      },
    );
  }

  void _handleProcessEvent(DownloadItem item, ProcessEvent event) {
    switch (event.type) {
      case ProcessEventType.log:
        if (event.message != null) item.addLog(event.message!);
        break;

      case ProcessEventType.loginConnecting:
        connectionStatus = ConnectionStatus.connecting;
        break;

      case ProcessEventType.loginSuccess:
        connectionStatus = ConnectionStatus.connected;
        appConfigService.setConnected(true);
        break;

      case ProcessEventType.loginFailed:
        connectionStatus = ConnectionStatus.failed;
        appConfigService.setConnected(false);
        item.addLog(event.message ?? 'Login failed');
        // Trigger login re-prompt
        onConnectionLost?.call();
        break;

      case ProcessEventType.jobStart:
        if (event.trackTotal != null) {
          item.totalCount = event.trackTotal!;
        }
        break;

      case ProcessEventType.trackStart:
        if (event.trackIndex != null && event.trackTotal != null && event.trackTotal! > 0) {
          item.progress = (event.trackIndex! - 1) / event.trackTotal!;
        }
        break;

      case ProcessEventType.trackSuccess:
        item.succeededCount++;
        if (event.trackIndex != null && event.trackTotal != null && event.trackTotal! > 0) {
          item.progress = event.trackIndex! / event.trackTotal!;
        }
        break;

      case ProcessEventType.trackFailed:
        item.failedCount++;
        break;

      case ProcessEventType.trackSkipped:
        // Skipped tracks count toward progress
        if (event.trackIndex != null && event.trackTotal != null && event.trackTotal! > 0) {
          item.progress = event.trackIndex! / event.trackTotal!;
        }
        break;

      case ProcessEventType.jobComplete:
        if (event.succeededCount != null) item.succeededCount = event.succeededCount!;
        if (event.failedCount != null) item.failedCount = event.failedCount!;
        item.progress = 1.0;
        item.status = item.failedCount > 0 && item.succeededCount == 0
            ? DownloadStatus.failed
            : DownloadStatus.succeeded;
        item.completedAt = DateTime.now();
        break;

      case ProcessEventType.jobFailed:
        item.status = DownloadStatus.failed;
        item.completedAt = DateTime.now();
        if (event.message != null) item.addLog(event.message!);
        break;
    }

    notifyListeners();
  }
}

extension _IterableExt<T> on Iterable<T> {
  T? firstWhereOrNull(bool Function(T) test) {
    for (final element in this) {
      if (test(element)) return element;
    }
    return null;
  }
}
