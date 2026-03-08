import 'dart:async';
import 'dart:convert';
import 'dart:io';
import '../models/download_item.dart';
import '../models/sldl_config.dart';

enum ProcessEventType {
  log,
  loginConnecting,
  loginSuccess,
  loginFailed,
  jobStart,
  trackStart,
  trackProgress,
  trackSuccess,
  trackFailed,
  trackSkipped,
  jobComplete,
  jobFailed,
}

class ProcessEvent {
  final ProcessEventType type;
  final String? message;
  final int? trackIndex;
  final int? trackTotal;
  final int? succeededCount;
  final int? failedCount;
  final double? progress;

  const ProcessEvent({
    required this.type,
    this.message,
    this.trackIndex,
    this.trackTotal,
    this.succeededCount,
    this.failedCount,
    this.progress,
  });
}

class ProcessService {
  Process? _currentProcess;
  bool _isCancelling = false;

  bool get isRunning => _currentProcess != null;

  /// Start sldl for a download item. Returns a stream of [ProcessEvent]s.
  Stream<ProcessEvent> run(
    String executablePath,
    DownloadItem item,
    SldlConfig config,
  ) {
    final controller = StreamController<ProcessEvent>();
    _runAsync(executablePath, item, config, controller);
    return controller.stream;
  }

  Future<void> _runAsync(
    String executablePath,
    DownloadItem item,
    SldlConfig config,
    StreamController<ProcessEvent> controller,
  ) async {
    _isCancelling = false;

    final args = config.toArgs(
      input: item.input,
      inputType: item.inputType != InputType.auto ? item.inputType.cliValue : null,
      albumMode: item.albumMode,
      aggregateMode: item.aggregateMode,
      interactiveMode: item.interactiveMode,
      extraNameFormat: item.nameFormat.isNotEmpty ? item.nameFormat : null,
    );

    try {
      _currentProcess = await Process.start(
        executablePath,
        args,
        runInShell: Platform.isWindows,
      );

      controller.add(ProcessEvent(
        type: ProcessEventType.log,
        message: 'Starting: $executablePath ${args.join(' ')}',
      ));

      // Parse stdout
      _currentProcess!.stdout
          .transform(utf8.decoder)
          .transform(const LineSplitter())
          .listen(
        (line) {
          if (controller.isClosed) return;
          final event = _parseLine(line);
          if (event != null) {
            controller.add(event);
          }
          controller.add(ProcessEvent(type: ProcessEventType.log, message: line));
        },
        onDone: () {},
        onError: (_) {},
      );

      // Parse stderr (sldl writes most output to stdout but some errors to stderr)
      _currentProcess!.stderr
          .transform(utf8.decoder)
          .transform(const LineSplitter())
          .listen(
        (line) {
          if (controller.isClosed) return;
          final event = _parseLine(line);
          if (event != null) {
            controller.add(event);
          }
          controller.add(ProcessEvent(type: ProcessEventType.log, message: '[stderr] $line'));
        },
        onDone: () {},
        onError: (_) {},
      );

      final exitCode = await _currentProcess!.exitCode;
      _currentProcess = null;

      if (_isCancelling) {
        if (!controller.isClosed) controller.close();
        return;
      }

      if (exitCode == 0) {
        controller.add(ProcessEvent(type: ProcessEventType.jobComplete, message: 'Process exited successfully'));
      } else {
        controller.add(ProcessEvent(
          type: ProcessEventType.jobFailed,
          message: 'Process exited with code $exitCode',
        ));
      }
    } catch (e) {
      _currentProcess = null;
      controller.add(ProcessEvent(
        type: ProcessEventType.jobFailed,
        message: 'Failed to start sldl: $e',
      ));
    } finally {
      if (!controller.isClosed) {
        controller.close();
      }
    }
  }

  void cancel() {
    _isCancelling = true;
    if (_currentProcess != null) {
      if (Platform.isWindows) {
        Process.run('taskkill', ['/F', '/T', '/PID', _currentProcess!.pid.toString()]);
      } else {
        _currentProcess!.kill(ProcessSignal.sigterm);
      }
      _currentProcess = null;
    }
  }

  ProcessEvent? _parseLine(String line) {
    final lower = line.toLowerCase();

    // Login / connection events
    if (_matchesAny(lower, ['logging in', 'connecting to soulseek'])) {
      return ProcessEvent(type: ProcessEventType.loginConnecting, message: line);
    }
    if (_matchesAny(lower, ['connected to soulseek', 'logged in', 'connected.'])) {
      return ProcessEvent(type: ProcessEventType.loginSuccess, message: line);
    }
    if (_matchesAny(lower, [
      'login failed',
      'failed to ensure soulseek connection',
      'soulseek login failed',
      'no soulseek username',
      'invalid credentials',
    ])) {
      return ProcessEvent(type: ProcessEventType.loginFailed, message: line);
    }

    // Download summary
    final downloadingMatch = RegExp(
      r'downloading\s+(\d+)\s+track',
      caseSensitive: false,
    ).firstMatch(line);
    if (downloadingMatch != null) {
      final total = int.tryParse(downloadingMatch.group(1) ?? '');
      return ProcessEvent(
        type: ProcessEventType.jobStart,
        message: line,
        trackTotal: total,
      );
    }

    // Individual track events — match patterns like "[1/5] Downloading: ..."
    final trackMatch = RegExp(
      r'\[(\d+)/(\d+)\]\s+(Downloading|Succeeded|Failed|Skipping|Queued):?\s*(.*)',
      caseSensitive: false,
    ).firstMatch(line);
    if (trackMatch != null) {
      final idx = int.tryParse(trackMatch.group(1) ?? '');
      final total = int.tryParse(trackMatch.group(2) ?? '');
      final action = trackMatch.group(3)?.toLowerCase() ?? '';
      final name = trackMatch.group(4) ?? '';

      if (action.contains('downloading') || action.contains('queued')) {
        return ProcessEvent(
          type: ProcessEventType.trackStart,
          message: name,
          trackIndex: idx,
          trackTotal: total,
        );
      } else if (action.contains('succeeded')) {
        return ProcessEvent(
          type: ProcessEventType.trackSuccess,
          message: name,
          trackIndex: idx,
          trackTotal: total,
        );
      } else if (action.contains('failed')) {
        return ProcessEvent(
          type: ProcessEventType.trackFailed,
          message: name,
          trackIndex: idx,
          trackTotal: total,
        );
      } else if (action.contains('skip')) {
        return ProcessEvent(
          type: ProcessEventType.trackSkipped,
          message: name,
          trackIndex: idx,
          trackTotal: total,
        );
      }
    }

    // Standalone Succeeded/Failed/Skipping lines without [n/N] prefix
    if (RegExp(r'^succeeded:', caseSensitive: false).hasMatch(line.trim())) {
      return ProcessEvent(type: ProcessEventType.trackSuccess, message: line);
    }
    if (RegExp(r'^failed:', caseSensitive: false).hasMatch(line.trim())) {
      return ProcessEvent(type: ProcessEventType.trackFailed, message: line);
    }
    if (RegExp(r'^skipping:', caseSensitive: false).hasMatch(line.trim())) {
      return ProcessEvent(type: ProcessEventType.trackSkipped, message: line);
    }

    // Completion summary
    final completeMatch = RegExp(
      r'completed:\s*(\d+)\s+succeeded,\s*(\d+)\s+failed',
      caseSensitive: false,
    ).firstMatch(line);
    if (completeMatch != null) {
      final succeeded = int.tryParse(completeMatch.group(1) ?? '');
      final failed = int.tryParse(completeMatch.group(2) ?? '');
      return ProcessEvent(
        type: ProcessEventType.jobComplete,
        message: line,
        succeededCount: succeeded,
        failedCount: failed,
      );
    }

    return null;
  }

  bool _matchesAny(String text, List<String> patterns) {
    return patterns.any((p) => text.contains(p));
  }
}
