import 'dart:convert';

import 'package:flutter/material.dart';

import '../models.dart';
import '../scope.dart';
import '../widgets.dart';
import 'editor.dart';

/// Installed apps and games detected on the PC. Tap = launch, + = make a deck button (with the real icon).
class AppsScreen extends StatefulWidget {
  const AppsScreen({super.key});
  @override
  State<AppsScreen> createState() => _AppsScreenState();
}

class _AppsScreenState extends State<AppsScreen> {
  Future<List<Map<String, dynamic>>>? _f;
  String _q = '';
  String _kind = 'all';

  void _load({bool refresh = false}) {
    final conn = AppScope.of(context).conn;
    setState(() => _f = conn.apps(refresh: refresh));
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _f ??= AppScope.of(context).conn.connected ? AppScope.of(context).conn.apps() : null;
  }

  Future<void> _addButton(Map<String, dynamic> a) async {
    final app = AppScope.of(context).app;
    String? img;
    if (a['icon'] != null) img = await app.saveImageBytes(base64Decode(a['icon'] as String));
    final kind = a['kind'] as String;
    final b = DeckButton(
      label: a['name'] as String,
      icon: img != null ? 'image' : (kind == 'game' ? 'games' : 'apps'),
      imagePath: img,
      color1: kind == 'game' ? 0xFF00E676 : 0xFF7C4DFF,
      color2: kind == 'game' ? 0xFF1B5E20 : 0xFF1E90FF,
      action: ActionSpec('app', {'appId': a['id'], 'appName': a['name']}),
    );
    app.addButtonToFirstFree(b);
    if (mounted) toast(context, 'Added "${b.label}" to ${app.page.name}');
  }

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      if (!conn.connected) {
        return const EmptyState(icon: Icons.link_off, text: 'Connect to your PC to browse its apps and games.');
      }
      _f ??= conn.apps();
      return Column(children: [
        Row(children: [
          Expanded(
            child: TextField(
              decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Search apps and games'),
              onChanged: (v) => setState(() => _q = v.toLowerCase()),
            ),
          ),
          const SizedBox(width: 8),
          IconButton.filledTonal(onPressed: () => _load(refresh: true), icon: const Icon(Icons.refresh), tooltip: 'Rescan PC'),
        ]),
        const SizedBox(height: 8),
        Row(children: [
          for (final k in const ['all', 'app', 'game', 'store'])
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: ChoiceChip(
                label: Text(k == 'all' ? 'All' : k == 'app' ? 'Apps' : k == 'game' ? 'Games' : 'Store'),
                selected: _kind == k,
                selectedColor: Neon.purple,
                onSelected: (_) => setState(() => _kind = k),
              ),
            ),
        ]),
        const SizedBox(height: 8),
        Expanded(
          child: FutureBuilder<List<Map<String, dynamic>>>(
            future: _f,
            builder: (ctx, snap) {
              if (snap.connectionState != ConnectionState.done) {
                return const Center(child: Column(mainAxisSize: MainAxisSize.min, children: [CircularProgressIndicator(), SizedBox(height: 12), Text('Scanning installed appsâ€¦')]));
              }
              if (snap.hasError) return EmptyState(icon: Icons.error_outline, text: '${snap.error}');
              final list = snap.data!.where((a) => (a['name'] as String).toLowerCase().contains(_q) && (_kind == 'all' || a['kind'] == _kind)).toList();
              if (list.isEmpty) return const EmptyState(icon: Icons.search_off, text: 'No matching apps.');
              return ListView.builder(
                itemCount: list.length,
                itemBuilder: (_, i) {
                  final a = list[i];
                  return Card(
                    margin: const EdgeInsets.only(bottom: 6),
                    child: ListTile(
                      leading: AppIcon(base64Icon: a['icon'] as String?, kind: a['kind'] as String),
                      title: Text(a['name'] as String, maxLines: 1, overflow: TextOverflow.ellipsis),
                      subtitle: Text(a['kind'] == 'game' ? 'Steam game' : a['kind'] == 'store' ? 'Store app' : 'Desktop app', style: const TextStyle(color: Neon.muted)),
                      onTap: () async {
                        final r = await conn.run(ActionSpec('app', {'appId': a['id']}));
                        if (mounted) toast(context, r.ok ? 'Launching ${a['name']}â€¦' : (r.error ?? 'Failed'), error: !r.ok);
                      },
                      trailing: IconButton(icon: const Icon(Icons.add_box_outlined), tooltip: 'Add to deck', onPressed: () => _addButton(a)),
                    ),
                  );
                },
              );
            },
          ),
        ),
      ]);
    });
  }
}

class EmptyState extends StatelessWidget {
  const EmptyState({super.key, required this.icon, required this.text});
  final IconData icon;
  final String text;
  @override
  Widget build(BuildContext context) => Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(mainAxisSize: MainAxisSize.min, children: [Icon(icon, size: 48, color: Neon.muted), const SizedBox(height: 12), Text(text, textAlign: TextAlign.center, style: const TextStyle(color: Neon.muted))]),
        ),
      );
}


