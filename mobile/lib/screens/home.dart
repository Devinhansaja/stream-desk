import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../connection.dart';
import '../models.dart';
import '../scope.dart';
import '../store.dart';
import '../widgets.dart';
import 'apps_screen.dart';
import 'backgrounds_screen.dart';
import 'editor.dart';
import 'macros_screen.dart';
import 'pair_screen.dart';
import 'settings_screen.dart';
import 'system_screen.dart';

enum Section { deck, apps, system, macros, backgrounds, settings }

const _sectionInfo = <Section, (String, IconData)>{
  Section.deck: ('Stream Deck', Icons.grid_view_rounded),
  Section.apps: ('Apps', Icons.apps),
  Section.system: ('System Controls', Icons.settings),
  Section.macros: ('Macros', Icons.bolt),
  Section.backgrounds: ('Backgrounds', Icons.image),
  Section.settings: ('Settings', Icons.tune),
};

class HomeShell extends StatefulWidget {
  const HomeShell({super.key});
  @override
  State<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends State<HomeShell> {
  Section section = Section.deck;
  bool fullscreen = false;

  void go(Section s) => setState(() => section = s);

  void _toggleFullscreen() {
    setState(() => fullscreen = !fullscreen);
    SystemChrome.setEnabledSystemUIMode(fullscreen ? SystemUiMode.immersiveSticky : SystemUiMode.edgeToEdge);
  }

  Widget _body() {
    switch (section) {
      case Section.deck:
        return DeckView(onNavigate: go);
      case Section.apps:
        return const AppsScreen();
      case Section.system:
        return const SystemScreen();
      case Section.macros:
        return const MacrosScreen();
      case Section.backgrounds:
        return const BackgroundsScreen();
      case Section.settings:
        return const SettingsScreen();
    }
  }

  @override
  Widget build(BuildContext context) {
    final wide = MediaQuery.of(context).size.width >= 820;
    return Scaffold(
      body: SafeArea(
        child: Column(children: [
          _Header(onSettings: () => go(Section.settings), onFullscreen: _toggleFullscreen, fullscreen: fullscreen, compact: !wide),
          Expanded(
            child: wide
                ? Row(children: [
                    _Sidebar(current: section, onSelect: go),
                    Expanded(child: Padding(padding: const EdgeInsets.fromLTRB(0, 0, 12, 12), child: _body())),
                  ])
                : Padding(padding: const EdgeInsets.fromLTRB(10, 0, 10, 0), child: _body()),
          ),
        ]),
      ),
      bottomNavigationBar: wide
          ? null
          : NavigationBar(
              backgroundColor: Neon.panel,
              indicatorColor: Neon.purple.withAlpha(110),
              height: 64,
              selectedIndex: section.index,
              labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
              onDestinationSelected: (i) => go(Section.values[i]),
              destinations: [
                for (final s in Section.values)
                  NavigationDestination(icon: Icon(_sectionInfo[s]!.$2), label: _sectionInfo[s]!.$1.split(' ').first),
              ],
            ),
    );
  }
}

// ---------------------------------------------------------------------------
// Header + sidebar
// ---------------------------------------------------------------------------

class _Header extends StatelessWidget {
  const _Header({required this.onSettings, required this.onFullscreen, required this.fullscreen, required this.compact});
  final VoidCallback onSettings, onFullscreen;
  final bool fullscreen, compact;

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      final ok = conn.connected;
      final connecting = conn.state == LinkState.connecting;
      final color = ok ? Neon.green : (connecting ? Colors.amber : Colors.redAccent);
      final label = ok ? 'Connected' : (connecting ? 'Connecting…' : (app.activePc == null ? 'Not paired' : 'Offline'));
      final pcName = ok ? (conn.hostName ?? 'PC') : (app.activePc?.name ?? 'No PC');
      final via = ok ? (conn.isUsb ? 'USB' : 'Wi-Fi') : '';
      return Container(
        padding: const EdgeInsets.fromLTRB(14, 8, 10, 8),
        child: Row(children: [
          Container(
            width: 40,
            height: 40,
            decoration: BoxDecoration(borderRadius: BorderRadius.circular(12), gradient: const LinearGradient(colors: [Neon.purple, Neon.blue])),
            child: const Icon(Icons.play_arrow_rounded, color: Colors.white, size: 30),
          ),
          const SizedBox(width: 10),
          Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Text('StreamDeck', style: TextStyle(fontSize: 20, fontWeight: FontWeight.bold, height: 1.05)),
            PopupMenuButton<String>(
              padding: EdgeInsets.zero,
              color: Neon.panel2,
              onSelected: (v) async {
                if (v == '_new') {
                  final n = await askText(context, 'New profile', 'Profile name');
                  if (n != null && n.trim().isNotEmpty) app.addProfile(n.trim());
                } else {
                  app.selectProfile(v);
                }
              },
              itemBuilder: (_) => [
                for (final p in app.profiles)
                  PopupMenuItem(value: p.id, child: Row(children: [Icon(p.id == app.currentProfileId ? Icons.check : Icons.circle_outlined, size: 16), const SizedBox(width: 8), Text(p.name)])),
                const PopupMenuItem(value: '_new', child: Row(children: [Icon(Icons.add, size: 16), SizedBox(width: 8), Text('New profile')])),
              ],
              child: Row(mainAxisSize: MainAxisSize.min, children: [
                Text(app.profile.name, style: const TextStyle(color: Neon.purple, fontSize: 13)),
                const Icon(Icons.arrow_drop_down, color: Neon.purple, size: 18),
              ]),
            ),
          ]),
          const Spacer(),
          GestureDetector(
            onTap: () {
              if (app.activePc == null) {
                Navigator.push(context, MaterialPageRoute(builder: (_) => const PairScreen()));
              } else {
                AppScope.of(context).conn.nudge();
              }
            },
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 7),
              decoration: BoxDecoration(color: Neon.panel, borderRadius: BorderRadius.circular(30), border: Border.all(color: Neon.purple.withAlpha(110))),
              child: Row(mainAxisSize: MainAxisSize.min, children: [
                Icon(Icons.circle, size: 10, color: color),
                const SizedBox(width: 8),
                Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
                  Text(label, style: TextStyle(color: color, fontWeight: FontWeight.bold, fontSize: 13, height: 1.1)),
                  if (!compact || true) Text(via.isEmpty ? pcName : '$pcName · $via', style: const TextStyle(fontSize: 11, color: Neon.muted, height: 1.1)),
                ]),
              ]),
            ),
          ),
          if (!compact) IconButton(onPressed: onSettings, icon: const Icon(Icons.settings)),
          IconButton(onPressed: onFullscreen, icon: Icon(fullscreen ? Icons.fullscreen_exit : Icons.fullscreen)),
        ]),
      );
    });
  }
}

Future<String?> askText(BuildContext context, String title, String hint, {String initial = ''}) {
  final c = TextEditingController(text: initial);
  return showDialog<String>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(title),
      content: TextField(controller: c, autofocus: true, decoration: InputDecoration(hintText: hint), onSubmitted: (v) => Navigator.pop(ctx, v)),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
        FilledButton(onPressed: () => Navigator.pop(ctx, c.text), child: const Text('OK')),
      ],
    ),
  );
}

class _Sidebar extends StatelessWidget {
  const _Sidebar({required this.current, required this.onSelect});
  final Section current;
  final ValueChanged<Section> onSelect;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 220,
      margin: const EdgeInsets.fromLTRB(10, 0, 10, 12),
      child: Column(children: [
        for (final s in Section.values)
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: InkWell(
              borderRadius: BorderRadius.circular(14),
              onTap: () => onSelect(s),
              child: Container(
                height: 50,
                padding: const EdgeInsets.symmetric(horizontal: 14),
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(14),
                  gradient: s == current ? const LinearGradient(colors: [Color(0xFF5B34E0), Color(0xFF2A1B80)]) : null,
                  border: s == current ? Border.all(color: Neon.purple) : null,
                ),
                child: Row(children: [
                  Icon(_sectionInfo[s]!.$2, color: s == current ? Colors.white : Neon.muted),
                  const SizedBox(width: 12),
                  Text(_sectionInfo[s]!.$1, style: TextStyle(fontSize: 15, color: s == current ? Colors.white : Neon.text)),
                ]),
              ),
            ),
          ),
        const Spacer(),
        NeonPanel(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Row(children: [Icon(Icons.workspace_premium, color: Neon.purple), SizedBox(width: 8), Text('Open source', style: TextStyle(fontWeight: FontWeight.bold))]),
            const SizedBox(height: 4),
            const Text('Everything unlocked: unlimited pages, profiles and macros.', style: TextStyle(color: Neon.muted, fontSize: 12)),
          ]),
        ),
      ]),
    );
  }
}

// ---------------------------------------------------------------------------
// Deck
// ---------------------------------------------------------------------------

class DeckView extends StatelessWidget {
  const DeckView({super.key, required this.onNavigate});
  final ValueChanged<Section> onNavigate;

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      return LayoutBuilder(builder: (context, box) {
        final wide = box.maxWidth >= 640;
        final grid = Column(children: [
          _PageTabs(),
          if (app.pcs.isEmpty) const _PairBanner(),
          Expanded(child: _DeckGrid()),
        ]);
        if (!wide) {
          return Column(children: [
            _StatusStrip(),
            const SizedBox(height: 6),
            Expanded(child: grid),
          ]);
        }
        return Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Expanded(child: grid),
          const SizedBox(width: 12),
          SizedBox(width: 250, child: SingleChildScrollView(child: _StatusPanel(onNavigate: onNavigate))),
        ]);
      });
    });
  }
}

class _PairBanner extends StatelessWidget {
  const _PairBanner();
  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: NeonPanel(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
        child: Row(children: [
          const Icon(Icons.qr_code_scanner, color: Neon.blue),
          const SizedBox(width: 12),
          const Expanded(child: Text('Pair with your PC to start controlling it.')),
          FilledButton(onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const PairScreen())), child: const Text('Pair')),
        ]),
      ),
    );
  }
}

class _PageTabs extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      return SizedBox(
        height: 52,
        child: Row(children: [
          Expanded(
            child: ListView(scrollDirection: Axis.horizontal, children: [
              for (var i = 0; i < app.profile.pages.length; i++)
                Padding(
                  padding: const EdgeInsets.only(right: 8, top: 6, bottom: 6),
                  child: GestureDetector(
                    onLongPress: () => _pageMenu(context, app, i),
                    child: ChoiceChip(
                      selected: i == app.pageIndex,
                      label: Text(app.profile.pages[i].name),
                      avatar: i == 0 ? const Icon(Icons.home, size: 18) : null,
                      selectedColor: Neon.purple,
                      backgroundColor: Neon.panel,
                      shape: const StadiumBorder(),
                      onSelected: (_) {
                        if (i == app.pageIndex && app.editMode) {
                          _pageMenu(context, app, i);
                        } else {
                          app.setPage(i);
                        }
                      },
                    ),
                  ),
                ),
              Padding(
                padding: const EdgeInsets.only(top: 6, bottom: 6),
                child: ActionChip(avatar: const Icon(Icons.add, size: 18), label: const Text('Page'), shape: const StadiumBorder(), backgroundColor: Neon.panel, onPressed: app.addPage),
              ),
            ]),
          ),
          const SizedBox(width: 6),
          FilledButton.tonalIcon(
            style: FilledButton.styleFrom(backgroundColor: app.editMode ? Neon.purple : Neon.panel2),
            icon: Icon(app.editMode ? Icons.check : Icons.edit, size: 18),
            label: Text(app.editMode ? 'Done' : 'Edit'),
            onPressed: () {
              app.editMode = !app.editMode;
              app.changed();
            },
          ),
        ]),
      );
    });
  }

  Future<void> _pageMenu(BuildContext context, AppState app, int i) async {
    final page = app.profile.pages[i];
    await showModalBottomSheet(
      context: context,
      backgroundColor: Neon.panel,
      isScrollControlled: true,
      builder: (ctx) => StatefulBuilder(builder: (ctx, set) {
        return Padding(
          padding: EdgeInsets.fromLTRB(20, 16, 20, 20 + MediaQuery.of(ctx).viewInsets.bottom),
          child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
            TextFormField(
              initialValue: page.name,
              decoration: const InputDecoration(labelText: 'Page name'),
              onChanged: (v) {
                page.name = v;
                app.changed();
              },
            ),
            const SizedBox(height: 14),
            Text('Columns: ${page.cols}'),
            Slider(
              value: page.cols.toDouble(), min: 2, max: 8, divisions: 6,
              onChanged: (v) {
                page.resize(v.round(), page.rows);
                set(() {});
                app.changed();
              },
            ),
            Text('Rows: ${page.rows}'),
            Slider(
              value: page.rows.toDouble(), min: 1, max: 8, divisions: 7,
              onChanged: (v) {
                page.resize(page.cols, v.round());
                set(() {});
                app.changed();
              },
            ),
            if (app.profile.pages.length > 1)
              TextButton.icon(
                style: TextButton.styleFrom(foregroundColor: Colors.redAccent),
                icon: const Icon(Icons.delete),
                label: const Text('Delete this page'),
                onPressed: () {
                  Navigator.pop(ctx);
                  app.deletePage(i);
                },
              ),
          ]),
        );
      }),
    );
  }
}

class _DeckGrid extends StatelessWidget {
  Future<void> _press(BuildContext context, AppState app, ConnectionManager conn, DeckButton b) async {
    if (app.haptics) HapticFeedback.lightImpact();
    if (!conn.connected) {
      toast(context, app.pcs.isEmpty ? 'Pair with a PC first' : 'Not connected to the PC', error: true);
      conn.nudge();
      return;
    }
    if (b.confirm) {
      final ok = await showDialog<bool>(
        context: context,
        builder: (ctx) => AlertDialog(
          title: Text('${b.label}?'),
          content: const Text('Run this action on your PC?'),
          actions: [
            TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancel')),
            FilledButton(onPressed: () => Navigator.pop(ctx, true), child: const Text('Run')),
          ],
        ),
      );
      if (ok != true) return;
    }
    final r = await conn.run(b.action);
    if (!context.mounted) return;
    if (!r.ok) {
      toast(context, r.error ?? 'Failed', error: true);
    } else if (r.message != null) {
      toast(context, r.message!);
    }
  }

  Future<void> _edit(BuildContext context, AppState app, int slot, DeckButton? existing) async {
    final isNew = existing == null;
    final b = existing ?? DeckButton(label: '', icon: 'bolt', action: ActionSpec('volume_up', {'step': 5}));
    final r = await Navigator.push<Object?>(context, MaterialPageRoute(builder: (_) => ButtonEditorScreen(button: b, isNew: isNew)));
    if (r is DeckButton) app.setButton(slot, r);
    if (isRemove(r)) app.setButton(slot, null);
  }

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      final page = app.page;
      return LayoutBuilder(builder: (context, box) {
        const gap = 12.0;
        final cellW = (box.maxWidth - gap * (page.cols - 1)) / page.cols;
        var cellH = (box.maxHeight - gap * (page.rows - 1) - 8) / page.rows;
        var scroll = false;
        if (cellH < 74) {
          cellH = cellW.clamp(74.0, 150.0);
          scroll = true;
        }
        final ratio = cellW / cellH;

        Widget slot(int i) {
          final b = page.slots[i];
          final active = b != null &&
              ((b.action.type == 'mute_toggle' && conn.status.muted) || (b.action.type == 'mic_toggle' && conn.status.micMuted == true));
          Widget tile;
          if (b == null) {
            tile = GestureDetector(
              onTap: app.editMode ? () => _edit(context, app, i, null) : null,
              child: Container(
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(20),
                  color: Neon.panel.withAlpha(app.editMode ? 200 : 80),
                  border: Border.all(color: app.editMode ? Neon.purple.withAlpha(140) : Colors.white10),
                ),
                child: app.editMode ? const Center(child: Icon(Icons.add, color: Neon.purple, size: 32)) : null,
              ),
            );
          } else {
            tile = Stack(children: [
              Positioned.fill(
                child: NeonKey(
                  button: b,
                  active: active,
                  onTap: () => app.editMode ? _edit(context, app, i, b) : _press(context, app, conn, b),
                ),
              ),
              if (app.editMode)
                const Positioned(right: 6, top: 6, child: Icon(Icons.edit, size: 16, color: Colors.white70)),
            ]);
          }
          return DragTarget<int>(
            onWillAcceptWithDetails: (d) => d.data != i,
            onAcceptWithDetails: (d) => app.swapSlots(d.data, i),
            builder: (ctx, cand, _) {
              final hover = cand.isNotEmpty;
              final inner = b == null
                  ? tile
                  : LongPressDraggable<int>(
                      data: i,
                      hapticFeedbackOnStart: true,
                      feedback: SizedBox(width: cellW, height: cellH, child: Material(color: Colors.transparent, child: Opacity(opacity: .9, child: NeonKey(button: b)))),
                      childWhenDragging: Opacity(opacity: .2, child: tile),
                      child: tile,
                    );
              return AnimatedContainer(
                duration: const Duration(milliseconds: 120),
                decoration: BoxDecoration(borderRadius: BorderRadius.circular(22), border: Border.all(color: hover ? Neon.green : Colors.transparent, width: 2)),
                child: inner,
              );
            },
          );
        }

        return GridView.builder(
          physics: scroll ? const BouncingScrollPhysics() : const NeverScrollableScrollPhysics(),
          padding: const EdgeInsets.only(bottom: 8),
          gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(crossAxisCount: page.cols, mainAxisSpacing: gap, crossAxisSpacing: gap, childAspectRatio: ratio),
          itemCount: page.slots.length,
          itemBuilder: (_, i) => slot(i),
        );
      });
    });
  }
}

// ---------------------------------------------------------------------------
// PC status
// ---------------------------------------------------------------------------

class _StatusPanel extends StatelessWidget {
  const _StatusPanel({required this.onNavigate});
  final ValueChanged<Section> onNavigate;

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      final s = conn.status;
      Widget bar(IconData icon, Color c, String label, double v, String valueText) => Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: Row(children: [
              Icon(icon, color: c),
              const SizedBox(width: 10),
              Expanded(
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [Text(label, style: const TextStyle(color: Neon.muted, fontSize: 12)), Text(valueText, style: TextStyle(color: c, fontWeight: FontWeight.bold, fontSize: 12))]),
                  const SizedBox(height: 4),
                  ClipRRect(borderRadius: BorderRadius.circular(6), child: LinearProgressIndicator(value: v.clamp(0, 1), minHeight: 6, color: c, backgroundColor: Colors.white12)),
                ]),
              ),
            ]),
          );
      final on = conn.connected;
      return Column(children: [
        NeonPanel(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Icon(Icons.circle, size: 12, color: on ? Neon.green : Colors.redAccent),
              const SizedBox(width: 8),
              const Text('PC Status', style: TextStyle(fontWeight: FontWeight.bold, fontSize: 15)),
            ]),
            const SizedBox(height: 14),
            bar(s.muted ? Icons.volume_off : Icons.volume_up, Neon.blue, 'Volume', s.volume / 100, on ? '${s.volume}%' : '—'),
            bar(Icons.wb_sunny, Colors.amber, 'Brightness', s.brightness / 100, !on ? '—' : (s.brightness < 0 ? 'n/a' : '${s.brightness}%')),
            Row(children: [
              Icon(s.micMuted == true ? Icons.mic_off : Icons.mic, color: s.micMuted == true ? Colors.redAccent : Neon.green),
              const SizedBox(width: 10),
              Expanded(child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                const Text('Mic', style: TextStyle(color: Neon.muted, fontSize: 12)),
                Text(!on ? '—' : (s.micMuted == null ? 'No microphone' : (s.micMuted! ? 'Muted' : 'Unmuted')), style: TextStyle(color: s.micMuted == true ? Colors.redAccent : Neon.green, fontWeight: FontWeight.bold)),
              ])),
            ]),
          ]),
        ),
        const SizedBox(height: 12),
        NeonPanel(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Row(children: [Icon(Icons.bolt, color: Colors.amber, size: 20), SizedBox(width: 8), Text('Quick Actions', style: TextStyle(fontWeight: FontWeight.bold))]),
            _QuickRow(Icons.apps, 'Open Apps', () => onNavigate(Section.apps)),
            _QuickRow(Icons.image, 'Live Backgrounds', () => onNavigate(Section.backgrounds)),
            _QuickRow(Icons.play_arrow, 'Macros', () => onNavigate(Section.macros)),
          ]),
        ),
      ]);
    });
  }
}

class _QuickRow extends StatelessWidget {
  const _QuickRow(this.icon, this.text, this.onTap);
  final IconData icon;
  final String text;
  final VoidCallback onTap;
  @override
  Widget build(BuildContext context) => InkWell(
        onTap: onTap,
        child: Padding(padding: const EdgeInsets.symmetric(vertical: 10), child: Row(children: [Icon(icon, size: 20, color: Neon.muted), const SizedBox(width: 12), Text(text)])),
      );
}

class _StatusStrip extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      final s = conn.status;
      final on = conn.connected;
      Widget chip(IconData i, Color c, String t) => Expanded(child: Row(mainAxisAlignment: MainAxisAlignment.center, children: [Icon(i, size: 16, color: c), const SizedBox(width: 6), Text(t, style: TextStyle(color: c, fontSize: 12, fontWeight: FontWeight.bold))]));
      return NeonPanel(
        padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 6),
        radius: 14,
        glow: false,
        child: Row(children: [
          chip(s.muted ? Icons.volume_off : Icons.volume_up, Neon.blue, on ? '${s.volume}%' : '—'),
          chip(Icons.wb_sunny, Colors.amber, !on ? '—' : (s.brightness < 0 ? 'n/a' : '${s.brightness}%')),
          chip(s.micMuted == true ? Icons.mic_off : Icons.mic, s.micMuted == true ? Colors.redAccent : Neon.green, !on ? '—' : (s.micMuted == true ? 'Muted' : 'Live')),
        ]),
      );
    });
  }
}
