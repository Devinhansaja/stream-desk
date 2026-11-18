import 'dart:convert';
import 'dart:math';

String newId() {
  final r = Random.secure();
  return List.generate(16, (_) => r.nextInt(16).toRadixString(16)).join();
}

/// A single PC action: {type, params}. Everything a button does is data like this.
class ActionSpec {
  String type;
  Map<String, dynamic> params;
  ActionSpec(this.type, [Map<String, dynamic>? params]) : params = params ?? {};

  Map<String, dynamic> toJson() => {'type': type, 'params': params};
  factory ActionSpec.fromJson(Map j) =>
      ActionSpec(j['type'] as String, Map<String, dynamic>.from((j['params'] ?? {}) as Map));
  ActionSpec copy() => ActionSpec.fromJson(jsonDecode(jsonEncode(toJson())) as Map);

  /// Message sent to the PC. Macros are expanded inline so the PC needs no macro library.
  Map<String, dynamic> wire(List<Macro> macros, [int depth = 0]) {
    if (type == 'macro') {
      final id = params['macroId'];
      final m = macros.where((m) => m.id == id).toList();
      final steps = (depth > 4 || m.isEmpty) ? <ActionSpec>[] : m.first.steps;
      return {'type': 'macro', 'steps': steps.map((s) => s.wire(macros, depth + 1)).toList()};
    }
    return {'type': type, ...params};
  }
}

class Macro {
  String id;
  String name;
  List<ActionSpec> steps;
  Macro({String? id, required this.name, List<ActionSpec>? steps})
      : id = id ?? newId(),
        steps = steps ?? [];

  Map<String, dynamic> toJson() => {'id': id, 'name': name, 'steps': steps.map((s) => s.toJson()).toList()};
  factory Macro.fromJson(Map j) => Macro(
        id: j['id'] as String,
        name: j['name'] as String,
        steps: ((j['steps'] ?? []) as List).map((s) => ActionSpec.fromJson(s as Map)).toList(),
      );
}

class DeckButton {
  String id;
  String label;

  /// Icon key from the icon registry, "t:<text or emoji>" for a glyph, or "image" to use [imagePath].
  String icon;
  String? imagePath;
  int color1; // ARGB, border + gradient start
  int color2; // ARGB, gradient end
  double glow; // 0..1
  bool confirm;
  ActionSpec action;

  DeckButton({
    String? id,
    required this.label,
    this.icon = 'bolt',
    this.imagePath,
    this.color1 = 0xFF1E90FF,
    int? color2,
    this.glow = 0.6,
    this.confirm = false,
    required this.action,
  })  : id = id ?? newId(),
        color2 = color2 ?? 0xFF7C4DFF;

  Map<String, dynamic> toJson() => {
        'id': id,
        'label': label,
        'icon': icon,
        'imagePath': imagePath,
        'color1': color1,
        'color2': color2,
        'glow': glow,
        'confirm': confirm,
        'action': action.toJson(),
      };

  factory DeckButton.fromJson(Map j) => DeckButton(
        id: j['id'] as String,
        label: (j['label'] ?? '') as String,
        icon: (j['icon'] ?? 'bolt') as String,
        imagePath: j['imagePath'] as String?,
        color1: (j['color1'] ?? 0xFF1E90FF) as int,
        color2: (j['color2'] ?? 0xFF7C4DFF) as int,
        glow: ((j['glow'] ?? 0.6) as num).toDouble(),
        confirm: (j['confirm'] ?? false) as bool,
        action: ActionSpec.fromJson(j['action'] as Map),
      );

  DeckButton clone() => DeckButton.fromJson(jsonDecode(jsonEncode(toJson())) as Map)..id = newId();
}

class DeckPage {
  String id;
  String name;
  int cols;
  int rows;
  List<DeckButton?> slots;

  DeckPage({String? id, required this.name, this.cols = 4, this.rows = 4, List<DeckButton?>? slots})
      : id = id ?? newId(),
        slots = slots ?? List<DeckButton?>.filled(cols * rows, null, growable: true) {
    _fit();
  }

  void _fit() {
    final n = cols * rows;
    while (slots.length < n) {
      slots.add(null);
    }
    // Shrinking keeps buttons: they are compacted into the remaining slots where possible.
    if (slots.length > n) {
      final keep = slots.whereType<DeckButton>().toList();
      slots = List<DeckButton?>.filled(n, null, growable: true);
      for (var i = 0; i < keep.length && i < n; i++) {
        slots[i] = keep[i];
      }
    }
  }

  void resize(int c, int r) {
    cols = c;
    rows = r;
    _fit();
  }

  Map<String, dynamic> toJson() => {
        'id': id,
        'name': name,
        'cols': cols,
        'rows': rows,
        'slots': slots.map((s) => s?.toJson()).toList(),
      };

  factory DeckPage.fromJson(Map j) => DeckPage(
        id: j['id'] as String,
        name: j['name'] as String,
        cols: (j['cols'] ?? 4) as int,
        rows: (j['rows'] ?? 4) as int,
        slots: ((j['slots'] ?? []) as List).map((s) => s == null ? null : DeckButton.fromJson(s as Map)).toList(),
      );
}

class Profile {
  String id;
  String name;
  List<DeckPage> pages;
  Profile({String? id, required this.name, List<DeckPage>? pages})
      : id = id ?? newId(),
        pages = pages ?? [DeckPage(name: 'Page 1')];

  Map<String, dynamic> toJson() => {'id': id, 'name': name, 'pages': pages.map((p) => p.toJson()).toList()};
  factory Profile.fromJson(Map j) => Profile(
        id: j['id'] as String,
        name: j['name'] as String,
        pages: ((j['pages'] ?? []) as List).map((p) => DeckPage.fromJson(p as Map)).toList(),
      );
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

class PcStatus {
  final int volume;
  final bool muted;
  final bool? micMuted;
  final int brightness; // -1 = unsupported
  const PcStatus({this.volume = 0, this.muted = false, this.micMuted, this.brightness = -1});
  factory PcStatus.fromJson(Map j) => PcStatus(
        volume: ((j['volume'] ?? 0) as num).toInt(),
        muted: (j['muted'] ?? false) as bool,
        micMuted: j['micMuted'] as bool?,
        brightness: ((j['brightness'] ?? -1) as num).toInt(),
      );
}
