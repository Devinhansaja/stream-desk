import 'dart:convert';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

enum LinkMode { auto, wifi, usb }

String newId() {
  final r = Random.secure();
  return List.generate(16, (_) => r.nextInt(16).toRadixString(16)).join();
}

class PairedPc {
  String name;
  List<String> hosts;
  int port;
  String deviceId;
  String token;
  String? lastHost;
  PairedPc({required this.name, required this.hosts, required this.port, required this.deviceId, required this.token, this.lastHost});

  Map<String, dynamic> toJson() =>
      {'name': name, 'hosts': hosts, 'port': port, 'deviceId': deviceId, 'token': token, 'lastHost': lastHost};
  factory PairedPc.fromJson(Map j) => PairedPc(
        name: j['name'] as String,
        hosts: List<String>.from(j['hosts'] as List),
        port: j['port'] as int,
        deviceId: j['deviceId'] as String,
        token: j['token'] as String,
        lastHost: j['lastHost'] as String?,
      );
}

/// Only connection data lives on the phone. Buttons and layout are edited on the PC and streamed to the phone.
class AppState extends ChangeNotifier {
  late SharedPreferences _prefs;
  List<PairedPc> pcs = [];
  int activePcIndex = 0;
  LinkMode linkMode = LinkMode.auto;
  bool haptics = true;
  String deviceName = 'My Android';
  String deviceId = '';

  PairedPc? get activePc => pcs.isEmpty ? null : pcs[activePcIndex.clamp(0, pcs.length - 1)];

  Future<void> load() async {
    _prefs = await SharedPreferences.getInstance();
    deviceId = _prefs.getString('deviceId') ?? '';
    if (deviceId.isEmpty) {
      deviceId = 'dev-${newId()}';
      await _prefs.setString('deviceId', deviceId);
    }
    final raw = _prefs.getString('conn_v2');
    if (raw != null) {
      try {
        final j = jsonDecode(raw) as Map<String, dynamic>;
        pcs = ((j['pcs'] ?? []) as List).map((p) => PairedPc.fromJson(p as Map)).toList();
        activePcIndex = (j['activePc'] ?? 0) as int;
        linkMode = LinkMode.values[((j['linkMode'] ?? 0) as int).clamp(0, 2)];
        haptics = (j['haptics'] ?? true) as bool;
        deviceName = (j['deviceName'] ?? deviceName) as String;
      } catch (e) {
        debugPrint('state load failed: $e');
      }
    }
    notifyListeners();
  }

  Future<void> save() => _prefs.setString(
        'conn_v2',
        jsonEncode({
          'pcs': pcs.map((p) => p.toJson()).toList(),
          'activePc': activePcIndex,
          'linkMode': linkMode.index,
          'haptics': haptics,
          'deviceName': deviceName,
        }),
      );

  void changed() {
    notifyListeners();
    save();
  }

  void addPc(PairedPc pc) {
    pcs.removeWhere((p) => p.name == pc.name);
    pcs.add(pc);
    activePcIndex = pcs.length - 1;
    changed();
  }

  void removePc(int i) {
    pcs.removeAt(i);
    activePcIndex = 0;
    changed();
  }
}
