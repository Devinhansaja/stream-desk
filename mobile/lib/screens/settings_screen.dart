import 'package:flutter/material.dart';

import '../scope.dart';
import '../store.dart';
import '../widgets.dart';
import 'home.dart' show askText;
import 'pair_screen.dart';

class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Live(builder: (context, app, conn) {
      return ListView(children: [
        _title('PC connection'),
        NeonPanel(
          child: Column(children: [
            for (var i = 0; i < app.pcs.length; i++)
              ListTile(
                contentPadding: EdgeInsets.zero,
                leading: Icon(Icons.desktop_windows, color: i == app.activePcIndex ? Neon.green : Neon.muted),
                title: Text(app.pcs[i].name),
                subtitle: Text(i == app.activePcIndex ? (conn.connected ? 'Connected via ${conn.isUsb ? 'USB' : 'Wi-Fi'}' : (conn.lastError ?? 'Offline - reconnecting automatically')) : 'Tap to switch', style: const TextStyle(color: Neon.muted)),
                onTap: () {
                  app.activePcIndex = i;
                  conn.needsRepair = false;
                  app.changed();
                  conn.nudge();
                },
                trailing: IconButton(icon: const Icon(Icons.delete_outline), tooltip: 'Forget this PC', onPressed: () => app.removePc(i)),
              ),
            if (app.pcs.isEmpty) const Padding(padding: EdgeInsets.only(bottom: 8), child: Text('No PC paired yet.', style: TextStyle(color: Neon.muted))),
            SizedBox(
              width: double.infinity,
              child: FilledButton.icon(
                icon: const Icon(Icons.qr_code_scanner),
                label: const Text('Pair a PC (scan QR)'),
                onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const PairScreen())),
              ),
            ),
          ]),
        ),
        const SizedBox(height: 12),
        NeonPanel(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            const Text('Connection mode', style: TextStyle(fontWeight: FontWeight.bold)),
            const SizedBox(height: 8),
            SegmentedButton<LinkMode>(
              segments: const [
                ButtonSegment(value: LinkMode.auto, label: Text('Auto'), icon: Icon(Icons.autorenew)),
                ButtonSegment(value: LinkMode.wifi, label: Text('Wi-Fi'), icon: Icon(Icons.wifi)),
                ButtonSegment(value: LinkMode.usb, label: Text('USB'), icon: Icon(Icons.usb)),
              ],
              selected: {app.linkMode},
              onSelectionChanged: (s) {
                app.linkMode = s.first;
                app.changed();
                conn.nudge();
              },
            ),
            const SizedBox(height: 8),
            const Text('Auto tries USB first, then the last known address, then finds the PC on your Wi-Fi. For USB: enable USB debugging on the phone and plug it in; StreamDeck Host sets up the tunnel automatically.', style: TextStyle(color: Neon.muted, fontSize: 12)),
          ]),
        ),
        const SizedBox(height: 12),
        _title('This phone'),
        NeonPanel(
          child: Column(children: [
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Device name'),
              subtitle: Text(app.deviceName, style: const TextStyle(color: Neon.muted)),
              trailing: const Icon(Icons.edit),
              onTap: () async {
                final n = await askText(context, 'Device name', 'Shown in StreamDeck Host', initial: app.deviceName);
                if (n != null && n.trim().isNotEmpty) {
                  app.deviceName = n.trim();
                  app.changed();
                }
              },
            ),
            SwitchListTile(contentPadding: EdgeInsets.zero, title: const Text('Haptic feedback'), value: app.haptics, onChanged: (v) {
              app.haptics = v;
              app.changed();
            }),
          ]),
        ),
        const SizedBox(height: 12),
        _title('Profiles'),
        NeonPanel(
          child: Column(children: [
            for (final p in app.profiles)
              ListTile(
                contentPadding: EdgeInsets.zero,
                leading: Icon(p.id == app.currentProfileId ? Icons.radio_button_checked : Icons.radio_button_off, color: Neon.purple),
                title: Text(p.name),
                subtitle: Text('${p.pages.length} page${p.pages.length == 1 ? '' : 's'}', style: const TextStyle(color: Neon.muted)),
                onTap: () => app.selectProfile(p.id),
                trailing: Row(mainAxisSize: MainAxisSize.min, children: [
                  IconButton(
                    icon: const Icon(Icons.edit, size: 20),
                    onPressed: () async {
                      final n = await askText(context, 'Rename profile', 'Name', initial: p.name);
                      if (n != null && n.trim().isNotEmpty) {
                        p.name = n.trim();
                        app.changed();
                      }
                    },
                  ),
                  if (app.profiles.length > 1) IconButton(icon: const Icon(Icons.delete_outline, size: 20), onPressed: () => app.deleteProfile(p.id)),
                ]),
              ),
            SizedBox(
              width: double.infinity,
              child: OutlinedButton.icon(
                icon: const Icon(Icons.add),
                label: const Text('New profile'),
                onPressed: () async {
                  final n = await askText(context, 'New profile', 'Profile name');
                  if (n != null && n.trim().isNotEmpty) app.addProfile(n.trim());
                },
              ),
            ),
          ]),
        ),
        const SizedBox(height: 20),
        const Center(child: Text('StreamDeck Mobile Controller 1.0', style: TextStyle(color: Neon.muted, fontSize: 12))),
      ]);
    });
  }

  Widget _title(String t) => Padding(padding: const EdgeInsets.only(bottom: 8, top: 4), child: Text(t, style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 16)));
}

