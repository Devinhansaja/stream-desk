import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import '../models.dart';
import '../scope.dart';
import '../widgets.dart';
import 'apps_screen.dart';

/// Desktop wallpaper control: upload images from the phone to the PC, apply, rotate, add buttons.
class BackgroundsScreen extends StatefulWidget {
  const BackgroundsScreen({super.key});
  @override
  State<BackgroundsScreen> createState() => _BackgroundsScreenState();
}

class _BackgroundsScreenState extends State<BackgroundsScreen> {
  Future<List<Map<String, dynamic>>>? _f;
  bool _busy = false;

  void _reload() {
    final conn = AppScope.of(context).conn;
    setState(() => _f = conn.connected ? conn.wallpapers() : null);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final conn = AppScope.of(context).conn;
    _f ??= conn.connected ? conn.wallpapers() : null;
  }

  Future<void> _upload() async {
    final conn = AppScope.of(context).conn;
    final picks = await ImagePicker().pickMultiImage(maxWidth: 3840, maxHeight: 2160, imageQuality: 92);
    if (picks.isEmpty) return;
    setState(() => _busy = true);
    var failed = 0;
    for (var i = 0; i < picks.length; i++) {
      final bytes = await picks[i].readAsBytes();
      final r = await conn.uploadWallpaper(picks[i].name, bytes, apply: picks.length == 1);
      if (!r.ok) failed++;
    }
    if (!mounted) return;
    setState(() => _busy = false);
    toast(context, failed == 0 ? 'Uploaded ${picks.length} wallpaper${picks.length == 1 ? '' : 's'}' : '$failed upload(s) failed', error: failed > 0);
    _reload();
  }

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      if (!conn.connected) return const EmptyState(icon: Icons.link_off, text: 'Connect to your PC to change its wallpaper.');
      _f ??= conn.wallpapers();
      return Column(children: [
        Wrap(spacing: 8, runSpacing: 8, children: [
          FilledButton.icon(onPressed: _busy ? null : _upload, icon: const Icon(Icons.upload), label: const Text('Upload from phone')),
          OutlinedButton.icon(
            icon: const Icon(Icons.skip_next),
            label: const Text('Next'),
            onPressed: () async {
              final r = await conn.run(ActionSpec('wallpaper', {'mode': 'next'}));
              if (mounted) toast(context, r.ok ? 'Wallpaper changed' : (r.error ?? 'Failed'), error: !r.ok);
            },
          ),
          OutlinedButton.icon(
            icon: const Icon(Icons.shuffle),
            label: const Text('Random'),
            onPressed: () async {
              final r = await conn.run(ActionSpec('wallpaper', {'mode': 'random'}));
              if (mounted) toast(context, r.ok ? 'Wallpaper changed' : (r.error ?? 'Failed'), error: !r.ok);
            },
          ),
          OutlinedButton.icon(
            icon: const Icon(Icons.add_box_outlined),
            label: const Text('Add "Next BG" button'),
            onPressed: () {
              app.addButtonToFirstFree(DeckButton(label: 'Change BG', icon: 'image', color1: 0xFF1E90FF, color2: 0xFF00E676, action: ActionSpec('wallpaper', {'mode': 'next'})));
              toast(context, 'Added to ${app.page.name}');
            },
          ),
        ]),
        if (_busy) const Padding(padding: EdgeInsets.only(top: 10), child: LinearProgressIndicator()),
        const SizedBox(height: 12),
        Expanded(
          child: FutureBuilder<List<Map<String, dynamic>>>(
            future: _f,
            builder: (ctx, snap) {
              if (snap.connectionState != ConnectionState.done) return const Center(child: CircularProgressIndicator());
              if (snap.hasError) return EmptyState(icon: Icons.error_outline, text: '${snap.error}');
              final items = snap.data!;
              if (items.isEmpty) return const EmptyState(icon: Icons.photo_library, text: 'No wallpapers on the PC yet.\nUpload images from your phone.');
              return GridView.builder(
                gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(maxCrossAxisExtent: 220, mainAxisSpacing: 10, crossAxisSpacing: 10, childAspectRatio: 1.5),
                itemCount: items.length,
                itemBuilder: (_, i) {
                  final w = items[i];
                  final name = w['name'] as String;
                  return GestureDetector(
                    onTap: () async {
                      final r = await conn.run(ActionSpec('wallpaper', {'mode': 'specific', 'name': name}));
                      if (mounted) toast(context, r.ok ? 'Wallpaper set' : (r.error ?? 'Failed'), error: !r.ok);
                    },
                    onLongPress: () async {
                      final choice = await showModalBottomSheet<String>(
                        context: context,
                        backgroundColor: Neon.panel,
                        builder: (ctx) => Column(mainAxisSize: MainAxisSize.min, children: [
                          ListTile(leading: const Icon(Icons.add_box_outlined), title: const Text('Add button for this wallpaper'), onTap: () => Navigator.pop(ctx, 'add')),
                          ListTile(leading: const Icon(Icons.delete, color: Colors.redAccent), title: const Text('Delete from PC library'), onTap: () => Navigator.pop(ctx, 'del')),
                        ]),
                      );
                      if (choice == 'add') {
                        app.addButtonToFirstFree(DeckButton(label: name.split('.').first, icon: 'image', color1: 0xFF7C4DFF, color2: 0xFF1E90FF, action: ActionSpec('wallpaper', {'mode': 'specific', 'name': name})));
                        if (mounted) toast(context, 'Added to ${app.page.name}');
                      } else if (choice == 'del') {
                        await conn.deleteWallpaper(name);
                        _reload();
                      }
                    },
                    child: ClipRRect(
                      borderRadius: BorderRadius.circular(14),
                      child: Stack(fit: StackFit.expand, children: [
                        w['thumb'] != null ? Image.memory(base64Decode(w['thumb'] as String), fit: BoxFit.cover, gaplessPlayback: true) : Container(color: Neon.panel2),
                        Positioned(
                          left: 0,
                          right: 0,
                          bottom: 0,
                          child: Container(color: Colors.black54, padding: const EdgeInsets.all(6), child: Text(name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12))),
                        ),
                      ]),
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
