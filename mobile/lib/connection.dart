import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:web_socket_channel/io.dart';

import 'store.dart';

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

class DiscoveredPc {
  final String ip;
  final String name;
  final int port;
  const DiscoveredPc(this.ip, this.name, this.port);
}

/// Finding and pairing with the PC. The deck itself runs inside a WebView served by the PC.
class HostLink {
  static const discoveryPort = 47801;

  /// True when a StreamDeck Host answers on [host]:[port].
  static Future<bool> probe(String host, int port) async {
    final c = HttpClient()..connectionTimeout = const Duration(seconds: 2);
    try {
      final req = await c.getUrl(Uri.parse('http://$host:$port/')).timeout(const Duration(seconds: 3));
      final resp = await req.close().timeout(const Duration(seconds: 3));
      final body = await resp.transform(utf8.decoder).join().timeout(const Duration(seconds: 3));
      return body.contains('StreamDeck Host');
    } catch (_) {
      return false;
    } finally {
      c.close(force: true);
    }
  }

  /// First host (from [hosts]) that answers; probes all in parallel.
  static Future<String?> firstReachable(List<String> hosts, int port) async {
    if (hosts.isEmpty) return null;
    final done = Completer<String?>();
    var pending = hosts.length;
    for (final h in hosts) {
      probe(h, port).then((ok) {
        if (ok && !done.isCompleted) done.complete(h);
        if (--pending == 0 && !done.isCompleted) done.complete(null);
      });
    }
    return done.future;
  }

  static Future<List<DiscoveredPc>> discover({Duration wait = const Duration(milliseconds: 1500)}) async {
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
              found[d.address.address] = DiscoveredPc(d.address.address, (j['name'] ?? 'PC') as String, (j['port'] ?? 47800) as int);
            }
          } catch (_) {}
        }
      });
      final msg = utf8.encode('STREAMDECK_DISCOVER');
      sock.send(msg, InternetAddress('255.255.255.255'), discoveryPort);
      await Future.delayed(const Duration(milliseconds: 250));
      sock.send(msg, InternetAddress('255.255.255.255'), discoveryPort);
      await Future.delayed(wait);
    } catch (_) {
    } finally {
      sock?.close();
    }
    return found.values.toList();
  }

  /// Returns the reachable host for [pc] (USB tunnel first, then known addresses, then network discovery).
  static Future<String?> resolve(PairedPc pc, LinkMode mode) async {
    final order = <String>[];
    void add(String? h) {
      if (h != null && h.isNotEmpty && !order.contains(h)) order.add(h);
    }

    if (mode != LinkMode.wifi) add('127.0.0.1');
    if (mode != LinkMode.usb) {
      add(pc.lastHost);
      pc.hosts.forEach(add);
    }
    var host = await firstReachable(order, pc.port);
    if (host == null && mode != LinkMode.usb) {
      final found = await discover();
      final match = found.where((d) => d.name == pc.name).toList();
      final cands = (match.isNotEmpty ? match : found).map((d) => d.ip).toList();
      host = await firstReachable(cands, pc.port);
      if (host != null && !pc.hosts.contains(host)) pc.hosts.add(host);
    }
    return host;
  }

  /// Pairs using the scanned QR payload. Returns null on success, otherwise a human-readable reason.
  static Future<String?> pair(PairQr qr, AppState app) async {
    final hosts = <String>[...qr.hosts, '127.0.0.1'];
    final host = await firstReachable(hosts, qr.port);
    if (host == null) {
      return 'Could not reach your PC at ${qr.hosts.isEmpty ? "(no address in QR)" : qr.hosts.join(", ")}.\n\n'
          '• Phone and PC must be on the same Wi-Fi (not a guest network).\n'
          '• In StreamDeck Host on the PC press "Fix firewall".\n'
          '• Or plug in USB with USB debugging on and use the manual option with 127.0.0.1.';
    }
    IOWebSocketChannel? ch;
    try {
      ch = IOWebSocketChannel.connect(Uri.parse('ws://$host:${qr.port}/ws'), connectTimeout: const Duration(seconds: 4));
      final c = ch;
      await c.ready.timeout(const Duration(seconds: 5));
      c.sink.add(jsonEncode({'t': 'pair', 'secret': qr.secret, 'deviceId': app.deviceId, 'deviceName': app.deviceName}));
      await for (final data in c.stream.timeout(const Duration(seconds: 8))) {
        final m = jsonDecode(data as String) as Map<String, dynamic>;
        if (m['t'] == 'paired') {
          final hostName = (m['host'] ?? qr.name) as String;
          app.addPc(PairedPc(
            name: hostName,
            hosts: qr.hosts,
            port: qr.port,
            deviceId: app.deviceId,
            token: m['token'] as String,
            lastHost: host,
          ));
          await c.sink.close();
          return null;
        }
        if (m['t'] == 'pair_fail') {
          await c.sink.close();
          return '${m['error'] ?? 'Pairing rejected'}. Press "New pairing code" on the PC and scan again.';
        }
      }
      return 'The PC closed the connection.';
    } on TimeoutException {
      return 'The PC did not answer in time.';
    } catch (e) {
      return 'Connection error: $e';
    } finally {
      try {
        await ch?.sink.close();
      } catch (_) {}
    }
  }
}
