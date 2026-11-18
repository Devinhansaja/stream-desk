import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'models.dart';

enum LinkMode { auto, wifi, usb }

/// All user data: profiles, pages, buttons, macros, paired PCs, preferences. Persisted as JSON.
class AppState extends ChangeNotifier {
  late SharedPreferences _prefs;
  late Directory imagesDir;

  List<Profile> profiles = [];
  String currentProfileId = '';
  int pageIndex = 0;
  List<Macro> macros = [];
  List<PairedPc> pcs = [];
  int activePcIndex = 0;
  LinkMode linkMode = LinkMode.auto;
  bool haptics = true;
  bool editMode = false;
  String deviceName = 'My Android';
  String deviceId = '';

  Profile get profile => profiles.firstWhere((p) => p.id == currentProfileId, orElse: () => profiles.first);
  DeckPage get page => profile.pages[pageIndex.clamp(0, profile.pages.length - 1)];
  PairedPc? get activePc => pcs.isEmpty ? null : pcs[activePcIndex.clamp(0, pcs.length - 1)];

  Future<void> load() async {
    _prefs = await SharedPreferences.getInstance();
    final docs = await getApplicationDocumentsDirectory();
    imagesDir = Directory('${docs.path}/images');
    if (!await imagesDir.exists()) await imagesDir.create(recursive: true);

    deviceId = _prefs.getString('deviceId') ?? '';
    if (deviceId.isEmpty) {
      deviceId = 'dev-${newId()}';
      await _prefs.setString('deviceId', deviceId);
    }

    final raw = _prefs.getString('state_v1');
    if (raw != null) {
      try {
        final j = jsonDecode(raw) as Map<String, dynamic>;
        profiles = (j['profiles'] as List).map((p) => Profile.fromJson(p as Map)).toList();
        currentProfileId = (j['current'] ?? '') as String;
        pageIndex = (j['page'] ?? 0) as int;
        macros = ((j['macros'] ?? []) as List).map((m) => Macro.fromJson(m as Map)).toList();
        pcs = ((j['pcs'] ?? []) as List).map((p) => PairedPc.fromJson(p as Map)).toList();
        activePcIndex = (j['activePc'] ?? 0) as int;
        linkMode = LinkMode.values[((j['linkMode'] ?? 0) as int).clamp(0, 2)];
        haptics = (j['haptics'] ?? true) as bool;
        deviceName = (j['deviceName'] ?? deviceName) as String;
      } catch (e) {
        debugPrint('state load failed: $e');
      }
    }
    if (profiles.isEmpty) {
      profiles = [defaultProfile()];
      currentProfileId = profiles.first.id;
      macros = [
        Macro(name: 'Gaming mode', steps: [
          ActionSpec('volume_set', {'value': 60}),
          ActionSpec('hotkey', {'keys': 'win+d'}),
        ]),
      ];
    }
    if (!profiles.any((p) => p.id == currentProfileId)) currentProfileId = profiles.first.id;
    notifyListeners();
  }

  Future<void> save() async {
    final j = {
      'profiles': profiles.map((p) => p.toJson()).toList(),
      'current': currentProfileId,
      'page': pageIndex,
      'macros': macros.map((m) => m.toJson()).toList(),
      'pcs': pcs.map((p) => p.toJson()).toList(),
      'activePc': activePcIndex,
      'linkMode': linkMode.index,
      'haptics': haptics,
      'deviceName': deviceName,
    };
    await _prefs.setString('state_v1', jsonEncode(j));
  }

  void changed() {
    notifyListeners();
    save();
  }

  // ---------------- pages / profiles ----------------
  void setPage(int i) {
    pageIndex = i.clamp(0, profile.pages.length - 1);
    changed();
  }

  void addPage() {
    profile.pages.add(DeckPage(name: 'Page ${profile.pages.length + 1}'));
    pageIndex = profile.pages.length - 1;
    changed();
  }

  void deletePage(int i) {
    if (profile.pages.length <= 1) return;
    profile.pages.removeAt(i);
    pageIndex = pageIndex.clamp(0, profile.pages.length - 1);
    changed();
  }

  void selectProfile(String id) {
    currentProfileId = id;
    pageIndex = 0;
    changed();
  }

  void addProfile(String name) {
    final p = Profile(name: name);
    profiles.add(p);
    selectProfile(p.id);
  }

  void deleteProfile(String id) {
    if (profiles.length <= 1) return;
    profiles.removeWhere((p) => p.id == id);
    if (currentProfileId == id) currentProfileId = profiles.first.id;
    pageIndex = 0;
    changed();
  }

  // ---------------- buttons ----------------
  void setButton(int slot, DeckButton? b) {
    page.slots[slot] = b;
    changed();
  }

  void swapSlots(int a, int b) {
    final s = page.slots;
    final t = s[a];
    s[a] = s[b];
    s[b] = t;
    changed();
  }

  /// Places a new button into the first empty slot of the current page (adds a page if full).
  void addButtonToFirstFree(DeckButton b) {
    var idx = page.slots.indexWhere((s) => s == null);
    if (idx < 0) {
      addPage();
      idx = 0;
    }
    page.slots[idx] = b;
    changed();
  }

  // ---------------- macros ----------------
  void upsertMacro(Macro m) {
    final i = macros.indexWhere((x) => x.id == m.id);
    if (i >= 0) {
      macros[i] = m;
    } else {
      macros.add(m);
    }
    changed();
  }

  void deleteMacro(String id) {
    macros.removeWhere((m) => m.id == id);
    changed();
  }

  // ---------------- pcs ----------------
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

  // ---------------- images ----------------
  Future<String> saveImageBytes(List<int> bytes, {String ext = 'png'}) async {
    final f = File('${imagesDir.path}/${newId()}.$ext');
    await f.writeAsBytes(bytes);
    return f.path;
  }

  Future<String> copyImage(String src) async {
    final ext = src.contains('.') ? src.split('.').last.toLowerCase() : 'png';
    return saveImageBytes(await File(src).readAsBytes(), ext: ext.length > 4 ? 'png' : ext);
  }
}

/// First-run starter layout (editable data, not hard-coded UI).
Profile defaultProfile() {
  DeckButton b(String label, String icon, int c1, int c2, ActionSpec a, {bool confirm = false}) =>
      DeckButton(label: label, icon: icon, color1: c1, color2: c2, action: a, confirm: confirm);

  final p1 = DeckPage(name: 'Home', slots: [
    b('Volume Up', 'volume_up', 0xFF1E90FF, 0xFF00E5FF, ActionSpec('volume_up', {'step': 5})),
    b('Volume Down', 'volume_down', 0xFF1E90FF, 0xFF00E5FF, ActionSpec('volume_down', {'step': 5})),
    b('Mute', 'volume_off', 0xFFFF1744, 0xFFB71C1C, ActionSpec('mute_toggle')),
    b('Mic Mute', 'mic_off', 0xFF7C4DFF, 0xFFD500F9, ActionSpec('mic_toggle')),
    b('Brightness +', 'sun', 0xFFFFC400, 0xFFFF6D00, ActionSpec('brightness_up', {'step': 10})),
    b('Brightness -', 'brightness_low', 0xFFFFC400, 0xFF8D6E00, ActionSpec('brightness_down', {'step': 10})),
    b('Screenshot', 'camera', 0xFF7C4DFF, 0xFF1E90FF, ActionSpec('screenshot')),
    b('Lock PC', 'lock', 0xFF1E90FF, 0xFF0D47A1, ActionSpec('lock')),
    b('Shutdown', 'power', 0xFF00E676, 0xFF1B5E20, ActionSpec('shutdown'), confirm: true),
    b('Restart', 'restart', 0xFF00E5FF, 0xFF0277BD, ActionSpec('restart'), confirm: true),
    b('Open Chrome', 'web', 0xFF7C4DFF, 0xFF1E90FF, ActionSpec('url', {'url': 'https://www.google.com'})),
    b('Task Manager', 'speed', 0xFFB0BEC5, 0xFF455A64, ActionSpec('hotkey', {'keys': 'ctrl+shift+esc'})),
    b('Change BG', 'image', 0xFF1E90FF, 0xFF00E676, ActionSpec('wallpaper', {'mode': 'next'})),
    b('Play / Pause', 'play', 0xFFFF1744, 0xFFB71C1C, ActionSpec('media', {'key': 'playpause'})),
    b('Show Desktop', 'desktop', 0xFFB0BEC5, 0xFF455A64, ActionSpec('hotkey', {'keys': 'win+d'})),
    null,
  ]);
  final p2 = DeckPage(name: 'Games');
  final p3 = DeckPage(name: 'Page 3');
  return Profile(name: 'Default', pages: [p1, p2, p3]);
}
