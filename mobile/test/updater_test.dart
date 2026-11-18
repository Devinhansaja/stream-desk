import 'package:flutter_test/flutter_test.dart';
import 'package:streamdeck_mobile/updater.dart';

void main() {
  test('version comparison', () {
    expect(isNewer('v1.2.3', '1.2.2'), isTrue);
    expect(isNewer('1.3.0', '1.2.9'), isTrue);
    expect(isNewer('v2.0.0', '1.9.9'), isTrue);
    expect(isNewer('1.2.2', '1.2.2'), isFalse);
    expect(isNewer('v1.1.0', '1.2.2'), isFalse);
  });
}
