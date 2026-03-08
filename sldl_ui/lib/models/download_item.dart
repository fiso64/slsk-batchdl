import 'package:uuid/uuid.dart';

enum DownloadStatus { queued, running, succeeded, failed, cancelled }

enum InputType {
  auto,
  string,
  csv,
  youtube,
  spotify,
  bandcamp,
  musicbrainz,
  soulseek,
  list,
}

extension InputTypeExt on InputType {
  String get label {
    switch (this) {
      case InputType.auto:
        return 'Auto-detect';
      case InputType.string:
        return 'Search String';
      case InputType.csv:
        return 'CSV File';
      case InputType.youtube:
        return 'YouTube';
      case InputType.spotify:
        return 'Spotify';
      case InputType.bandcamp:
        return 'Bandcamp';
      case InputType.musicbrainz:
        return 'MusicBrainz';
      case InputType.soulseek:
        return 'Soulseek Link';
      case InputType.list:
        return 'List File';
    }
  }

  String get cliValue {
    switch (this) {
      case InputType.auto:
        return '';
      case InputType.string:
        return 'string';
      case InputType.csv:
        return 'csv';
      case InputType.youtube:
        return 'youtube';
      case InputType.spotify:
        return 'spotify';
      case InputType.bandcamp:
        return 'bandcamp';
      case InputType.musicbrainz:
        return 'musicbrainz';
      case InputType.soulseek:
        return 'soulseek';
      case InputType.list:
        return 'list';
    }
  }
}

class TrackStat {
  final String name;
  final DownloadStatus status;
  final String? message;

  const TrackStat({required this.name, required this.status, this.message});
}

class DownloadItem {
  final String id;
  String input;
  InputType inputType;
  bool albumMode;
  bool aggregateMode;
  bool interactiveMode;
  String nameFormat;
  DownloadStatus status;
  double progress; // 0.0 to 1.0
  int succeededCount;
  int failedCount;
  int totalCount;
  List<String> logLines;
  List<TrackStat> tracks;
  DateTime createdAt;
  DateTime? startedAt;
  DateTime? completedAt;

  DownloadItem({
    String? id,
    required this.input,
    this.inputType = InputType.auto,
    this.albumMode = false,
    this.aggregateMode = false,
    this.interactiveMode = false,
    this.nameFormat = '',
    this.status = DownloadStatus.queued,
    this.progress = 0.0,
    this.succeededCount = 0,
    this.failedCount = 0,
    this.totalCount = 0,
    List<String>? logLines,
    List<TrackStat>? tracks,
    DateTime? createdAt,
    this.startedAt,
    this.completedAt,
  })  : id = id ?? const Uuid().v4(),
        logLines = logLines ?? [],
        tracks = tracks ?? [],
        createdAt = createdAt ?? DateTime.now();

  void addLog(String line) {
    logLines.add(line);
    if (logLines.length > 500) {
      logLines.removeAt(0);
    }
  }

  String get statusLabel {
    switch (status) {
      case DownloadStatus.queued:
        return 'Queued';
      case DownloadStatus.running:
        return 'Running';
      case DownloadStatus.succeeded:
        return 'Completed';
      case DownloadStatus.failed:
        return 'Failed';
      case DownloadStatus.cancelled:
        return 'Cancelled';
    }
  }

  /// Human-readable display name for the download item.
  String get displayName {
    final trimmed = input.trim();
    if (trimmed.length > 60) {
      return '${trimmed.substring(0, 57)}...';
    }
    return trimmed;
  }
}
