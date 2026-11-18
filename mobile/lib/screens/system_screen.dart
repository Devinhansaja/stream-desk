import 'package:flutter/material.dart';

import '../catalog.dart';
import '../models.dart';
import '../scope.dart';
import '../widgets.dart';

/// System control presets: tap a tile to run it now, "+" to put it on the current deck page as an editable button.
/// Also has live sliders for volume and brightness.
class SystemScreen extends StatelessWidget {
  const SystemScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      final presets = systemPresets();
      final s = conn.status;
      return ListView(children: [
        NeonPanel(
          child: Column(children: [
            _LiveSlider(
              icon: s.muted ? Icons.volume_off : Icons.volume_up,
              label: 'Volume',
              value: s.volume.toDouble(),
              enabled: conn.connected,
              color: Neon.blue,
              onEnd: (v) => conn.run(ActionSpec('volume_set', {'value': v.round()})),
            ),
            _LiveSlider(
              icon: Icons.wb_sunny,
              label: s.brightness < 0 && conn.connected ? 'Brightness (not supported by this display)' : 'Brightness',
              value: s.brightness < 0 ? 0 : s.brightness.toDouble(),
              enabled: conn.connected && s.brightness >= 0,
              color: Colors.amber,
              onEnd: (v) async {
                final r = await conn.run(ActionSpec('brightness_set', {'value': v.round()}));
                if (!r.ok && context.mounted) toast(context, r.error ?? 'Failed', error: true);
              },
            ),
          ]),
        ),
        const SizedBox(height: 14),
        const Text('Presets', style: TextStyle(fontWeight: FontWeight.bold, fontSize: 16)),
        const Text('Tap to run now. Use + to add as a button to the current page.', style: TextStyle(color: Neon.muted, fontSize: 12)),
        const SizedBox(height: 8),
        for (final p in presets)
          Card(
            margin: const EdgeInsets.only(bottom: 6),
            child: ListTile(
              leading: Container(
                width: 42,
                height: 42,
                decoration: BoxDecoration(borderRadius: BorderRadius.circular(12), gradient: LinearGradient(colors: [Color(p.c1), Color(p.c2)])),
                child: Icon(kIcons[p.icon], color: Colors.white),
              ),
              title: Text(p.label),
              subtitle: Text(actionDef(p.action.type)?.label ?? p.action.type, style: const TextStyle(color: Neon.muted, fontSize: 12)),
              onTap: () async {
                if (p.confirm) {
                  final ok = await showDialog<bool>(
                    context: context,
                    builder: (ctx) => AlertDialog(
                      title: Text('${p.label}?'),
                      actions: [TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Cancel')), FilledButton(onPressed: () => Navigator.pop(ctx, true), child: const Text('Run'))],
                    ),
                  );
                  if (ok != true) return;
                }
                final r = await conn.run(p.action);
                if (context.mounted) toast(context, r.ok ? (r.message ?? '${p.label} done') : (r.error ?? 'Failed'), error: !r.ok);
              },
              trailing: IconButton(
                icon: const Icon(Icons.add_box_outlined),
                tooltip: 'Add to deck',
                onPressed: () {
                  app.addButtonToFirstFree(DeckButton(label: p.label, icon: p.icon, color1: p.c1, color2: p.c2, confirm: p.confirm, action: p.action.copy()));
                  toast(context, 'Added "${p.label}" to ${app.page.name}');
                },
              ),
            ),
          ),
      ]);
    });
  }
}

class _LiveSlider extends StatefulWidget {
  const _LiveSlider({required this.icon, required this.label, required this.value, required this.enabled, required this.color, required this.onEnd});
  final IconData icon;
  final String label;
  final double value;
  final bool enabled;
  final Color color;
  final void Function(double) onEnd;
  @override
  State<_LiveSlider> createState() => _LiveSliderState();
}

class _LiveSliderState extends State<_LiveSlider> {
  double? _drag;
  @override
  Widget build(BuildContext context) {
    final v = (_drag ?? widget.value).clamp(0, 100).toDouble();
    return Row(children: [
      Icon(widget.icon, color: widget.color),
      const SizedBox(width: 8),
      Expanded(
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('${widget.label}  ${widget.enabled ? '${v.round()}%' : ''}', style: const TextStyle(color: Neon.muted, fontSize: 12)),
          Slider(
            value: v,
            min: 0,
            max: 100,
            activeColor: widget.color,
            onChanged: widget.enabled ? (x) => setState(() => _drag = x) : null,
            onChangeEnd: (x) {
              widget.onEnd(x);
              setState(() => _drag = null);
            },
          ),
        ]),
      ),
    ]);
  }
}
