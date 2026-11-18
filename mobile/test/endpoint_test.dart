import 'package:flutter_test/flutter_test.dart';
import 'package:streamdeck_mobile/connection.dart';

void main() {
  test('plain IP uses default port over http', () {
    final e = Endpoint.parse('192.168.1.20', 47800);
    expect(e.httpBase, 'http://192.168.1.20:47800');
    expect(e.wsBase, 'ws://192.168.1.20:47800');
  });

  test('host:port', () {
    expect(Endpoint.parse('myhome.duckdns.org:47800', 1).httpBase, 'http://myhome.duckdns.org:47800');
  });

  test('https URL uses TLS on 443', () {
    final e = Endpoint.parse('https://abc.trycloudflare.com/', 47800);
    expect(e.httpBase, 'https://abc.trycloudflare.com');
    expect(e.wsBase, 'wss://abc.trycloudflare.com');
  });

  test('https with explicit port', () {
    expect(Endpoint.parse('https://x.example.com:8443', 47800).wsBase, 'wss://x.example.com:8443');
  });
}
