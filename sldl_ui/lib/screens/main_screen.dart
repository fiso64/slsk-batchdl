import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../providers/app_provider.dart';
import '../widgets/download_queue_widget.dart';
import '../widgets/input_panel_widget.dart';
import '../widgets/name_format_builder.dart';
import '../widgets/login_dialog.dart';
import 'settings_screen.dart';

class MainScreen extends StatefulWidget {
  const MainScreen({super.key});

  @override
  State<MainScreen> createState() => _MainScreenState();
}

class _MainScreenState extends State<MainScreen> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _initialize());
  }

  Future<void> _initialize() async {
    if (!mounted) return;
    final provider = context.read<AppProvider>();
    await provider.initialize();

    // Register callbacks for login re-prompt
    provider.onConnectionLost = () {
      if (mounted) {
        LoginDialog.show(context,
            isDismissible: true,
            errorMessage: 'Connection to Soulseek was lost. Please re-enter credentials.');
      }
    };

    // Show login dialog if no credentials are configured
    if (!provider.hasCredentials) {
      if (mounted) {
        LoginDialog.show(context, isDismissible: false);
      }
    }

    // Warn if sldl executable not found
    if (provider.sldlExecutablePath == null && mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: const Text(
              'sldl executable not found. Please set the path in Settings.'),
          action: SnackBarAction(
            label: 'Settings',
            onPressed: _openSettings,
          ),
          duration: const Duration(seconds: 8),
        ),
      );
    }
  }

  void _openSettings() {
    Navigator.push(
      context,
      MaterialPageRoute(builder: (_) => const SettingsScreen()),
    );
  }

  @override
  Widget build(BuildContext context) {
    final provider = context.watch<AppProvider>();
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Row(
          children: [
            Icon(Icons.headphones, size: 22),
            SizedBox(width: 8),
            Text('sldl UI'),
          ],
        ),
        actions: [
          // Connection status indicator
          _ConnectionStatusChip(status: provider.connectionStatus),
          const SizedBox(width: 4),
          // Settings button
          IconButton(
            icon: const Icon(Icons.settings_outlined),
            tooltip: 'Settings',
            onPressed: _openSettings,
          ),
          const SizedBox(width: 4),
        ],
      ),
      body: Column(
        children: [
          // Input area
          Container(
            color: theme.colorScheme.surfaceContainerLow,
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                InputPanelWidget(
                  onSubmit: (item) {
                    final p = context.read<AppProvider>();
                    if (!p.hasCredentials) {
                      LoginDialog.show(context, isDismissible: false);
                      return;
                    }
                    p.enqueue(item);
                  },
                ),
                const SizedBox(height: 12),
                // Name format builder lives on the main screen per requirements
                _NameFormatSection(),
              ],
            ),
          ),

          // Download queue
          Expanded(
            child: const DownloadQueueWidget(),
          ),
        ],
      ),
    );
  }
}

class _NameFormatSection extends StatefulWidget {
  @override
  State<_NameFormatSection> createState() => _NameFormatSectionState();
}

class _NameFormatSectionState extends State<_NameFormatSection> {
  bool _expanded = false;

  @override
  Widget build(BuildContext context) {
    final provider = context.read<AppProvider>();
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        InkWell(
          onTap: () => setState(() => _expanded = !_expanded),
          borderRadius: BorderRadius.circular(6),
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            child: Row(
              children: [
                Icon(Icons.label_outline,
                    size: 16, color: theme.colorScheme.onSurfaceVariant),
                const SizedBox(width: 6),
                Text('Name Format',
                    style: theme.textTheme.labelMedium
                        ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
                const SizedBox(width: 4),
                Icon(
                  _expanded
                      ? Icons.keyboard_arrow_up
                      : Icons.keyboard_arrow_down,
                  size: 16,
                  color: theme.colorScheme.onSurfaceVariant,
                ),
                if (provider.config.nameFormat != null &&
                    provider.config.nameFormat!.isNotEmpty) ...[
                  const SizedBox(width: 8),
                  Text(
                    provider.config.nameFormat!,
                    style: theme.textTheme.bodySmall?.copyWith(
                      fontFamily: 'monospace',
                      color: theme.colorScheme.primary,
                    ),
                  ),
                ],
              ],
            ),
          ),
        ),
        if (_expanded)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: NameFormatBuilder(
              initialValue: provider.config.nameFormat ?? '',
              label: 'Global Name Format',
              hint: 'e.g. {artist( - )title|slsk-filename}',
              onChanged: (value) {
                provider.config.nameFormat = value.isEmpty ? null : value;
              },
            ),
          ),
      ],
    );
  }
}

class _ConnectionStatusChip extends StatelessWidget {
  final ConnectionStatus status;
  const _ConnectionStatusChip({required this.status});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    final (label, color, icon) = switch (status) {
      ConnectionStatus.unknown => ('Not Connected', theme.colorScheme.onSurfaceVariant, Icons.radio_button_unchecked),
      ConnectionStatus.connecting => ('Connecting…', Colors.orange, Icons.sync),
      ConnectionStatus.connected => ('Connected', Colors.green, Icons.radio_button_checked),
      ConnectionStatus.failed => ('Login Failed', theme.colorScheme.error, Icons.error_outline),
    };

    return Tooltip(
      message: 'Soulseek connection status',
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: color.withOpacity(0.12),
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: color.withOpacity(0.3)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 12, color: color),
            const SizedBox(width: 4),
            Text(label, style: TextStyle(fontSize: 11, color: color)),
          ],
        ),
      ),
    );
  }
}
