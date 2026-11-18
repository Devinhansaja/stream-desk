import 'package:flutter/material.dart';

import '../catalog.dart';
import '../models.dart';
import '../scope.dart';
import '../widgets.dart';
import 'apps_screen.dart';
import 'editor.dart';
import 'home.dart' show askText;

class MacrosScreen extends StatelessWidget {
  const MacrosScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      return Column(children: [
        Row(children: [
          const Expanded(child: Text('Macros run several actions in order on your PC.', style: TextStyle(color: Neon.muted))),
          FilledButton.icon(
            icon: const Icon(Icons.add),
            label: const Text('New macro'),
            onPressed: () async {
              final name = await askText(context, 'New macro', 'Macro name');
              if (name == null || name.trim().isEmpty || !context.mounted) return;
              final m = Macro(name: name.trim(), steps: [ActionSpec('hotkey', {'keys': 'win+d'})]);
              app.upsertMacro(m);
              Navigator.push(context, MaterialPageRoute(builder: (_) => MacroEditorScreen(macroId: m.id)));
            },
          ),
        ]),
        const SizedBox(height: 10),
        Expanded(
          child: app.macros.isEmpty
              ? const EmptyState(icon: Icons.bolt, text: 'No macros yet. Create one to chain volume, shortcuts, apps and more.')
              : ListView(children: [
                  for (final m in app.macros)
                    Card(
                      margin: const EdgeInsets.only(bottom: 8),
                      child: ListTile(
                        leading: const Icon(Icons.bolt, color: Colors.amber),
                        title: Text(m.name),
                        subtitle: Text('${m.steps.length} step${m.steps.length == 1 ? '' : 's'}', style: const TextStyle(color: Neon.muted)),
                        onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => MacroEditorScreen(macroId: m.id))),
                        trailing: Row(mainAxisSize: MainAxisSize.min, children: [
                          IconButton(
                            icon: const Icon(Icons.play_arrow, color: Neon.green),
                            tooltip: 'Run now',
                            onPressed: () async {
                              final r = await conn.run(ActionSpec('macro', {'macroId': m.id}));
                              if (context.mounted) toast(context, r.ok ? 'Macro finished' : (r.error ?? 'Failed'), error: !r.ok);
                            },
                          ),
                          IconButton(
                            icon: const Icon(Icons.add_box_outlined),
                            tooltip: 'Add to deck',
                            onPressed: () {
                              app.addButtonToFirstFree(DeckButton(label: m.name, icon: 'bolt', color1: 0xFFFFC400, color2: 0xFFFF6D00, action: ActionSpec('macro', {'macroId': m.id})));
                              toast(context, 'Added "${m.name}" to ${app.page.name}');
                            },
                          ),
                        ]),
                      ),
                    ),
                ]),
        ),
      ]);
    });
  }
}

class MacroEditorScreen extends StatefulWidget {
  const MacroEditorScreen({super.key, required this.macroId});
  final String macroId;
  @override
  State<MacroEditorScreen> createState() => _MacroEditorScreenState();
}

class _MacroEditorScreenState extends State<MacroEditorScreen> {
  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      final idx = app.macros.indexWhere((m) => m.id == widget.macroId);
      if (idx < 0) return const Scaffold(body: Center(child: Text('Macro deleted')));
      final m = app.macros[idx];
      return Scaffold(
        appBar: AppBar(
          title: Text(m.name),
          actions: [
            IconButton(
              icon: const Icon(Icons.edit),
              onPressed: () async {
                final n = await askText(context, 'Rename macro', 'Name', initial: m.name);
                if (n != null && n.trim().isNotEmpty) {
                  m.name = n.trim();
                  app.changed();
                }
              },
            ),
            IconButton(
              icon: const Icon(Icons.play_arrow, color: Neon.green),
              onPressed: () async {
                final r = await conn.run(ActionSpec('macro', {'macroId': m.id}));
                if (context.mounted) toast(context, r.ok ? 'Macro finished' : (r.error ?? 'Failed'), error: !r.ok);
              },
            ),
            IconButton(
              icon: const Icon(Icons.delete, color: Colors.redAccent),
              onPressed: () {
                app.deleteMacro(m.id);
                Navigator.pop(context);
              },
            ),
          ],
        ),
        floatingActionButton: FloatingActionButton.extended(
          icon: const Icon(Icons.add),
          label: const Text('Add step'),
          onPressed: () {
            m.steps.add(ActionSpec('delay', {'ms': 500}));
            app.changed();
          },
        ),
        body: m.steps.isEmpty
            ? const EmptyState(icon: Icons.playlist_add, text: 'Add steps. They run top to bottom.')
            : ReorderableListView.builder(
                padding: const EdgeInsets.fromLTRB(12, 12, 12, 100),
                itemCount: m.steps.length,
                onReorder: (a, b) {
                  if (b > a) b--;
                  final s = m.steps.removeAt(a);
                  m.steps.insert(b, s);
                  app.changed();
                },
                itemBuilder: (ctx, i) {
                  final s = m.steps[i];
                  final d = actionDef(s.type);
                  return Card(
                    key: ValueKey('${m.id}-$i-${s.type}-${identityHashCode(s)}'),
                    margin: const EdgeInsets.only(bottom: 8),
                    child: Padding(
                      padding: const EdgeInsets.all(12),
                      child: Column(children: [
                        Row(children: [
                          CircleAvatar(radius: 13, backgroundColor: Neon.purple, child: Text('${i + 1}', style: const TextStyle(fontSize: 12))),
                          const SizedBox(width: 10),
                          Icon(d?.icon ?? Icons.bolt, size: 18),
                          const SizedBox(width: 8),
                          Expanded(child: Text(d?.label ?? s.type, style: const TextStyle(fontWeight: FontWeight.bold))),
                          IconButton(
                            icon: const Icon(Icons.close, size: 18),
                            onPressed: () {
                              m.steps.removeAt(i);
                              app.changed();
                            },
                          ),
                          ReorderableDragStartListener(index: i, child: const Icon(Icons.drag_handle)),
                        ]),
                        const SizedBox(height: 8),
                        ActionForm(spec: s, allowMacro: false, onChanged: app.changed),
                      ]),
                    ),
                  );
                },
              ),
      );
    });
  }
}
