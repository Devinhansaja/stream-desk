import 'package:flutter_test/flutter_test.dart';
import 'package:streamdeck_mobile/connection.dart';

void main() {
  test('QR payload from the PC parses', () {
    final qr = PairQr.parse('{"v":1,"n":"HANSAJA","p":47800,"h":["192.168.1.5"],"s":"abc"}');
    expect(qr, isNotNull);
    expect(qr!.name, 'HANSAJA');
    expect(qr.port, 47800);
    expect(qr.hosts, ['192.168.1.5']);
    expect(qr.secret, 'abc');
  });

  test('garbage QR is rejected', () {
    expect(PairQr.parse('hello'), isNull);
  });
}
