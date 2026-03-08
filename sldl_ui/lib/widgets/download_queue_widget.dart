import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../models/download_item.dart';
import '../providers/app_provider.dart';

class DownloadQueueWidget extends StatelessWidget {
  const DownloadQueueWidget({super.key});

  @override
  Widget build(BuildContext context) {
    final provider = context.watch<AppProvider>();
    final queue = provider.queue;

    if (queue.isEmpty) {
      return Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(Icons.download_done_outlined,
                size: 56, color: Theme.of(context).colorScheme.outlineVariant),
            const SizedBox(height: 12),
            Text('No downloads yet',
                style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    )),
            const SizedBox(height: 4),
            Text('Add an input above to start downloading.',
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    )),
          ],
        ),
      );
    }

    return Column(
      children: [
        // Header row
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
          child: Row(
            children: [
              Text(
                '${queue.length} download${queue.length == 1 ? '' : 's'}',
                style: Theme.of(context).textTheme.titleSmall,
              ),
              const Spacer(),
              TextButton.icon(
                icon: const Icon(Icons.clear_all, size: 16),
                label: const Text('Clear Completed'),
                onPressed: queue.any((i) =>
                        i.status == DownloadStatus.succeeded ||
                        i.status == DownloadStatus.failed ||
                        i.status == DownloadStatus.cancelled)
                    ? () => provider.clearCompleted()
                    : null,
              ),
            ],
          ),
        ),
        Expanded(
          child: ListView.builder(
            padding: const EdgeInsets.symmetric(horizontal: 8),
            itemCount: queue.length,
            itemBuilder: (context, index) {
              final item = queue[index];
              return _DownloadItemCard(item: item);
            },
          ),
        ),
      ],
    );
  }
}

class _DownloadItemCard extends StatefulWidget {
  final DownloadItem item;
  const _DownloadItemCard({required this.item});

  @override
  State<_DownloadItemCard> createState() => _DownloadItemCardState();
}

class _DownloadItemCardState extends State<_DownloadItemCard> {
  bool _showLog = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final item = widget.item;
    final provider = context.read<AppProvider>();

    Color statusColor = _statusColor(theme, item.status);
    IconData statusIcon = _statusIcon(item.status);

    return Card(
      margin: const EdgeInsets.only(bottom: 6),
      elevation: item.status == DownloadStatus.running ? 2 : 0,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(10),
        side: BorderSide(
          color: item.status == DownloadStatus.running
              ? theme.colorScheme.primary.withOpacity(0.4)
              : theme.colorScheme.outlineVariant,
          width: item.status == DownloadStatus.running ? 1.5 : 1,
        ),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Title row
            Row(
              children: [
                // Mode badges
                if (item.albumMode) ...[
                  _badge(context, 'A', Colors.blue),
                  const SizedBox(width: 4),
                ],
                if (item.aggregateMode) ...[
                  _badge(context, 'G', Colors.purple),
                  const SizedBox(width: 4),
                ],
                // Status icon
                Icon(statusIcon, size: 16, color: statusColor),
                const SizedBox(width: 8),
                // Display name
                Expanded(
                  child: Text(
                    item.displayName,
                    style: theme.textTheme.bodyMedium
                        ?.copyWith(fontWeight: FontWeight.w600),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                const SizedBox(width: 8),
                // Status label
                Text(
                  item.statusLabel,
                  style: theme.textTheme.bodySmall?.copyWith(color: statusColor),
                ),
                // Cancel/Remove button
                IconButton(
                  icon: Icon(
                    item.status == DownloadStatus.running
                        ? Icons.cancel_outlined
                        : Icons.close,
                    size: 18,
                  ),
                  onPressed: () => provider.removeFromQueue(item.id),
                  tooltip: item.status == DownloadStatus.running
                      ? 'Cancel download'
                      : 'Remove',
                  visualDensity: VisualDensity.compact,
                ),
              ],
            ),

            // Input type + stats
            Padding(
              padding: const EdgeInsets.only(left: 24),
              child: Row(
                children: [
                  if (item.inputType != InputType.auto)
                    Text(
                      item.inputType.label,
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                    ),
                  if (item.totalCount > 0) ...[
                    const SizedBox(width: 12),
                    Icon(Icons.check_circle_outline,
                        size: 12, color: Colors.green[600]),
                    const SizedBox(width: 2),
                    Text('${item.succeededCount}',
                        style: const TextStyle(fontSize: 12)),
                    const SizedBox(width: 6),
                    Icon(Icons.cancel_outlined,
                        size: 12, color: theme.colorScheme.error),
                    const SizedBox(width: 2),
                    Text('${item.failedCount}',
                        style: const TextStyle(fontSize: 12)),
                    const SizedBox(width: 6),
                    Text('/ ${item.totalCount}',
                        style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant)),
                  ],
                ],
              ),
            ),

            // Progress bar
            if (item.status == DownloadStatus.running) ...[
              const SizedBox(height: 6),
              LinearProgressIndicator(
                value: item.progress > 0 ? item.progress : null,
                borderRadius: BorderRadius.circular(2),
              ),
            ],

            // Log toggle
            if (item.logLines.isNotEmpty) ...[
              const SizedBox(height: 4),
              InkWell(
                onTap: () => setState(() => _showLog = !_showLog),
                borderRadius: BorderRadius.circular(4),
                child: Padding(
                  padding: const EdgeInsets.symmetric(vertical: 2),
                  child: Row(
                    children: [
                      const SizedBox(width: 24),
                      Icon(
                        _showLog
                            ? Icons.keyboard_arrow_up
                            : Icons.keyboard_arrow_down,
                        size: 14,
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                      const SizedBox(width: 2),
                      Text(
                        _showLog ? 'Hide log' : 'Show log',
                        style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant),
                      ),
                    ],
                  ),
                ),
              ),
              if (_showLog)
                Container(
                  margin: const EdgeInsets.only(top: 4, left: 24),
                  padding: const EdgeInsets.all(8),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.surfaceContainerHighest,
                    borderRadius: BorderRadius.circular(6),
                  ),
                  constraints: const BoxConstraints(maxHeight: 160),
                  child: SingleChildScrollView(
                    reverse: true,
                    child: SelectableText(
                      item.logLines.join('\n'),
                      style: theme.textTheme.bodySmall?.copyWith(
                        fontFamily: 'monospace',
                        fontSize: 11,
                      ),
                    ),
                  ),
                ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _badge(BuildContext context, String label, Color color) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 1),
      decoration: BoxDecoration(
        color: color.withOpacity(0.15),
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: color.withOpacity(0.4)),
      ),
      child: Text(
        label,
        style: TextStyle(fontSize: 10, color: color, fontWeight: FontWeight.bold),
      ),
    );
  }

  Color _statusColor(ThemeData theme, DownloadStatus status) {
    switch (status) {
      case DownloadStatus.queued:
        return theme.colorScheme.onSurfaceVariant;
      case DownloadStatus.running:
        return theme.colorScheme.primary;
      case DownloadStatus.succeeded:
        return Colors.green[600]!;
      case DownloadStatus.failed:
        return theme.colorScheme.error;
      case DownloadStatus.cancelled:
        return theme.colorScheme.onSurfaceVariant;
    }
  }

  IconData _statusIcon(DownloadStatus status) {
    switch (status) {
      case DownloadStatus.queued:
        return Icons.schedule;
      case DownloadStatus.running:
        return Icons.downloading;
      case DownloadStatus.succeeded:
        return Icons.check_circle_outline;
      case DownloadStatus.failed:
        return Icons.error_outline;
      case DownloadStatus.cancelled:
        return Icons.cancel_outlined;
    }
  }
}
