import 'package:flutter_test/flutter_test.dart';
import 'package:streamdeck_mobile/models.dart';

void main() {
  test('macro actions are expanded inline for the PC', () {
    final m = Macro(name: 'm', steps: [
      ActionSpec('volume_set', {'value': 20}),
      ActionSpec('delay', {'ms': 100}),
    ]);
    final wire = ActionSpec('macro', {'macroId': m.id}).wire([m]);
    expect(wire['type'], 'macro');
    expect((wire['steps'] as List).length, 2);
    expect((wire['steps'] as List).first, {'type': 'volume_set', 'value': 20});
  });

  test('buttons round-trip through JSON', () {
    final b = DeckButton(label: 'X', action: ActionSpec('hotkey', {'keys': 'win+d'}), color1: 0xFF112233, glow: 0.3);
    final r = DeckButton.fromJson(b.toJson());
    expect(r.label, 'X');
    expect(r.color1, 0xFF112233);
    expect(r.action.params['keys'], 'win+d');
  });

  test('pages keep buttons when resized', () {
    final p = DeckPage(name: 'p', cols: 4, rows: 4);
    p.slots[15] = DeckButton(label: 'last', action: ActionSpec('lock'));
    p.resize(2, 2);
    expect(p.slots.length, 4);
    expect(p.slots.whereType<DeckButton>().length, 1);
    p.resize(4, 4);
    expect(p.slots.length, 16);
  });
}
