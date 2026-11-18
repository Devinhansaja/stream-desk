import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:web_socket_channel/io.dart';

import 'models.dart';
import 'store.dart';

enum LinkState { disconnected, connecting, connected }

class ActionResult {
  final bool ok;
  final String? error;
  final String? message;
  const ActionResult(this.ok, {this.error, this.message});
}

class DiscoveredPc {
  final String ip;
  final String name;
  final int port;
  const DiscoveredPc(this.ip, this.name, this.port);
}

class PairQr {
  final String name;
  final int port;
  final List<String> hosts;
  final String secret;
  PairQr(this.name, this.port, this.hosts, this.secret);

  static PairQr? parse(String raw) {
    try {
      final j = jsonDecode(raw) as Map<String, dynamic>;
      return PairQr(
        (j['n'] ?? 'PC') as String,
        (j['p'] ?? 47800) as int,
        List<String>.from((j['h'] ?? []) as List),
        j['s'] as String,
      );
    } catch (_) {
      return null;
    }
  }
}

/// Owns the single WebSocket to the PC: USB/Wi-Fi/discovery, pairing, auth, automatic reconnection,
/// request/response matching and the live PC status.
class ConnectionManager extends ChangeNotifier {
  ConnectionManager(this.app);
  final AppState app;

  static const discoveryPort = 47801;

  LinkState state = LinkState.disconnected;
  String? hostName;
  String? lastError;
  String? connectedVia; // host/ip in use ("127.0.0.1" == USB)
  PcStatus status = const PcStatus();
  bool needsRepair = false;

  IOWebSocketChannel? _ch;
  bool _running = false;
  bool _busy = false; // pairing in progress: pause the reconnect loop
  int _nextId = 1;
  final Map<int, Completer<Map<String, dynamic>>> _pending = {};

  bool get isUsb => connectedVia == '127.0.0.1';
  bool get connected => state == LinkState.connected;

  // ------------------------------------------------------------ lifecycle
  void start() {
    if (_running) return;
    _running = true;
    _loop();
  }

  void stop() {
    _running = false;
    _drop();
  }

  /// Call when the app returns to foreground so we reconnect immediately.
  void nudge() {
    if (!connected) _wake?.complete();
  }

  Completer<void>? _wake;

  Future<void> _sleep(Duration d) async {
    _wake = Completer<void>();
    await Future.any([Future.delayed(d), _wake!.future]);
    _wake = null;
  }

  Future<void> _loop() async {
    var attempt = 0;
    while (_running) {
      if (!connected && !_busy && app.activePc != null && !needsRepair) {
        state = LinkState.connecting;
        notifyListeners();
        final ok = await _connectToActive();
        if (ok) {
          attempt = 0;
        } else {
          attempt++;
          if (!connected) {
            state = LinkState.disconnected;
            notifyListeners();
          }
        }
      }
      await _sleep(Duration(seconds: connected ? 3 : min(1 + attempt, 6)));
    }
  }

  // ------------------------------------------------------------ discovery
  Future<List<DiscoveredPc>> discover({Duration wait = const Duration(milliseconds: 1500)}) async {
    final found = <String, DiscoveredPc>{};
    RawDatagramSocket? sock;
    try {
      sock = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
      sock.broadcastEnabled = true;
      sock.listen((e) {
        if (e == RawSocketEvent.read) {
          final d = sock!.receive();
          if (d == null) return;
          try {
            final j = jsonDecode(utf8.decode(d.data)) as Map<String, dynamic>;
            if (j['app'] == 'streamdeck-host') {
              found[d.address.address] =
                  DiscoveredPc(d.address.address, (j['name'] ?? 'PC') as String, (j['port'] ?? 47800) as int);
            }
          } catch (_) {}
        }
      });
      final msg = utf8.encode('STREAMDECK_DISCOVER');
      sock.send(msg, InternetAddress('255.255.255.255'), discoveryPort);
      await Future.delayed(const Duration(milliseconds: 250));
      sock.send(msg, InternetAddress('255.255.255.255'), discoveryPort);
      await Future.delayed(wait);
    } catch (e) {
      debugPrint('discovery failed: $e');
    } finally {
      sock?.close();
    }
    return found.values.toList();
  }

  // ------------------------------------------------------------ connect
  Future<bool> _connectToActive() async {
    final pc = app.activePc!;
    final mode = app.linkMode;
    final order = <String>[];
    void add(String? h) {
      if (h != null && h.isNotEmpty && !order.contains(h)) order.add(h);
    }

    if (mode != LinkMode.wifi) add('127.0.0.1'); // USB (adb reverse) is fastest and preferred
    if (mode != LinkMode.usb) {
      add(pc.lastHost);
      pc.hosts.forEach(add);
    }

    for (final h in order) {
      if (await _tryAuth(pc, h)) return true;
      if (needsRepair) return false;
    }
    if (mode != LinkMode.usb) {
      final found = await discover();
      for (final d in found) {
        if (d.name == pc.name || found.length == 1) {
          if (!pc.hosts.contains(d.ip)) pc.hosts.add(d.ip);
          if (await _tryAuth(pc, d.ip, port: d.port)) {
            app.changed();
            return true;
          }
        }
      }
    }
    return false;
  }

  Future<bool> _tryAuth(PairedPc pc, String host, {int? port}) async {
    final r = await _handshake(host, port ?? pc.port, {'t': 'auth', 'deviceId': pc.deviceId, 'token': pc.token});
    if (r == null) return false;
    if (r['t'] == 'auth_ok') {
      hostName = (r['host'] ?? pc.name) as String;
      pc.lastHost = host;
      connectedVia = host;
      lastError = null;
      state = LinkState.connected;
      app.save();
      notifyListeners();
      return true;
    }
    if (r['t'] == 'auth_fail') {
      needsRepair = true;
      lastError = 'This PC no longer recognises this phone. Pair again.';
      _drop();
      notifyListeners();
    }
    return false;
  }

  /// Opens a socket, sends [hello] and returns the first handshake reply, leaving the socket open on success.
  Future<Map<String, dynamic>?> _handshake(String host, int port, Map<String, dynamic> hello) async {
    IOWebSocketChannel? ch;
    try {
      ch = IOWebSocketChannel.connect(
        Uri.parse('ws://$host:$port/ws'),
        connectTimeout: const Duration(seconds: 3),
        pingInterval: const Duration(seconds: 10),
      );
      final c = ch;
      await c.ready.timeout(const Duration(seconds: 4));
      final first = Completer<Map<String, dynamic>>();
      c.stream.listen(
        (data) {
          try {
            final m = jsonDecode(data as String) as Map<String, dynamic>;
            final t = m['t'];
            if (!first.isCompleted && (t == 'auth_ok' || t == 'auth_fail' || t == 'paired' || t == 'pair_fail')) {
              first.complete(m);
            } else {
              _onMessage(m);
            }
          } catch (_) {}
        },
        onError: (Object e) {
          if (!first.isCompleted) first.completeError(e);
        },
        onDone: () {
          if (!first.isCompleted) first.completeError('closed');
          if (identical(_ch, c)) _onDisconnected();
        },
        cancelOnError: true,
      );
      c.sink.add(jsonEncode(hello));
      final reply = await first.future.timeout(const Duration(seconds: 5));
      final ok = reply['t'] == 'auth_ok' || reply['t'] == 'paired';
      if (ok) {
        _drop(); // replace any previous socket
        _ch = c;
      } else {
        await c.sink.close();
      }
      return reply;
    } catch (_) {
      try {
        await ch?.sink.close();
      } catch (_) {}
      return null;
    }
  }

  void _drop() {
    final c = _ch;
    _ch = null;
    try {
      c?.sink.close();
    } catch (_) {}
    for (final p in _pending.values) {
      if (!p.isCompleted) p.completeError('disconnected');
    }
    _pending.clear();
  }

  void _onDisconnected() {
    _ch = null;
    state = LinkState.disconnected;
    connectedVia = null;
    for (final p in _pending.values) {
      if (!p.isCompleted) p.completeError('disconnected');
    }
    _pending.clear();
    notifyListeners();
    nudge();
  }

  // ------------------------------------------------------------ pairing
  /// Pairs using a scanned QR (or manual) payload. Returns an error message, or null on success.
  Future<String?> pair(PairQr qr) async {
    _busy = true;
    needsRepair = false;
    try {
      final hosts = <String>[...qr.hosts, '127.0.0.1'];
      String? err;
      for (final h in hosts) {
        final r = await _handshake(h, qr.port, {
          't': 'pair',
          'secret': qr.secret,
          'deviceId': app.deviceId,
          'deviceName': app.deviceName,
        });
        if (r == null) continue;
        if (r['t'] == 'paired') {
          final pc = PairedPc(
            name: (r['host'] ?? qr.name) as String,
            hosts: qr.hosts,
            port: qr.port,
            deviceId: app.deviceId,
            token: r['token'] as String,
            lastHost: h,
          );
          app.addPc(pc);
          hostName = pc.name;
          connectedVia = h;
          state = LinkState.connected;
          lastError = null;
          notifyListeners();
          return null;
        }
        err = (r['error'] ?? 'Pairing rejected') as String;
        break;
      }
      return err ?? 'Could not reach the PC. Same Wi-Fi? Is StreamDeck Host running?';
    } finally {
      _busy = false;
      nudge();
    }
  }

  // ------------------------------------------------------------ messages
  void _onMessage(Map<String, dynamic> m) {
    final id = m['id'];
    if (id is int && _pending.containsKey(id)) {
      _pending.remove(id)!.complete(m);
      return;
    }
    if (m['t'] == 'status') {
      status = PcStatus.fromJson(m);
      notifyListeners();
    }
  }

  Future<Map<String, dynamic>> request(Map<String, dynamic> msg, {Duration timeout = const Duration(seconds: 15)}) {
    final ch = _ch;
    if (ch == null || !connected) return Future.error('Not connected to a PC');
    final id = _nextId++;
    final c = Completer<Map<String, dynamic>>();
    _pending[id] = c;
    ch.sink.add(jsonEncode({...msg, 'id': id}));
    return c.future.timeout(timeout, onTimeout: () {
      _pending.remove(id);
      throw 'The PC did not answer in time';
    });
  }

  Future<ActionResult> run(ActionSpec action) async {
    try {
      final r = await request({'t': 'action', 'action': action.wire(app.macros)},
          timeout: const Duration(minutes: 2));
      if (r['ok'] == true) return ActionResult(true, message: r['message'] as String?);
      return ActionResult(false, error: (r['error'] ?? 'Failed') as String);
    } catch (e) {
      return ActionResult(false, error: e.toString());
    }
  }

  Future<List<Map<String, dynamic>>> apps({bool refresh = false}) async {
    final r = await request({'t': 'apps', 'refresh': refresh}, timeout: const Duration(seconds: 60));
    return List<Map<String, dynamic>>.from((r['apps'] ?? []) as List);
  }

  Future<List<Map<String, dynamic>>> wallpapers() async {
    final r = await request({'t': 'wallpapers'}, timeout: const Duration(seconds: 30));
    return List<Map<String, dynamic>>.from((r['items'] ?? []) as List);
  }

  Future<ActionResult> uploadWallpaper(String name, List<int> bytes, {bool apply = true}) async {
    try {
      final r = await request({'t': 'wallpaper_upload', 'name': name, 'data': base64Encode(bytes), 'apply': apply},
          timeout: const Duration(minutes: 2));
      return r['ok'] == true ? const ActionResult(true) : ActionResult(false, error: (r['error'] ?? 'Upload failed') as String);
    } catch (e) {
      return ActionResult(false, error: e.toString());
    }
  }

  Future<bool> deleteWallpaper(String name) async {
    try {
      final r = await request({'t': 'wallpaper_delete', 'name': name});
      return r['ok'] == true;
    } catch (_) {
      return false;
    }
  }
}
