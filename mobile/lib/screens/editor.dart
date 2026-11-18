import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import '../catalog.dart';
import '../models.dart';
import '../scope.dart';
import '../widgets.dart';

// ---------------------------------------------------------------------------
// Pickers that need the PC (installed apps, wallpapers)
// ---------------------------------------------------------------------------

/// Lets the user choose an installed app/game from the PC. Returns the app map {id,name,kind,icon}.
Future<Map<String, dynamic>?> pickApp(BuildContext context) {
  final conn = AppScope.of(context).conn;
  return showModalBottomSheet<Map<String, dynamic>>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Neon.panel,
    builder: (ctx) => _AppPickerSheet(load: () => conn.apps()),
  );
}

class _AppPickerSheet extends StatefulWidget {
  const _AppPickerSheet({required this.load});
  final Future<List<Map<String, dynamic>>> Function() load;
  @override
  State<_AppPickerSheet> createState() => _AppPickerSheetState();
}

class _AppPickerSheetState extends State<_AppPickerSheet> {
  late final Future<List<Map<String, dynamic>>> _f = widget.load();
  String _q = '';

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      height: MediaQuery.of(context).size.height * 0.85,
      child: Column(children: [
        Padding(
          padding: const EdgeInsets.all(12),
          child: TextField(
            decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Search apps and games on your PC'),
            onChanged: (v) => setState(() => _q = v.toLowerCase()),
          ),
        ),
        Expanded(
          child: FutureBuilder<List<Map<String, dynamic>>>(
            future: _f,
            builder: (ctx, snap) {
              if (snap.connectionState != ConnectionState.done) {
                return const Center(child: CircularProgressIndicator());
              }
              if (snap.hasError) {
                return Center(child: Padding(padding: const EdgeInsets.all(24), child: Text('${snap.error}', textAlign: TextAlign.center)));
              }
              final apps = snap.data!.where((a) => (a['name'] as String).toLowerCase().contains(_q)).toList();
              return ListView.builder(
                itemCount: apps.length,
                itemBuilder: (_, i) {
                  final a = apps[i];
                  return ListTile(
                    leading: AppIcon(base64Icon: a['icon'] as String?, kind: a['kind'] as String),
                    title: Text(a['name'] as String),
                    subtitle: Text(a['kind'] == 'game' ? 'Steam game' : a['kind'] == 'store' ? 'Store app' : 'Desktop app'),
                    onTap: () => Navigator.pop(context, a),
                  );
                },
              );
            },
          ),
        ),
      ]),
    );
  }
}

class AppIcon extends StatelessWidget {
  const AppIcon({super.key, this.base64Icon, required this.kind, this.size = 40});
  final String? base64Icon;
  final String kind;
  final double size;
  @override
  Widget build(BuildContext context) {
    if (base64Icon != null) {
      return Image.memory(base64Decode(base64Icon!), width: size, height: size, gaplessPlayback: true);
    }
    return Icon(kind == 'game' ? Icons.sports_esports : Icons.apps, size: size * 0.8, color: Neon.purple);
  }
}

Future<String?> pickWallpaperName(BuildContext context) async {
  final conn = AppScope.of(context).conn;
  List<Map<String, dynamic>> items;
  try {
    items = await conn.wallpapers();
  } catch (e) {
    toast(context, '$e', error: true);
    return null;
  }
  if (items.isEmpty) {
    toast(context, 'No wallpapers on the PC yet. Upload some in Backgrounds.', error: true);
    return null;
  }
  return showModalBottomSheet<String>(
    context: context,
    backgroundColor: Neon.panel,
    builder: (ctx) => ListView(
      children: [
        for (final w in items)
          ListTile(
            leading: w['thumb'] != null ? Image.memory(base64Decode(w['thumb'] as String), width: 64, fit: BoxFit.cover) : const Icon(Icons.image),
            title: Text(w['name'] as String),
            onTap: () => Navigator.pop(ctx, w['name'] as String),
          ),
      ],
    ),
  );
}

// ---------------------------------------------------------------------------
// Action editor: a form generated from the action catalog
// ---------------------------------------------------------------------------

class ActionForm extends StatefulWidget {
  const ActionForm({super.key, required this.spec, required this.onChanged, this.allowMacro = true});
  final ActionSpec spec;
  final VoidCallback onChanged;
  final bool allowMacro;
  @override
  State<ActionForm> createState() => _ActionFormState();
}

class _ActionFormState extends State<ActionForm> {
  final Map<String, TextEditingController> _ctl = {};

  TextEditingController _c(String key, dynamic value) =>
      _ctl.putIfAbsent(key, () => TextEditingController(text: value == null ? '' : '$value'));

  @override
  void dispose() {
    for (final c in _ctl.values) {
      c.dispose();
    }
    super.dispose();
  }

  void _setType(String type) {
    final def = actionDef(type)!;
    setState(() {
      widget.spec.type = type;
      widget.spec.params = defaultParams(def);
      for (final c in _ctl.values) {
        c.dispose();
      }
      _ctl.clear();
    });
    widget.onChanged();
  }

  @override
  Widget build(BuildContext context) {
    final app = AppScope.of(context).app;
    final spec = widget.spec;
    final def = actionDef(spec.type) ?? kActions.first;
    final cats = <String, List<ActionDef>>{};
    for (final a in kActions) {
      if (!widget.allowMacro && (a.type == 'macro')) continue;
      cats.putIfAbsent(a.category, () => []).add(a);
    }
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      DropdownButtonFormField<String>(
        value: def.type,
        isExpanded: true,
        decoration: const InputDecoration(labelText: 'Action on PC'),
        items: [
          for (final e in cats.entries) ...[
            DropdownMenuItem<String>(enabled: false, value: '_${e.key}', child: Text(e.key.toUpperCase(), style: const TextStyle(color: Neon.purple, fontSize: 12, fontWeight: FontWeight.bold))),
            for (final a in e.value) DropdownMenuItem(value: a.type, child: Row(children: [Icon(a.icon, size: 18), const SizedBox(width: 10), Flexible(child: Text(a.label, overflow: TextOverflow.ellipsis))])),
          ]
        ],
        onChanged: (v) {
          if (v != null && !v.startsWith('_')) _setType(v);
        },
      ),
      for (final p in def.params) ...[
        const SizedBox(height: 12),
        _field(context, app, spec, p),
      ],
      if (def.dangerous)
        const Padding(
          padding: EdgeInsets.only(top: 10),
          child: Text('This action changes the power state or runs commands. Consider enabling "Ask before running".', style: TextStyle(color: Colors.orangeAccent, fontSize: 12)),
        ),
    ]);
  }

  Widget _field(BuildContext context, dynamic app, ActionSpec spec, P p) {
    final v = spec.params[p.key];
    switch (p.kind) {
      case PK.text:
        return TextField(
          controller: _c(p.key, v),
          decoration: InputDecoration(labelText: p.label, hintText: p.hint),
          onChanged: (t) {
            spec.params[p.key] = t;
            widget.onChanged();
          },
        );
      case PK.number:
        return TextField(
          controller: _c(p.key, v),
          keyboardType: TextInputType.number,
          decoration: InputDecoration(labelText: p.label),
          onChanged: (t) {
            final n = int.tryParse(t);
            if (n != null) spec.params[p.key] = n;
            widget.onChanged();
          },
        );
      case PK.choice:
        return DropdownButtonFormField<String>(
          value: p.options.contains(v) ? v as String : p.options.first,
          decoration: InputDecoration(labelText: p.label),
          items: [for (final o in p.options) DropdownMenuItem(value: o, child: Text(o))],
          onChanged: (x) {
            setState(() => spec.params[p.key] = x);
            widget.onChanged();
          },
        );
      case PK.app:
        return OutlinedButton.icon(
          icon: const Icon(Icons.apps),
          label: Align(alignment: Alignment.centerLeft, child: Text(spec.params['appName'] != null ? '${spec.params['appName']}' : 'Choose app or game from PC')),
          style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52), alignment: Alignment.centerLeft),
          onPressed: () async {
            final a = await pickApp(context);
            if (a != null) {
              setState(() {
                spec.params['appId'] = a['id'];
                spec.params['appName'] = a['name'];
              });
              widget.onChanged();
            }
          },
        );
      case PK.macro:
        final macros = (app.macros as List<Macro>);
        return DropdownButtonFormField<String>(
          value: macros.any((m) => m.id == v) ? v as String : null,
          decoration: InputDecoration(labelText: p.label),
          items: [for (final m in macros) DropdownMenuItem(value: m.id, child: Text(m.name))],
          onChanged: (x) {
            setState(() => spec.params[p.key] = x);
            widget.onChanged();
          },
        );
      case PK.wallpaper:
        return OutlinedButton.icon(
          icon: const Icon(Icons.image),
          label: Align(alignment: Alignment.centerLeft, child: Text(v != null && '$v'.isNotEmpty ? '$v' : p.label)),
          style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52), alignment: Alignment.centerLeft),
          onPressed: () async {
            final n = await pickWallpaperName(context);
            if (n != null) {
              setState(() => spec.params[p.key] = n);
              widget.onChanged();
            }
          },
        );
    }
  }
}

// ---------------------------------------------------------------------------
// Button editor
// ---------------------------------------------------------------------------

class ButtonEditorScreen extends StatefulWidget {
  const ButtonEditorScreen({super.key, required this.button, this.isNew = false});
  final DeckButton button;
  final bool isNew;
  @override
  State<ButtonEditorScreen> createState() => _ButtonEditorScreenState();
}

class _ButtonEditorScreenState extends State<ButtonEditorScreen> {
  late DeckButton b = DeckButton.fromJson(widget.button.toJson()); // edit a copy
  late final TextEditingController _label = TextEditingController(text: b.label);
  String _iconQuery = '';

  @override
  void dispose() {
    _label.dispose();
    super.dispose();
  }

  Future<void> _pickImage() async {
    final app = AppScope.of(context).app;
    final x = await ImagePicker().pickImage(source: ImageSource.gallery, maxWidth: 256, maxHeight: 256);
    if (x == null) return;
    final saved = await app.copyImage(x.path);
    setState(() {
      b.icon = 'image';
      b.imagePath = saved;
    });
  }

  Future<void> _glyph() async {
    final c = TextEditingController(text: b.icon.startsWith('t:') ? b.icon.substring(2) : '');
    final r = await showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Text or emoji icon'),
        content: TextField(controller: c, maxLength: 3, autofocus: true, decoration: const InputDecoration(hintText: 'e.g. 🎮 or OBS')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(ctx, c.text), child: const Text('Use')),
        ],
      ),
    );
    if (r != null && r.isNotEmpty) setState(() => b.icon = 't:$r');
  }

  Widget _colorRow(String title, int value, ValueChanged<int> on) {
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Text(title, style: const TextStyle(color: Neon.muted)),
      const SizedBox(height: 6),
      Wrap(spacing: 8, runSpacing: 8, children: [
        for (final c in kPalette)
          GestureDetector(
            onTap: () => setState(() => on(c)),
            child: Container(
              width: 34,
              height: 34,
              decoration: BoxDecoration(
                color: Color(c),
                shape: BoxShape.circle,
                border: Border.all(color: value == c ? Colors.white : Colors.transparent, width: 2.5),
                boxShadow: [BoxShadow(color: Color(c).withAlpha(120), blurRadius: 8)],
              ),
            ),
          ),
        GestureDetector(
          onTap: () async {
            final r = await pickColor(context, Color(value));
            if (r != null) setState(() => on(r.value));
          },
          child: Container(
            width: 34,
            height: 34,
            decoration: BoxDecoration(shape: BoxShape.circle, border: Border.all(color: Neon.muted), gradient: const SweepGradient(colors: [Colors.red, Colors.yellow, Colors.green, Colors.cyan, Colors.blue, Colors.purple, Colors.red])),
          ),
        ),
      ]),
    ]);
  }

  @override
  Widget build(BuildContext context) {
    final scope = AppScope.of(context);
    final icons = kIcons.keys.where((k) => k.contains(_iconQuery.toLowerCase())).toList();
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.isNew ? 'New button' : 'Edit button'),
        actions: [
          IconButton(
            tooltip: 'Test on PC',
            icon: const Icon(Icons.play_arrow),
            onPressed: () async {
              final r = await scope.conn.run(b.action);
              if (mounted) toast(context, r.ok ? (r.message ?? 'Done') : (r.error ?? 'Failed'), error: !r.ok);
            },
          ),
        ],
      ),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        Center(child: SizedBox(width: 130, height: 130, child: NeonKey(button: b, onTap: () {}))),
        const SizedBox(height: 18),
        TextField(
          controller: _label,
          decoration: const InputDecoration(labelText: 'Label'),
          onChanged: (v) => setState(() => b.label = v),
        ),
        const SizedBox(height: 18),
        const Text('Icon', style: TextStyle(color: Neon.muted)),
        const SizedBox(height: 6),
        Row(children: [
          Expanded(child: OutlinedButton.icon(onPressed: _pickImage, icon: const Icon(Icons.photo), label: const Text('Image'))),
          const SizedBox(width: 8),
          Expanded(child: OutlinedButton.icon(onPressed: _glyph, icon: const Icon(Icons.emoji_emotions), label: const Text('Text / emoji'))),
        ]),
        const SizedBox(height: 8),
        TextField(decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Search icons'), onChanged: (v) => setState(() => _iconQuery = v)),
        const SizedBox(height: 8),
        SizedBox(
          height: 150,
          child: GridView.builder(
            gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(maxCrossAxisExtent: 52, mainAxisSpacing: 6, crossAxisSpacing: 6),
            itemCount: icons.length,
            itemBuilder: (_, i) {
              final k = icons[i];
              final sel = b.icon == k;
              return GestureDetector(
                onTap: () => setState(() => b.icon = k),
                child: Container(
                  decoration: BoxDecoration(color: sel ? Neon.purple.withAlpha(90) : Neon.panel2, borderRadius: BorderRadius.circular(10), border: Border.all(color: sel ? Neon.purple : Colors.transparent)),
                  child: Icon(kIcons[k], size: 24),
                ),
              );
            },
          ),
        ),
        const SizedBox(height: 18),
        _colorRow('Border / gradient start', b.color1, (c) => b.color1 = c),
        const SizedBox(height: 14),
        _colorRow('Gradient end', b.color2, (c) => b.color2 = c),
        const SizedBox(height: 14),
        Row(children: [
          const Text('Glow', style: TextStyle(color: Neon.muted)),
          Expanded(child: Slider(value: b.glow, onChanged: (v) => setState(() => b.glow = v))),
        ]),
        const Divider(height: 32),
        ActionForm(spec: b.action, onChanged: () => setState(() {})),
        const SizedBox(height: 8),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Ask before running'),
          subtitle: const Text('Shows a confirmation, useful for shutdown or restart'),
          value: b.confirm,
          onChanged: (v) => setState(() => b.confirm = v),
        ),
        const SizedBox(height: 16),
        Row(children: [
          if (!widget.isNew)
            Expanded(
              child: OutlinedButton.icon(
                style: OutlinedButton.styleFrom(foregroundColor: Colors.redAccent),
                icon: const Icon(Icons.delete),
                label: const Text('Remove'),
                onPressed: () => Navigator.pop(context, const _Remove()),
              ),
            ),
          if (!widget.isNew) const SizedBox(width: 12),
          Expanded(
            flex: 2,
            child: FilledButton.icon(
              icon: const Icon(Icons.check),
              label: const Text('Save'),
              onPressed: () {
                if (b.label.trim().isEmpty) b.label = actionDef(b.action.type)?.label ?? 'Button';
                Navigator.pop(context, b);
              },
            ),
          ),
        ]),
      ]),
    );
  }
}

/// Returned by the editor when the user chooses Remove.
class _Remove {
  const _Remove();
}

bool isRemove(Object? o) => o is _Remove;
