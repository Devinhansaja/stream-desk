import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../connection.dart';
import '../store.dart';
import '../widgets.dart';

/// Pair with a PC: scan the QR shown in StreamDeck Host, or enter IP + code manually.
class PairScreen extends StatefulWidget {
  const PairScreen({super.key, required this.app});
  final AppState app;
  @override
  State<PairScreen> createState() => _PairScreenState();
}

class _PairScreenState extends State<PairScreen> {
  final MobileScannerController _scanner = MobileScannerController(formats: const [BarcodeFormat.qrCode]);
  bool _busy = false;
  bool _manual = false;
  String? _error;
  final _ip = TextEditingController();
  final _code = TextEditingController();
  final _port = TextEditingController(text: '47800');

  @override
  void dispose() {
    _scanner.dispose();
    _ip.dispose();
    _code.dispose();
    _port.dispose();
    super.dispose();
  }

  Future<void> _doPair(PairQr qr) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    final err = await HostLink.pair(qr, widget.app);
    if (!mounted) return;
    if (err == null) {
      Navigator.pop(context, true);
    } else {
      setState(() {
        _busy = false;
        _error = err;
      });
    }
  }

  void _onDetect(BarcodeCapture cap) {
    if (_busy) return;
    for (final b in cap.barcodes) {
      final raw = b.rawValue;
      if (raw == null) continue;
      final qr = PairQr.parse(raw);
      if (qr != null) {
        _doPair(qr);
        return;
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Pair with PC'), actions: [
        TextButton(onPressed: () => setState(() => _manual = !_manual), child: Text(_manual ? 'Scan QR' : 'Enter manually')),
      ]),
      body: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(children: [
          if (!_manual) ...[
            const Text('On your PC open StreamDeck Host → Pairing, then point the camera at the QR code.', textAlign: TextAlign.center, style: TextStyle(color: Neon.muted)),
            const SizedBox(height: 12),
            Expanded(
              child: ClipRRect(
                borderRadius: BorderRadius.circular(20),
                child: Stack(fit: StackFit.expand, children: [
                  MobileScanner(
                    controller: _scanner,
                    onDetect: _onDetect,
                    errorBuilder: (ctx, err, _) => Center(child: Padding(padding: const EdgeInsets.all(24), child: Text('Camera unavailable: ${err.errorCode.name}\nUse "Enter manually" instead.', textAlign: TextAlign.center))),
                  ),
                  IgnorePointer(child: Center(child: Container(width: 230, height: 230, decoration: BoxDecoration(border: Border.all(color: Neon.purple, width: 3), borderRadius: BorderRadius.circular(20))))),
                  if (_busy) Container(color: Colors.black54, child: const Center(child: CircularProgressIndicator())),
                ]),
              ),
            ),
          ] else
            Expanded(
              child: ListView(children: [
                const Text('Use this if the camera is unavailable. Over USB, use 127.0.0.1.', style: TextStyle(color: Neon.muted)),
                const SizedBox(height: 14),
                TextField(controller: _ip, decoration: const InputDecoration(labelText: 'PC IP address', hintText: '192.168.1.20')),
                const SizedBox(height: 10),
                TextField(controller: _port, keyboardType: TextInputType.number, decoration: const InputDecoration(labelText: 'Port')),
                const SizedBox(height: 10),
                TextField(controller: _code, decoration: const InputDecoration(labelText: 'Pairing code (shown on PC)')),
                const SizedBox(height: 16),
                FilledButton(
                  onPressed: _busy ? null : () => _doPair(PairQr('PC', int.tryParse(_port.text) ?? 47800, [_ip.text.trim()], _code.text.trim())),
                  child: _busy ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)) : const Text('Pair'),
                ),
              ]),
            ),
          if (_error != null)
            Container(
              margin: const EdgeInsets.only(top: 12),
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(color: const Color(0x33FF1744), borderRadius: BorderRadius.circular(12)),
              child: Text(_error!, style: const TextStyle(color: Color(0xFFFF8A9A))),
            ),
        ]),
      ),
    );
  }
}
