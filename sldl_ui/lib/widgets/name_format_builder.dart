import 'package:flutter/material.dart';

/// Tag descriptor for the name format builder.
class NameFormatTag {
  final String tag;
  final String description;
  final String category;

  const NameFormatTag({
    required this.tag,
    required this.description,
    required this.category,
  });
}

const _tags = <NameFormatTag>[
  // File tag values
  NameFormatTag(tag: 'artist', description: 'First artist', category: 'File Tags'),
  NameFormatTag(tag: 'artists', description: "Artists, joined with '&'", category: 'File Tags'),
  NameFormatTag(tag: 'albumartist', description: 'First album artist', category: 'File Tags'),
  NameFormatTag(tag: 'albumartists', description: "Album artists, joined with '&'", category: 'File Tags'),
  NameFormatTag(tag: 'title', description: 'Track title', category: 'File Tags'),
  NameFormatTag(tag: 'album', description: 'Album name', category: 'File Tags'),
  NameFormatTag(tag: 'year', description: 'Track year', category: 'File Tags'),
  NameFormatTag(tag: 'track', description: 'Track number', category: 'File Tags'),
  NameFormatTag(tag: 'disc', description: 'Disc number', category: 'File Tags'),
  NameFormatTag(tag: 'length', description: 'Track length (seconds)', category: 'File Tags'),

  // Source values
  NameFormatTag(tag: 'sartist', description: 'Source artist', category: 'Source'),
  NameFormatTag(tag: 'stitle', description: 'Source track title', category: 'Source'),
  NameFormatTag(tag: 'salbum', description: 'Source album name', category: 'Source'),
  NameFormatTag(tag: 'slength', description: 'Source track length', category: 'Source'),
  NameFormatTag(tag: 'uri', description: 'Track URI', category: 'Source'),
  NameFormatTag(tag: 'snum', description: 'Source item number (1-indexed)', category: 'Source'),
  NameFormatTag(tag: 'row', description: 'Line number (CSV/list only)', category: 'Source'),

  // Soulseek / download info
  NameFormatTag(tag: 'slsk-filename', description: 'Soulseek filename without extension', category: 'Soulseek'),
  NameFormatTag(tag: 'slsk-foldername', description: 'Soulseek folder name', category: 'Soulseek'),
  NameFormatTag(tag: 'extractor', description: 'Name of the extractor used', category: 'Soulseek'),
  NameFormatTag(tag: 'input', description: 'Input string', category: 'Soulseek'),
  NameFormatTag(tag: 'item-name', description: 'Name of the playlist/source', category: 'Soulseek'),

  // Path & status
  NameFormatTag(tag: 'default-folder', description: 'Default sldl folder name', category: 'Path & Status'),
  NameFormatTag(tag: 'bindir', description: 'Base application directory', category: 'Path & Status'),
  NameFormatTag(tag: 'path', description: 'Download file path (or folder if album)', category: 'Path & Status'),
  NameFormatTag(tag: 'path-noext', description: 'Download file path without extension', category: 'Path & Status'),
  NameFormatTag(tag: 'ext', description: 'File extension', category: 'Path & Status'),
  NameFormatTag(tag: 'type', description: 'Track type', category: 'Path & Status'),
  NameFormatTag(tag: 'state', description: 'Track state', category: 'Path & Status'),
  NameFormatTag(tag: 'failure-reason', description: 'Reason for failure if any', category: 'Path & Status'),
  NameFormatTag(tag: 'is-audio', description: 'If track is audio (true/false)', category: 'Path & Status'),
  NameFormatTag(tag: 'artist-maybe-wrong', description: 'If artist might be incorrect', category: 'Path & Status'),
];

/// A text field with a helper panel of clickable tags that insert `{tag}` at cursor.
class NameFormatBuilder extends StatefulWidget {
  final String initialValue;
  final ValueChanged<String>? onChanged;
  final String label;
  final String hint;

  const NameFormatBuilder({
    super.key,
    this.initialValue = '',
    this.onChanged,
    this.label = 'Name Format',
    this.hint = 'e.g. {artist( - )title|slsk-filename}',
  });

  @override
  State<NameFormatBuilder> createState() => _NameFormatBuilderState();
}

class _NameFormatBuilderState extends State<NameFormatBuilder> {
  late final TextEditingController _ctrl;
  bool _showTagHelper = false;
  String _selectedCategory = 'File Tags';

  static final _categories = _tags.map((t) => t.category).toSet().toList();

  @override
  void initState() {
    super.initState();
    _ctrl = TextEditingController(text: widget.initialValue);
    _ctrl.addListener(() => widget.onChanged?.call(_ctrl.text));
  }

  @override
  void didUpdateWidget(NameFormatBuilder old) {
    super.didUpdateWidget(old);
    if (old.initialValue != widget.initialValue &&
        _ctrl.text != widget.initialValue) {
      _ctrl.text = widget.initialValue;
    }
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  void _insertTag(String tag) {
    final text = _ctrl.text;
    final sel = _ctrl.selection;
    final insertionPoint = sel.isValid ? sel.baseOffset : text.length;
    final toInsert = '{$tag}';
    final newText = text.substring(0, insertionPoint) +
        toInsert +
        text.substring(insertionPoint.clamp(0, text.length));
    _ctrl.value = TextEditingValue(
      text: newText,
      selection: TextSelection.collapsed(
          offset: insertionPoint + toInsert.length),
    );
  }

  List<NameFormatTag> get _filteredTags =>
      _tags.where((t) => t.category == _selectedCategory).toList();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: TextFormField(
                controller: _ctrl,
                decoration: InputDecoration(
                  labelText: widget.label,
                  hintText: widget.hint,
                  border: const OutlineInputBorder(),
                  suffixIcon: IconButton(
                    tooltip: _showTagHelper ? 'Hide tag helper' : 'Show tag helper',
                    icon: Icon(
                      _showTagHelper ? Icons.expand_less : Icons.label_outline,
                      color: _showTagHelper
                          ? theme.colorScheme.primary
                          : null,
                    ),
                    onPressed: () =>
                        setState(() => _showTagHelper = !_showTagHelper),
                  ),
                ),
              ),
            ),
          ],
        ),
        if (_showTagHelper) ...[
          const SizedBox(height: 8),
          Card(
            elevation: 0,
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(10),
              side: BorderSide(color: theme.colorScheme.outlineVariant),
            ),
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  // Category selector
                  SingleChildScrollView(
                    scrollDirection: Axis.horizontal,
                    child: Row(
                      children: _categories.map((cat) {
                        final selected = cat == _selectedCategory;
                        return Padding(
                          padding: const EdgeInsets.only(right: 6),
                          child: FilterChip(
                            label: Text(cat, style: const TextStyle(fontSize: 12)),
                            selected: selected,
                            onSelected: (_) =>
                                setState(() => _selectedCategory = cat),
                          ),
                        );
                      }).toList(),
                    ),
                  ),
                  const SizedBox(height: 10),
                  // Tag chips grid
                  Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: _filteredTags.map((tag) {
                      return Tooltip(
                        message: tag.description,
                        child: ActionChip(
                          avatar: const Icon(Icons.add, size: 14),
                          label: Text(
                            '{${tag.tag}}',
                            style: const TextStyle(fontSize: 12, fontFamily: 'monospace'),
                          ),
                          onPressed: () => _insertTag(tag.tag),
                        ),
                      );
                    }).toList(),
                  ),
                  const SizedBox(height: 8),
                  // Syntax hint
                  Text(
                    'Click a tag to insert it at the cursor. '
                    'Use {tag1(separator)tag2} for conditional separators, '
                    'e.g. {artist( - )title|slsk-filename}.',
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
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
