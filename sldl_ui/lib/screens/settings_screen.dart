import 'package:flutter/material.dart';
import 'package:file_picker/file_picker.dart';
import 'package:provider/provider.dart';
import '../models/sldl_config.dart';
import '../providers/app_provider.dart';

class SettingsScreen extends StatefulWidget {
  const SettingsScreen({super.key});

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen>
    with SingleTickerProviderStateMixin {
  late TabController _tabCtrl;
  late SldlConfig _draft;
  String _sldlPathDraft = '';
  bool _dirty = false;

  final List<(String label, IconData icon)> _tabs = [
    ('General', Icons.settings),
    ('Connection', Icons.lan),
    ('Search', Icons.search),
    ('File Conditions', Icons.filter_list),
    ('Album & Aggregate', Icons.album),
    ('Spotify', Icons.music_note),
    ('YouTube', Icons.smart_display),
    ('CSV / List', Icons.table_chart),
    ('Advanced', Icons.tune),
  ];

  @override
  void initState() {
    super.initState();
    _tabCtrl = TabController(length: _tabs.length, vsync: this);
    final provider = context.read<AppProvider>();
    _draft = provider.config.copy();
    _sldlPathDraft = provider.sldlExecutablePath ?? '';
  }

  @override
  void dispose() {
    _tabCtrl.dispose();
    super.dispose();
  }

  void _markDirty() => setState(() => _dirty = true);

  Future<void> _save() async {
    final provider = context.read<AppProvider>();
    await provider.saveConfig(_draft);
    if (_sldlPathDraft.isNotEmpty) {
      provider.updateSldlPath(_sldlPathDraft);
    }
    setState(() => _dirty = false);
    if (mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Settings saved.')),
      );
    }
  }

  Future<void> _discard() async {
    final provider = context.read<AppProvider>();
    setState(() {
      _draft = provider.config.copy();
      _sldlPathDraft = provider.sldlExecutablePath ?? '';
      _dirty = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Settings'),
        bottom: TabBar(
          controller: _tabCtrl,
          isScrollable: true,
          tabAlignment: TabAlignment.start,
          tabs: _tabs
              .map((t) => Tab(
                    icon: Icon(t.$2, size: 16),
                    text: t.$1,
                    iconMargin: const EdgeInsets.only(bottom: 2),
                  ))
              .toList(),
        ),
        actions: [
          if (_dirty)
            TextButton.icon(
              icon: const Icon(Icons.undo),
              label: const Text('Discard'),
              onPressed: _discard,
            ),
          FilledButton.icon(
            icon: const Icon(Icons.save),
            label: const Text('Save'),
            onPressed: _dirty ? _save : null,
          ),
          const SizedBox(width: 8),
        ],
      ),
      body: TabBarView(
        controller: _tabCtrl,
        children: [
          _GeneralTab(draft: _draft, sldlPath: _sldlPathDraft, onChanged: (p, s) {
            _draft = p;
            _sldlPathDraft = s;
            _markDirty();
          }),
          _ConnectionTab(draft: _draft, onChanged: (_) { _markDirty(); }),
          _SearchTab(draft: _draft, onChanged: (_) { _markDirty(); }),
          _FileConditionsTab(draft: _draft, onChanged: (_) { _markDirty(); }),
          _AlbumAggregateTab(draft: _draft, onChanged: (_) { _markDirty(); }),
          _SpotifyTab(draft: _draft, onChanged: (_) { _markDirty(); }),
          _YouTubeTab(draft: _draft, onChanged: (_) { _markDirty(); }),
          _CsvListTab(draft: _draft, onChanged: (_) { _markDirty(); }),
          _AdvancedTab(draft: _draft, onChanged: (_) { _markDirty(); }),
        ],
      ),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared helper widgets
// ─────────────────────────────────────────────────────────────────────────────

class _Section extends StatelessWidget {
  final String title;
  final List<Widget> children;

  const _Section({required this.title, required this.children});

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(left: 2, bottom: 8),
          child: Text(title,
              style: Theme.of(context).textTheme.titleSmall?.copyWith(
                    color: Theme.of(context).colorScheme.primary,
                  )),
        ),
        ...children,
      ],
    );
  }
}

Widget _field(
  String label,
  String? value,
  ValueChanged<String> onChanged, {
  String? hint,
  TextInputType? keyboardType,
  bool obscure = false,
  String? helperText,
}) {
  return Padding(
    padding: const EdgeInsets.only(bottom: 12),
    child: TextFormField(
      initialValue: value ?? '',
      decoration: InputDecoration(
        labelText: label,
        hintText: hint,
        helperText: helperText,
        border: const OutlineInputBorder(),
        isDense: true,
      ),
      keyboardType: keyboardType,
      obscureText: obscure,
      onChanged: (v) => onChanged(v.isEmpty ? '' : v),
    ),
  );
}

Widget _toggle(
  String label,
  bool? value,
  ValueChanged<bool> onChanged, {
  String? subtitle,
}) {
  return SwitchListTile(
    title: Text(label),
    subtitle: subtitle != null ? Text(subtitle) : null,
    value: value ?? false,
    onChanged: onChanged,
    dense: true,
    contentPadding: const EdgeInsets.symmetric(horizontal: 0),
  );
}

Widget _intField(
  String label,
  int? value,
  ValueChanged<int?> onChanged, {
  String? hint,
  String? helperText,
}) {
  return _field(
    label,
    value?.toString(),
    (v) => onChanged(v.isEmpty ? null : int.tryParse(v)),
    hint: hint,
    helperText: helperText,
    keyboardType: TextInputType.number,
  );
}

/// Path field that properly manages its controller as a stateful widget.
class _PathField extends StatefulWidget {
  final String label;
  final String? value;
  final ValueChanged<String> onChanged;
  final bool isFile;
  final List<String>? fileExtensions;
  final String? helperText;

  const _PathField({
    required this.label,
    required this.value,
    required this.onChanged,
    this.isFile = false,
    this.fileExtensions,
    this.helperText,
  });

  @override
  State<_PathField> createState() => _PathFieldState();
}

class _PathFieldState extends State<_PathField> {
  late TextEditingController _ctrl;

  @override
  void initState() {
    super.initState();
    _ctrl = TextEditingController(text: widget.value ?? '');
  }

  @override
  void didUpdateWidget(_PathField old) {
    super.didUpdateWidget(old);
    // Only sync when the value changes from outside (e.g., browse pick)
    if (old.value != widget.value && _ctrl.text != (widget.value ?? '')) {
      _ctrl.text = widget.value ?? '';
    }
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Row(
        children: [
          Expanded(
            child: TextFormField(
              controller: _ctrl,
              decoration: InputDecoration(
                labelText: widget.label,
                helperText: widget.helperText,
                border: const OutlineInputBorder(),
                isDense: true,
              ),
              onChanged: widget.onChanged,
            ),
          ),
          const SizedBox(width: 6),
          IconButton(
            icon: const Icon(Icons.folder_open),
            tooltip: 'Browse',
            onPressed: () async {
              if (widget.isFile) {
                final r = await FilePicker.platform.pickFiles(
                  allowedExtensions: widget.fileExtensions,
                  type: widget.fileExtensions != null ? FileType.custom : FileType.any,
                );
                if (r?.files.single.path != null) {
                  final path = r!.files.single.path!;
                  _ctrl.text = path;
                  widget.onChanged(path);
                }
              } else {
                final r = await FilePicker.platform.getDirectoryPath();
                if (r != null) {
                  _ctrl.text = r;
                  widget.onChanged(r);
                }
              }
            },
          ),
        ],
      ),
    );
  }
}

Widget _pathField(
  BuildContext context,
  String label,
  String? value,
  ValueChanged<String> onChanged, {
  bool isFile = false,
  List<String>? fileExtensions,
  String? helperText,
}) {
  return _PathField(
    label: label,
    value: value,
    onChanged: onChanged,
    isFile: isFile,
    fileExtensions: fileExtensions,
    helperText: helperText,
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: General
// ─────────────────────────────────────────────────────────────────────────────

class _GeneralTab extends StatefulWidget {
  final SldlConfig draft;
  final String sldlPath;
  final void Function(SldlConfig, String) onChanged;

  const _GeneralTab(
      {required this.draft, required this.sldlPath, required this.onChanged});

  @override
  State<_GeneralTab> createState() => _GeneralTabState();
}

class _GeneralTabState extends State<_GeneralTab> {
  late SldlConfig _d;
  late String _sldlPath;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
    _sldlPath = widget.sldlPath;
  }

  void _notify() => widget.onChanged(_d, _sldlPath);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      _Section(title: 'sldl Executable', children: [
        _pathField(context, 'sldl Executable Path', _sldlPath, (v) {
          setState(() => _sldlPath = v);
          _notify();
        }, isFile: true, fileExtensions: ['exe', ''],
          helperText: 'Path to the sldl binary (auto-detected if left empty)'),
      ]),

      _Section(title: 'Downloads', children: [
        _pathField(context, 'Download Directory (-p)', _d.path, (v) {
          setState(() => _d.path = v.isEmpty ? null : v);
          _notify();
        }, helperText: 'Where downloaded files will be saved'),
        _intField('Concurrent Downloads', _d.concurrentDownloads, (v) {
          setState(() => _d.concurrentDownloads = v);
          _notify();
        }, hint: '2', helperText: 'Max parallel downloads for normal mode'),
      ]),

      _Section(title: 'Playlist / Index', children: [
        _toggle('Write Playlist (.m3u)', _d.writePlaylist, (v) {
          setState(() => _d.writePlaylist = v ? true : null);
          _notify();
        }, subtitle: 'Create an m3u playlist file in the output directory'),
        _pathField(context, 'Playlist Path', _d.playlistPath, (v) {
          setState(() => _d.playlistPath = v.isEmpty ? null : v);
          _notify();
        }),
        _toggle('No Write Index', _d.noWriteIndex, (v) {
          setState(() => _d.noWriteIndex = v ? true : null);
          _notify();
        }, subtitle: 'Do not create a .sldl index file'),
        _pathField(context, 'Index Path', _d.indexPath, (v) {
          setState(() => _d.indexPath = v.isEmpty ? null : v);
          _notify();
        }, helperText: 'Override default path for the sldl index file'),
      ]),

      _Section(title: 'Skip Behaviour', children: [
        _toggle('No Skip Existing', _d.noSkipExisting, (v) {
          setState(() => _d.noSkipExisting = v ? true : null);
          _notify();
        }, subtitle: 'Download even if the file already exists'),
        _toggle('Skip Not Found', _d.skipNotFound, (v) {
          setState(() => _d.skipNotFound = v ? true : null);
          _notify();
        }, subtitle: 'Skip tracks that were not found in the last run'),
        _toggle('Skip Check Conditions', _d.skipCheckCond, (v) {
          setState(() => _d.skipCheckCond = v ? true : null);
          _notify();
        }, subtitle: 'Check file conditions when skipping existing files'),
        _toggle('Skip Check Preferred Conditions', _d.skipCheckPrefCond, (v) {
          setState(() => _d.skipCheckPrefCond = v ? true : null);
          _notify();
        }, subtitle: 'Check preferred conditions when skipping existing files'),
        _pathField(context, 'Skip Music Directory', _d.skipMusicDir, (v) {
          setState(() => _d.skipMusicDir = v.isEmpty ? null : v);
          _notify();
        }, helperText: 'Also skip tracks found in this music library'),
      ]),

      _Section(title: 'Other', children: [
        _toggle('No Incomplete Extension', _d.noIncompleteExt, (v) {
          setState(() => _d.noIncompleteExt = v ? true : null);
          _notify();
        }, subtitle: 'Save files with final name instead of .incomplete extension'),
        _field('Profile Names', _d.profile, (v) {
          setState(() => _d.profile = v.isEmpty ? null : v);
          _notify();
        }, hint: 'e.g. lossless,youtube', helperText: 'Configuration profile(s) to activate'),
        _field('User Description', _d.userDescription, (v) {
          setState(() => _d.userDescription = v.isEmpty ? null : v);
          _notify();
        }, helperText: 'Optional description text for your Soulseek account'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: Connection
// ─────────────────────────────────────────────────────────────────────────────

class _ConnectionTab extends StatefulWidget {
  final SldlConfig draft;
  final ValueChanged<SldlConfig> onChanged;
  const _ConnectionTab({required this.draft, required this.onChanged});

  @override
  State<_ConnectionTab> createState() => _ConnectionTabState();
}

class _ConnectionTabState extends State<_ConnectionTab> {
  late SldlConfig _d;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
  }

  void _notify() => widget.onChanged(_d);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      _Section(title: 'Soulseek Credentials', children: [
        Container(
          padding: const EdgeInsets.all(10),
          margin: const EdgeInsets.only(bottom: 12),
          decoration: BoxDecoration(
            color: Theme.of(context).colorScheme.secondaryContainer.withOpacity(0.4),
            borderRadius: BorderRadius.circular(8),
          ),
          child: Text(
            'Tip: It is recommended to use a separate Soulseek account for sldl to avoid connection issues.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ),
        _field('Username', _d.username, (v) {
          setState(() => _d.username = v.isEmpty ? null : v);
          _notify();
        }),
        _field('Password', _d.password, (v) {
          setState(() => _d.password = v.isEmpty ? null : v);
          _notify();
        }, obscure: true),
      ]),

      _Section(title: 'Connection Settings', children: [
        _intField('Listen Port', _d.listenPort, (v) {
          setState(() => _d.listenPort = v);
          _notify();
        }, hint: '49998', helperText: 'Port for incoming Soulseek connections'),
        _intField('Connect Timeout (ms)', _d.connectTimeout, (v) {
          setState(() => _d.connectTimeout = v);
          _notify();
        }, hint: '20000', helperText: 'Timeout used when logging in (milliseconds)'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: Search
// ─────────────────────────────────────────────────────────────────────────────

class _SearchTab extends StatefulWidget {
  final SldlConfig draft;
  final ValueChanged<SldlConfig> onChanged;
  const _SearchTab({required this.draft, required this.onChanged});

  @override
  State<_SearchTab> createState() => _SearchTabState();
}

class _SearchTabState extends State<_SearchTab> {
  late SldlConfig _d;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
  }

  void _notify() => widget.onChanged(_d);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      _Section(title: 'Search Behaviour', children: [
        _toggle('Fast Search', _d.fastSearch, (v) {
          setState(() => _d.fastSearch = v ? true : null);
          _notify();
        }, subtitle: 'Download as soon as a matching file is found (normal mode only)'),
        _toggle('Remove Feat.', _d.removeFt, (v) {
          setState(() => _d.removeFt = v ? true : null);
          _notify();
        }, subtitle: "Remove 'feat.' and everything after before searching"),
        _toggle('Artist Maybe Wrong', _d.artistMaybeWrong, (v) {
          setState(() => _d.artistMaybeWrong = v ? true : null);
          _notify();
        }, subtitle: 'Also search without the artist name (useful for SoundCloud)'),
        _toggle('Desperate Search (-d)', _d.desperate, (v) {
          setState(() => _d.desperate = v ? true : null);
          _notify();
        }, subtitle: 'Try harder to find tracks by searching title/artist separately'),
        _field('Regex Filter', _d.regex, (v) {
          setState(() => _d.regex = v.isEmpty ? null : v);
          _notify();
        }, hint: 'e.g. [\\[\\(].*?[\\]\\)]',
          helperText: 'Remove matched text from titles/artists. Prefix with T:, A:, or L: to target title, artist, or album'),
      ]),

      _Section(title: 'Rate Limits & Timeouts', children: [
        _intField('Search Timeout (ms)', _d.searchTimeout, (v) {
          setState(() => _d.searchTimeout = v);
          _notify();
        }, hint: '6000'),
        _intField('Max Stale Time (ms)', _d.maxStaleTime, (v) {
          setState(() => _d.maxStaleTime = v);
          _notify();
        }, hint: '30000', helperText: 'Max time without download progress before giving up'),
        _intField('Searches Per Time', _d.searchesPerTime, (v) {
          setState(() => _d.searchesPerTime = v);
          _notify();
        }, hint: '34', helperText: 'Max searches per time interval (higher values may cause 30-min bans)'),
        _intField('Searches Renew Time (sec)', _d.searchesRenewTime, (v) {
          setState(() => _d.searchesRenewTime = v);
          _notify();
        }, hint: '220', helperText: 'How often (in seconds) search quota is replenished'),
      ]),

      _Section(title: 'Failure Handling', children: [
        _intField('Fails to Downrank', _d.failsToDownrank, (v) {
          setState(() => _d.failsToDownrank = v);
          _notify();
        }, hint: '1', helperText: "Number of fails to downrank a user's shares"),
        _intField('Fails to Ignore', _d.failsToIgnore, (v) {
          setState(() => _d.failsToIgnore = v);
          _notify();
        }, hint: '2', helperText: "Number of fails to ban/ignore a user's shares"),
      ]),

      _Section(title: 'yt-dlp Fallback', children: [
        _toggle('Use yt-dlp', _d.ytDlp, (v) {
          setState(() => _d.ytDlp = v ? true : null);
          _notify();
        }, subtitle: 'Use yt-dlp to download tracks not found on Soulseek'),
        _field('yt-dlp Arguments', _d.ytDlpArgument, (v) {
          setState(() => _d.ytDlpArgument = v.isEmpty ? null : v);
          _notify();
        }, hint: '"{id}" -f bestaudio/best -cix -o "{savepath}.%(ext)s"'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: File Conditions
// ─────────────────────────────────────────────────────────────────────────────

class _FileConditionsTab extends StatefulWidget {
  final SldlConfig draft;
  final ValueChanged<SldlConfig> onChanged;
  const _FileConditionsTab({required this.draft, required this.onChanged});

  @override
  State<_FileConditionsTab> createState() => _FileConditionsTabState();
}

class _FileConditionsTabState extends State<_FileConditionsTab> {
  late SldlConfig _d;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
  }

  void _notify() => widget.onChanged(_d);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      Container(
        padding: const EdgeInsets.all(10),
        margin: const EdgeInsets.only(bottom: 16),
        decoration: BoxDecoration(
          color: Theme.of(context).colorScheme.secondaryContainer.withOpacity(0.4),
          borderRadius: BorderRadius.circular(8),
        ),
        child: Text(
          'Required conditions: files not matching will be skipped.\n'
          'Preferred conditions: files matching are ranked higher.\n'
          'Leave blank to use sldl defaults.',
          style: Theme.of(context).textTheme.bodySmall,
        ),
      ),

      _Section(title: 'Required Conditions', children: [
        _field('Format (comma-separated)', _d.format, (v) {
          setState(() => _d.format = v.isEmpty ? null : v);
          _notify();
        }, hint: 'mp3,flac,ogg,m4a,opus,wav,aac,alac'),
        _intField('Length Tolerance (sec)', _d.lengthTol, (v) {
          setState(() => _d.lengthTol = v);
          _notify();
        }),
        _intField('Min Bitrate', _d.minBitrate, (v) {
          setState(() => _d.minBitrate = v);
          _notify();
        }),
        _intField('Max Bitrate', _d.maxBitrate, (v) {
          setState(() => _d.maxBitrate = v);
          _notify();
        }),
        _intField('Min Sample Rate', _d.minSamplerate, (v) {
          setState(() => _d.minSamplerate = v);
          _notify();
        }),
        _intField('Max Sample Rate', _d.maxSamplerate, (v) {
          setState(() => _d.maxSamplerate = v);
          _notify();
        }),
        _intField('Min Bit Depth', _d.minBitdepth, (v) {
          setState(() => _d.minBitdepth = v);
          _notify();
        }),
        _intField('Max Bit Depth', _d.maxBitdepth, (v) {
          setState(() => _d.maxBitdepth = v);
          _notify();
        }),
        _toggle('Strict Title', _d.strictTitle, (v) {
          setState(() => _d.strictTitle = v ? true : null);
          _notify();
        }, subtitle: 'File name must contain track title'),
        _toggle('Strict Artist', _d.strictArtist, (v) {
          setState(() => _d.strictArtist = v ? true : null);
          _notify();
        }, subtitle: 'File path must contain artist name'),
        _toggle('Strict Album', _d.strictAlbum, (v) {
          setState(() => _d.strictAlbum = v ? true : null);
          _notify();
        }, subtitle: 'File path must contain album name'),
        _toggle('Strict Conditions', _d.strictConditions, (v) {
          setState(() => _d.strictConditions = v ? true : null);
          _notify();
        }, subtitle: 'Reject files with missing/unknown properties'),
        _field('Banned Users', _d.bannedUsers, (v) {
          setState(() => _d.bannedUsers = v.isEmpty ? null : v);
          _notify();
        }, hint: 'user1,user2', helperText: 'Comma-separated list of users to ignore'),
      ]),

      _Section(title: 'Preferred Conditions', children: [
        _field('Preferred Format', _d.prefFormat, (v) {
          setState(() => _d.prefFormat = v.isEmpty ? null : v);
          _notify();
        }, hint: 'mp3', helperText: 'Default: mp3'),
        _intField('Preferred Length Tolerance (sec)', _d.prefLengthTol, (v) {
          setState(() => _d.prefLengthTol = v);
          _notify();
        }, hint: '3'),
        _intField('Preferred Min Bitrate', _d.prefMinBitrate, (v) {
          setState(() => _d.prefMinBitrate = v);
          _notify();
        }, hint: '200'),
        _intField('Preferred Max Bitrate', _d.prefMaxBitrate, (v) {
          setState(() => _d.prefMaxBitrate = v);
          _notify();
        }, hint: '2500'),
        _intField('Preferred Min Sample Rate', _d.prefMinSamplerate, (v) {
          setState(() => _d.prefMinSamplerate = v);
          _notify();
        }),
        _intField('Preferred Max Sample Rate', _d.prefMaxSamplerate, (v) {
          setState(() => _d.prefMaxSamplerate = v);
          _notify();
        }, hint: '48000'),
        _intField('Preferred Min Bit Depth', _d.prefMinBitdepth, (v) {
          setState(() => _d.prefMinBitdepth = v);
          _notify();
        }),
        _intField('Preferred Max Bit Depth', _d.prefMaxBitdepth, (v) {
          setState(() => _d.prefMaxBitdepth = v);
          _notify();
        }),
        _field('Preferred Banned Users', _d.prefBannedUsers, (v) {
          setState(() => _d.prefBannedUsers = v.isEmpty ? null : v);
          _notify();
        }, helperText: 'Comma-separated list of users to downrank'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: Album & Aggregate
// ─────────────────────────────────────────────────────────────────────────────

class _AlbumAggregateTab extends StatefulWidget {
  final SldlConfig draft;
  final ValueChanged<SldlConfig> onChanged;
  const _AlbumAggregateTab({required this.draft, required this.onChanged});

  @override
  State<_AlbumAggregateTab> createState() => _AlbumAggregateTabState();
}

class _AlbumAggregateTabState extends State<_AlbumAggregateTab> {
  late SldlConfig _d;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
  }

  void _notify() => widget.onChanged(_d);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      _Section(title: 'Album Mode', children: [
        _toggle('Album Mode (-a)', _d.album, (v) {
          setState(() => _d.album = v ? true : null);
          _notify();
        }, subtitle: 'Default to album download mode'),
        _toggle('Interactive Mode (-t)', _d.interactive, (v) {
          setState(() => _d.interactive = v ? true : null);
          _notify();
        }, subtitle: 'Interactively select folders (opens terminal)'),
        _field('Album Track Count', _d.albumTrackCount, (v) {
          setState(() => _d.albumTrackCount = v.isEmpty ? null : v);
          _notify();
        }, hint: 'e.g. 13+ or 5', helperText: 'Exact number of tracks, or n+/n- for inequalities'),
        _DropdownField<String>(
          label: 'Album Art',
          value: _d.albumArt ?? 'default',
          items: const {
            'default': 'Default (no extra images)',
            'largest': 'Largest (download from folder with largest image)',
            'most': 'Most (download from folder with most images)',
          },
          onChanged: (v) {
            setState(() => _d.albumArt = v == 'default' ? null : v);
            _notify();
          },
        ),
        _toggle('Album Art Only', _d.albumArtOnly, (v) {
          setState(() => _d.albumArtOnly = v ? true : null);
          _notify();
        }, subtitle: 'Only download album art for the provided album'),
        _toggle('No Browse Folder', _d.noBrowseFolder, (v) {
          setState(() => _d.noBrowseFolder = v ? true : null);
          _notify();
        }, subtitle: 'Do not automatically browse user shares to get all files'),
        _field('Failed Album Path', _d.failedAlbumPath, (v) {
          setState(() => _d.failedAlbumPath = v.isEmpty ? null : v);
          _notify();
        }, hint: 'delete | disable | /path/to/folder',
          helperText: "Where to move failed album files. 'delete' removes them, 'disable' keeps in place"),
        _toggle('Album Parallel Search', _d.albumParallelSearch, (v) {
          setState(() => _d.albumParallelSearch = v ? true : null);
          _notify();
        }, subtitle: 'Run album searches in parallel, then download sequentially'),
        _intField('Album Parallel Search Count', _d.albumParallelSearchCount, (v) {
          setState(() => _d.albumParallelSearchCount = v);
          _notify();
        }, hint: '5'),
      ]),

      _Section(title: 'Aggregate Mode', children: [
        _toggle('Aggregate Mode (-g)', _d.aggregate, (v) {
          setState(() => _d.aggregate = v ? true : null);
          _notify();
        }, subtitle: 'Find and download all distinct songs/albums for the input'),
        _intField('Aggregate Length Tolerance', _d.aggregateLengthTol, (v) {
          setState(() => _d.aggregateLengthTol = v);
          _notify();
        }, hint: '3', helperText: 'Max length diff (sec) to consider two tracks equal'),
        _intField('Min Shares (Aggregate)', _d.minSharesAggregate, (v) {
          setState(() => _d.minSharesAggregate = v);
          _notify();
        }, hint: '2', helperText: 'Minimum shares of a track/album to include'),
        _toggle('Relax Filtering', _d.relaxFiltering, (v) {
          setState(() => _d.relaxFiltering = v ? true : null);
          _notify();
        }, subtitle: 'Slightly relax file filtering in aggregate mode'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: Spotify
// ─────────────────────────────────────────────────────────────────────────────

class _SpotifyTab extends StatefulWidget {
  final SldlConfig draft;
  final ValueChanged<SldlConfig> onChanged;
  const _SpotifyTab({required this.draft, required this.onChanged});

  @override
  State<_SpotifyTab> createState() => _SpotifyTabState();
}

class _SpotifyTabState extends State<_SpotifyTab> {
  late SldlConfig _d;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
  }

  void _notify() => widget.onChanged(_d);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      _Section(title: 'Spotify Application', children: [
        Container(
          padding: const EdgeInsets.all(12),
          margin: const EdgeInsets.only(bottom: 12),
          decoration: BoxDecoration(
            color: Theme.of(context).colorScheme.secondaryContainer.withOpacity(0.4),
            borderRadius: BorderRadius.circular(8),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Setup Instructions',
                  style: Theme.of(context).textTheme.labelLarge),
              const SizedBox(height: 8),
              const Text(
                '1. Go to https://developer.spotify.com/dashboard\n'
                '2. Click "Create App" and fill in the details.\n'
                '3. Add http://127.0.0.1:48721/callback as a Redirect URI.\n'
                '4. Click Settings → copy your Client ID and Client Secret.\n'
                '5. Paste them below and save.\n\n'
                'On first use, sldl will open a browser for OAuth authorization and print a token and refresh token. Store those below for subsequent runs.',
                style: TextStyle(fontSize: 13),
              ),
            ],
          ),
        ),
        _field('Client ID (--spotify-id)', _d.spotifyId, (v) {
          setState(() => _d.spotifyId = v.isEmpty ? null : v);
          _notify();
        }),
        _field('Client Secret (--spotify-secret)', _d.spotifySecret, (v) {
          setState(() => _d.spotifySecret = v.isEmpty ? null : v);
          _notify();
        }, obscure: true),
        _field('Access Token (--spotify-token)', _d.spotifyToken, (v) {
          setState(() => _d.spotifyToken = v.isEmpty ? null : v);
          _notify();
        }, helperText: 'Short-lived access token (valid ~1 hour). Optional if refresh token is set.'),
        _field('Refresh Token (--spotify-refresh)', _d.spotifyRefresh, (v) {
          setState(() => _d.spotifyRefresh = v.isEmpty ? null : v);
          _notify();
        }, helperText: 'Long-lived refresh token. Allows sldl to renew access automatically.'),
        _toggle('Remove From Source', _d.removeFromSource, (v) {
          setState(() => _d.removeFromSource = v ? true : null);
          _notify();
        }, subtitle: 'Remove downloaded tracks from the source Spotify playlist'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: YouTube
// ─────────────────────────────────────────────────────────────────────────────

class _YouTubeTab extends StatefulWidget {
  final SldlConfig draft;
  final ValueChanged<SldlConfig> onChanged;
  const _YouTubeTab({required this.draft, required this.onChanged});

  @override
  State<_YouTubeTab> createState() => _YouTubeTabState();
}

class _YouTubeTabState extends State<_YouTubeTab> {
  late SldlConfig _d;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
  }

  void _notify() => widget.onChanged(_d);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      _Section(title: 'YouTube Data API', children: [
        Container(
          padding: const EdgeInsets.all(12),
          margin: const EdgeInsets.only(bottom: 12),
          decoration: BoxDecoration(
            color: Theme.of(context).colorScheme.secondaryContainer.withOpacity(0.4),
            borderRadius: BorderRadius.circular(8),
          ),
          child: const Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('YouTube API Key (Optional)',
                  style: TextStyle(fontWeight: FontWeight.bold)),
              SizedBox(height: 8),
              Text(
                'A YouTube Data API key enables reliable retrieval of all playlist videos.\n\n'
                'To get a key:\n'
                '1. Go to https://console.cloud.google.com\n'
                '2. Create a new project (or select an existing one).\n'
                '3. Click "Enable APIs & Services" and search for "YouTube Data API v3".\n'
                '4. Enable the API and go to Credentials.\n'
                '5. Click "Create Credentials" → "API Key".\n'
                '6. Paste the key below.\n\n'
                'Without a key, YouTube playlist fetching may miss some videos.',
                style: TextStyle(fontSize: 13),
              ),
            ],
          ),
        ),
        _field('YouTube Data API Key (--youtube-key)', _d.youtubeKey, (v) {
          setState(() => _d.youtubeKey = v.isEmpty ? null : v);
          _notify();
        }),
        _toggle('Get Deleted Videos', _d.getDeleted, (v) {
          setState(() => _d.getDeleted = v ? true : null);
          _notify();
        }, subtitle: 'Retrieve deleted video titles from the Wayback Machine (requires yt-dlp)'),
        _toggle('Deleted Only', _d.deletedOnly, (v) {
          setState(() => _d.deletedOnly = v ? true : null);
          _notify();
        }, subtitle: 'Only retrieve and download deleted videos'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: CSV / List
// ─────────────────────────────────────────────────────────────────────────────

class _CsvListTab extends StatefulWidget {
  final SldlConfig draft;
  final ValueChanged<SldlConfig> onChanged;
  const _CsvListTab({required this.draft, required this.onChanged});

  @override
  State<_CsvListTab> createState() => _CsvListTabState();
}

class _CsvListTabState extends State<_CsvListTab> {
  late SldlConfig _d;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
  }

  void _notify() => widget.onChanged(_d);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      _Section(title: 'CSV Column Names', children: [
        _field('Artist Column (--artist-col)', _d.artistCol, (v) {
          setState(() => _d.artistCol = v.isEmpty ? null : v);
          _notify();
        }, hint: 'Artist'),
        _field('Title Column (--title-col)', _d.titleCol, (v) {
          setState(() => _d.titleCol = v.isEmpty ? null : v);
          _notify();
        }, hint: 'Title'),
        _field('Album Column (--album-col)', _d.albumCol, (v) {
          setState(() => _d.albumCol = v.isEmpty ? null : v);
          _notify();
        }, hint: 'Album'),
        _field('Length Column (--length-col)', _d.lengthCol, (v) {
          setState(() => _d.lengthCol = v.isEmpty ? null : v);
          _notify();
        }, hint: 'Length'),
        _field('Track Count Column (--album-track-count-col)', _d.trackCountCol, (v) {
          setState(() => _d.trackCountCol = v.isEmpty ? null : v);
          _notify();
        }),
        _field('YouTube ID Column (--yt-id-col)', _d.ytIdCol, (v) {
          setState(() => _d.ytIdCol = v.isEmpty ? null : v);
          _notify();
        }),
        _field('YouTube Desc Column (--yt-desc-col)', _d.ytDescCol, (v) {
          setState(() => _d.ytDescCol = v.isEmpty ? null : v);
          _notify();
        }),
        _field('Time Format (--time-format)', _d.timeFormat, (v) {
          setState(() => _d.timeFormat = v.isEmpty ? null : v);
          _notify();
        }, hint: 's', helperText: 'Time format in Length column (e.g. h:m:s.ms)'),
        _toggle('YouTube Parse (--yt-parse)', _d.ytParse, (v) {
          setState(() => _d.ytParse = v ? true : null);
          _notify();
        }, subtitle: 'Parse YouTube video titles/channel names into title/artist'),
        _toggle('Remove From Source', _d.removeFromSource, (v) {
          setState(() => _d.removeFromSource = v ? true : null);
          _notify();
        }, subtitle: 'Remove downloaded tracks from the source CSV'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tab: Advanced
// ─────────────────────────────────────────────────────────────────────────────

class _AdvancedTab extends StatefulWidget {
  final SldlConfig draft;
  final ValueChanged<SldlConfig> onChanged;
  const _AdvancedTab({required this.draft, required this.onChanged});

  @override
  State<_AdvancedTab> createState() => _AdvancedTabState();
}

class _AdvancedTabState extends State<_AdvancedTab> {
  late SldlConfig _d;

  @override
  void initState() {
    super.initState();
    _d = widget.draft;
  }

  void _notify() => widget.onChanged(_d);

  @override
  Widget build(BuildContext context) {
    return _SettingsTabScroll(children: [
      _Section(title: 'On-Complete Actions', children: [
        Container(
          padding: const EdgeInsets.all(10),
          margin: const EdgeInsets.only(bottom: 8),
          decoration: BoxDecoration(
            color: Theme.of(context).colorScheme.secondaryContainer.withOpacity(0.4),
            borderRadius: BorderRadius.circular(8),
          ),
          child: const Text(
            'Run a command after each download. Available prefixes:\n'
            '  1: — only if succeeded  |  2: — only if failed\n'
            '  a: — album only  |  s: — use shell  |  h: — hide window\n'
            '  r: — read output  |  u: — use output to update index\n'
            'Variables: {path}, {artist}, {title}, {ext}, {is-audio}, etc.',
            style: TextStyle(fontSize: 12, fontFamily: 'monospace'),
          ),
        ),
        _field('On-Complete Command', _d.onComplete, (v) {
          setState(() => _d.onComplete = v.isEmpty ? null : v);
          _notify();
        }, hint: '1:h: cmd /c start "" "foobar2000.exe" /immediate /add "{path}"'),
      ]),

      _Section(title: 'Logging & Debug', children: [
        _toggle('Verbose (-v)', _d.verbose, (v) {
          setState(() => _d.verbose = v ? true : null);
          _notify();
        }, subtitle: 'Print extra debug information'),
        _pathField(context, 'Log File', _d.logFile, (v) {
          setState(() => _d.logFile = v.isEmpty ? null : v);
          _notify();
        }, isFile: true, helperText: 'Write debug info to this file'),
        _pathField(context, 'Mock Files Directory', _d.mockFilesDir, (v) {
          setState(() => _d.mockFilesDir = v.isEmpty ? null : v);
          _notify();
        }, helperText: 'Simulate downloads using local audio files (for testing)'),
      ]),
    ]);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared: scrollable tab container
// ─────────────────────────────────────────────────────────────────────────────

class _SettingsTabScroll extends StatelessWidget {
  final List<Widget> children;
  const _SettingsTabScroll({required this.children});

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          for (int i = 0; i < children.length; i++) ...[
            children[i],
            if (i < children.length - 1) const SizedBox(height: 20),
          ],
          const SizedBox(height: 40),
        ],
      ),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared: dropdown field
// ─────────────────────────────────────────────────────────────────────────────

class _DropdownField<T> extends StatelessWidget {
  final String label;
  final T value;
  final Map<T, String> items;
  final ValueChanged<T?> onChanged;

  const _DropdownField({
    required this.label,
    required this.value,
    required this.items,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: DropdownButtonFormField<T>(
        value: value,
        decoration: InputDecoration(
          labelText: label,
          border: const OutlineInputBorder(),
          isDense: true,
        ),
        items: items.entries
            .map((e) => DropdownMenuItem(
                  value: e.key,
                  child: Text(e.value),
                ))
            .toList(),
        onChanged: onChanged,
      ),
    );
  }
}
