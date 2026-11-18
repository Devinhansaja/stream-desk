import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:wakelock_plus/wakelock_plus.dart';
import 'package:webview_flutter/webview_flutter.dart';

import '../connection.dart';
import '../store.dart';
import '../updater.dart';
import '../widgets.dart';
import 'pair_screen.dart';

enum Phase { noPc, resolving, running, failed }

/// Full-screen deck. The buttons are drawn by a page served from the PC (so PC updates change the phone UI
/// immediately, with no APK reinstall) and are edited only in the PC's editor.
class ShellScreen extends StatefulWidget {
  const ShellScreen({super.key, required this.app});
  final AppState app;
  @override
  State<ShellScreen> createState() => _ShellScreenState();
}

class _ShellScreenState extends State<ShellScreen> with WidgetsBindingObserver {
  Phase phase = Phase.resolving;
  String? error;
  WebViewController? web;
  bool _resolving = false;

  AppState get app => widget.app;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
    WakelockPlus.enable();
    _begin();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    WakelockPlus.disable();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState s) {
    if (s == AppLifecycleState.resumed) {
      SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
      // The page reconnects by itself; if the PC moved, it asks us to look again.
      if (phase == Phase.failed) _begin();
    }
  }

  Future<void> _begin() async {
    if (_resolving) return;
    final pc = app.activePc;
    if (pc == null) {
      setState(() => phase = Phase.noPc);
      return;
    }
    _resolving = true;
    if (phase != Phase.running) setState(() => phase = Phase.resolving);
    final host = await HostLink.resolve(pc, app.linkMode);
    _resolving = false;
    if (!mounted) return;
    if (host == null) {
      setState(() {
        phase = Phase.failed;
        error = 'Could not find "${pc.name}".\n\n• Is StreamDeck Host running on the PC?\n• Same Wi-Fi? Firewall allowed (press "Fix firewall" on the PC)?\n• For USB: plug in, enable USB debugging, and keep the host running.';
      });
      return;
    }
    pc.lastHost = host;
    app.save();
    final url = Uri.parse('${Endpoint.parse(host, pc.port).httpBase}/deck#d=${pc.deviceId}&t=${pc.token}');
    final c = web ??
        (WebViewController()
          ..setJavaScriptMode(JavaScriptMode.unrestricted)
          ..setBackgroundColor(Neon.bg)
          ..addJavaScriptChannel('Native', onMessageReceived: _onJs)
          ..setNavigationDelegate(NavigationDelegate(onWebResourceError: (e) {
            if (e.isForMainFrame ?? false) {
              if (mounted) setState(() => phase = Phase.failed);
              error = 'The page could not be loaded (${e.description}).';
            }
          })));
    web = c;
    await c.loadRequest(url);
    if (mounted) setState(() => phase = Phase.running);
  }

  void _onJs(JavaScriptMessage m) {
    if (m.message.startsWith('hosts:')) {
      // The PC tells us its current addresses (LAN, Tailscale, tunnel...) so we can reach it from other networks.
      try {
        final list = (jsonDecode(m.message.substring(6)) as List).cast<String>();
        final pc = app.activePc;
        if (pc != null && list.isNotEmpty && list.join('|') != pc.hosts.join('|')) {
          pc.hosts = list;
          app.save();
        }
      } catch (_) {}
      return;
    }
    switch (m.message) {
      case 'h':
        if (app.haptics) HapticFeedback.lightImpact();
        break;
      case 'settings':
        _settings();
        break;
      case 'offline':
        _begin(); // PC may have a new address
        break;
      case 'unpaired':
        if (mounted) {
          setState(() {
            phase = Phase.failed;
            error = 'This PC no longer recognises this phone (it was removed in StreamDeck Host). Pair again.';
          });
        }
        break;
    }
  }

  Future<void> _pair() async {
    final ok = await Navigator.push<bool>(context, MaterialPageRoute(builder: (_) => PairScreen(app: app)));
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
    if (ok == true) {
      web = null;
      _begin();
    }
  }

  Future<void> _settings() async {
    await showModalBottomSheet(
      context: context,
      backgroundColor: Neon.panel,
      isScrollControlled: true,
      builder: (ctx) => StatefulBuilder(builder: (ctx, set) {
        return SafeArea(
          child: Padding(
            padding: const EdgeInsets.all(18),
            child: Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: CrossAxisAlignment.start, children: [
              const Text('Phone settings', style: TextStyle(fontSize: 18, fontWeight: FontWeight.bold)),
              const SizedBox(height: 6),
              const Text('Button layout is edited on the PC (StreamDeck Host → Open button editor).', style: TextStyle(color: Neon.muted, fontSize: 12)),
              const SizedBox(height: 12),
              for (var i = 0; i < app.pcs.length; i++)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: Icon(Icons.desktop_windows, color: i == app.activePcIndex ? Neon.green : Neon.muted),
                  title: Text(app.pcs[i].name),
                  subtitle: Text(i == app.activePcIndex ? 'Active' : 'Tap to use this PC'),
                  onTap: () {
                    app.activePcIndex = i;
                    app.changed();
                    Navigator.pop(ctx);
                    web = null;
                    _begin();
                  },
                  trailing: IconButton(
                    icon: const Icon(Icons.delete_outline),
                    onPressed: () {
                      app.removePc(i);
                      Navigator.pop(ctx);
                      web = null;
                      _begin();
                    },
                  ),
                ),
              const SizedBox(height: 8),
              SegmentedButton<LinkMode>(
                segments: const [
                  ButtonSegment(value: LinkMode.auto, label: Text('Auto')),
                  ButtonSegment(value: LinkMode.wifi, label: Text('Wi-Fi')),
                  ButtonSegment(value: LinkMode.usb, label: Text('USB')),
                ],
                selected: {app.linkMode},
                onSelectionChanged: (s) {
                  app.linkMode = s.first;
                  app.changed();
                  set(() {});
                },
              ),
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text('Haptic feedback'),
                value: app.haptics,
                onChanged: (v) {
                  app.haptics = v;
                  app.changed();
                  set(() {});
                },
              ),
              Row(children: [
                Expanded(
                  child: FilledButton.icon(
                    icon: const Icon(Icons.qr_code_scanner),
                    label: const Text('Pair a PC'),
                    onPressed: () {
                      Navigator.pop(ctx);
                      _pair();
                    },
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: OutlinedButton.icon(
                    icon: const Icon(Icons.refresh),
                    label: const Text('Reconnect'),
                    onPressed: () {
                      Navigator.pop(ctx);
                      web = null;
                      _begin();
                    },
                  ),
                ),
              ]),
              const SizedBox(height: 10),
              SizedBox(
                width: double.infinity,
                child: OutlinedButton.icon(
                  icon: const Icon(Icons.system_update),
                  label: const Text('Check for app update'),
                  onPressed: () => _checkUpdate(ctx),
                ),
              ),
              const SizedBox(height: 6),
              const Center(child: Text('StreamDeck Mobile $kAppVersion  ·  deck look and buttons update from the PC automatically', textAlign: TextAlign.center, style: TextStyle(color: Neon.muted, fontSize: 12))),
            ]),
          ),
        );
      }),
    );
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
  }

  Future<void> _checkUpdate(BuildContext sheetCtx) async {
    final nav = Navigator.of(sheetCtx);
    showDialog(context: sheetCtx, barrierDismissible: false, builder: (_) => const Center(child: CircularProgressIndicator()));
    final r = await checkAppUpdate();
    nav.pop(); // spinner
    if (!sheetCtx.mounted) return;
    final u = r.update;
    await showDialog(
      context: sheetCtx,
      builder: (ctx) => AlertDialog(
        title: Text(r.error != null ? 'Could not check' : (u == null ? 'You are up to date' : 'Update available')),
        content: Text(r.error ?? (u == null
            ? 'StreamDeck Mobile $kAppVersion is the latest version.'
            : 'Version ${u.version} is available (you have $kAppVersion).\n\nTap Download, then open the downloaded file to install it over this app. Your pairing and settings are kept.')),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Close')),
          if (u != null) FilledButton(onPressed: () { launchUrl(Uri.parse(u.apkUrl), mode: LaunchMode.externalApplication); Navigator.pop(ctx); }, child: const Text('Download')),
        ],
      ),
    );
  }

  Widget _center(List<Widget> children) => Center(
        child: Padding(
          padding: const EdgeInsets.all(28),
          child: Column(mainAxisSize: MainAxisSize.min, children: children),
        ),
      );

  @override
  Widget build(BuildContext context) {
    Widget body;
    switch (phase) {
      case Phase.running:
        body = WebViewWidget(controller: web!);
        break;
      case Phase.resolving:
        body = _center([
          const CircularProgressIndicator(),
          const SizedBox(height: 18),
          Text('Connecting to ${app.activePc?.name ?? 'PC'}…', style: const TextStyle(fontSize: 16)),
        ]);
        break;
      case Phase.noPc:
        body = _center([
          const Icon(Icons.qr_code_scanner, size: 64, color: Neon.purple),
          const SizedBox(height: 16),
          const Text('Pair with your PC', style: TextStyle(fontSize: 20, fontWeight: FontWeight.bold)),
          const SizedBox(height: 8),
          const Text('Start StreamDeck Host on your Windows PC, then scan the QR code on its Pairing tab.', textAlign: TextAlign.center, style: TextStyle(color: Neon.muted)),
          const SizedBox(height: 20),
          FilledButton.icon(onPressed: _pair, icon: const Icon(Icons.qr_code_scanner), label: const Text('Scan QR code')),
        ]);
        break;
      case Phase.failed:
        body = _center([
          const Icon(Icons.link_off, size: 56, color: Colors.redAccent),
          const SizedBox(height: 14),
          Text(error ?? 'Not connected', textAlign: TextAlign.center, style: const TextStyle(color: Neon.muted)),
          const SizedBox(height: 20),
          Wrap(spacing: 10, runSpacing: 10, alignment: WrapAlignment.center, children: [
            FilledButton.icon(onPressed: () { web = null; _begin(); }, icon: const Icon(Icons.refresh), label: const Text('Try again')),
            OutlinedButton.icon(onPressed: _pair, icon: const Icon(Icons.qr_code_scanner), label: const Text('Pair again')),
            OutlinedButton.icon(onPressed: _settings, icon: const Icon(Icons.tune), label: const Text('Settings')),
          ]),
        ]);
        break;
    }
    return Scaffold(backgroundColor: Neon.bg, body: SafeArea(top: false, bottom: false, child: body));
  }
}
