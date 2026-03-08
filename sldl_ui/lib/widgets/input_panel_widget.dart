import 'package:flutter/material.dart';
import 'package:file_picker/file_picker.dart';
import '../models/download_item.dart';

/// Input panel widget — lets the user type/paste a URL, search string,
/// or select a local file, configure download mode, and submit.
class InputPanelWidget extends StatefulWidget {
  final void Function(DownloadItem) onSubmit;

  const InputPanelWidget({super.key, required this.onSubmit});

  @override
  State<InputPanelWidget> createState() => _InputPanelWidgetState();
}

class _InputPanelWidgetState extends State<InputPanelWidget> {
  final _inputCtrl = TextEditingController();
  final _nameFormatCtrl = TextEditingController();
  InputType _inputType = InputType.auto;
  bool _albumMode = false;
  bool _aggregateMode = false;
  bool _interactiveMode = false;
  bool _showAdvanced = false;

  // Extra per-job options
  final _maxTracksCtrl = TextEditingController();
  final _offsetCtrl = TextEditingController();

  @override
  void initState() {
    super.initState();
    // Rebuild when input changes to update the submit button state
    _inputCtrl.addListener(() => setState(() {}));
  }

  @override
  void dispose() {
    _inputCtrl.dispose();
    _nameFormatCtrl.dispose();
    _maxTracksCtrl.dispose();
    _offsetCtrl.dispose();
    super.dispose();
  }

  void _submit() {
    final input = _inputCtrl.text.trim();
    if (input.isEmpty) return;

    final item = DownloadItem(
      input: input,
      inputType: _inputType,
      albumMode: _albumMode,
      aggregateMode: _aggregateMode,
      interactiveMode: _interactiveMode,
      nameFormat: _nameFormatCtrl.text.trim(),
    );

    widget.onSubmit(item);

    // Clear input for next job
    _inputCtrl.clear();
  }

  Future<void> _pickFile() async {
    final result = await FilePicker.platform.pickFiles(
      dialogTitle: 'Select input file',
      type: FileType.custom,
      allowedExtensions: ['csv', 'txt'],
    );
    if (result != null && result.files.single.path != null) {
      _inputCtrl.text = result.files.single.path!;
      // Auto-set input type based on extension
      final ext = result.files.single.extension?.toLowerCase();
      setState(() {
        if (ext == 'csv') {
          _inputType = InputType.csv;
        } else if (ext == 'txt') {
          _inputType = InputType.list;
        }
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Input row
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: TextField(
                controller: _inputCtrl,
                decoration: InputDecoration(
                  hintText:
                      'URL, search string, or file path — e.g. "spotify-likes" or "Artist - Album"',
                  border: const OutlineInputBorder(),
                  prefixIcon: const Icon(Icons.search),
                  suffixIcon: IconButton(
                    icon: const Icon(Icons.folder_open),
                    tooltip: 'Browse for file',
                    onPressed: _pickFile,
                  ),
                ),
                onSubmitted: (_) => _submit(),
              ),
            ),
            const SizedBox(width: 8),
            FilledButton.icon(
              icon: const Icon(Icons.download),
              label: const Text('Download'),
              onPressed: _inputCtrl.text.trim().isEmpty ? null : _submit,
            ),
          ],
        ),

        const SizedBox(height: 8),

        // Input type & mode row
        Wrap(
          spacing: 8,
          runSpacing: 6,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            // Input type selector
            DropdownButtonHideUnderline(
              child: Container(
                decoration: BoxDecoration(
                  border: Border.all(color: theme.colorScheme.outline),
                  borderRadius: BorderRadius.circular(4),
                ),
                padding: const EdgeInsets.symmetric(horizontal: 10),
                child: DropdownButton<InputType>(
                  value: _inputType,
                  isDense: true,
                  items: InputType.values
                      .map((t) => DropdownMenuItem(
                            value: t,
                            child: Text(t.label,
                                style: const TextStyle(fontSize: 13)),
                          ))
                      .toList(),
                  onChanged: (v) => setState(() => _inputType = v!),
                ),
              ),
            ),

            // Mode toggles
            _ModeChip(
              label: 'Album',
              icon: Icons.album,
              tooltip: '-a: Download an entire folder/album',
              selected: _albumMode,
              onChanged: (v) => setState(() => _albumMode = v),
            ),
            _ModeChip(
              label: 'Aggregate',
              icon: Icons.library_music,
              tooltip: '-g: Download all distinct tracks/albums',
              selected: _aggregateMode,
              onChanged: (v) => setState(() => _aggregateMode = v),
            ),
            _ModeChip(
              label: 'Interactive',
              icon: Icons.touch_app,
              tooltip: '-t: Interactive album selection (requires terminal)',
              selected: _interactiveMode,
              onChanged: (v) => setState(() => _interactiveMode = v),
            ),

            // Advanced toggle
            TextButton.icon(
              icon: Icon(
                _showAdvanced ? Icons.expand_less : Icons.tune,
                size: 16,
              ),
              label: Text(_showAdvanced ? 'Less options' : 'Options',
                  style: const TextStyle(fontSize: 12)),
              onPressed: () =>
                  setState(() => _showAdvanced = !_showAdvanced),
            ),
          ],
        ),

        // Advanced / per-job options
        if (_showAdvanced) ...[
          const SizedBox(height: 8),
          Card(
            elevation: 0,
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(10),
              side: BorderSide(color: theme.colorScheme.outlineVariant),
            ),
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Wrap(
                spacing: 12,
                runSpacing: 12,
                children: [
                  SizedBox(
                    width: 120,
                    child: TextField(
                      controller: _maxTracksCtrl,
                      decoration: const InputDecoration(
                        labelText: 'Max tracks (-n)',
                        border: OutlineInputBorder(),
                        isDense: true,
                      ),
                      keyboardType: TextInputType.number,
                    ),
                  ),
                  SizedBox(
                    width: 120,
                    child: TextField(
                      controller: _offsetCtrl,
                      decoration: const InputDecoration(
                        labelText: 'Offset (-o)',
                        border: OutlineInputBorder(),
                        isDense: true,
                      ),
                      keyboardType: TextInputType.number,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],

      ],
    );
  }
}

class _ModeChip extends StatelessWidget {
  final String label;
  final IconData icon;
  final String tooltip;
  final bool selected;
  final ValueChanged<bool> onChanged;

  const _ModeChip({
    required this.label,
    required this.icon,
    required this.tooltip,
    required this.selected,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: tooltip,
      child: FilterChip(
        avatar: Icon(icon, size: 14),
        label: Text(label, style: const TextStyle(fontSize: 12)),
        selected: selected,
        onSelected: onChanged,
        visualDensity: VisualDensity.compact,
      ),
    );
  }
}
