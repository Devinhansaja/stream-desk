import 'dart:io';

import 'package:flutter/material.dart';

import 'catalog.dart';
import 'models.dart';

class Neon {
  static const bg = Color(0xFF05050F);
  static const panel = Color(0xFF0E0E24);
  static const panel2 = Color(0xFF16163A);
  static const purple = Color(0xFF7C4DFF);
  static const blue = Color(0xFF1E90FF);
  static const green = Color(0xFF22E58B);
  static const text = Color(0xFFEDEDFB);
  static const muted = Color(0xFF9696BD);
}

ThemeData neonTheme() {
  final base = ThemeData.dark(useMaterial3: true);
  return base.copyWith(
    scaffoldBackgroundColor: Neon.bg,
    colorScheme: const ColorScheme.dark(
      primary: Neon.purple,
      secondary: Neon.blue,
      surface: Neon.panel,
    ),
    cardColor: Neon.panel,
    dialogBackgroundColor: Neon.panel,
    appBarTheme: const AppBarTheme(backgroundColor: Neon.bg, elevation: 0),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: Neon.panel2,
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide.none),
    ),
    snackBarTheme: const SnackBarThemeData(behavior: SnackBarBehavior.floating),
  );
}

/// Soft neon container used for panels (gradient border + dark fill).
class NeonPanel extends StatelessWidget {
  const NeonPanel({super.key, required this.child, this.padding = const EdgeInsets.all(14), this.radius = 18, this.glow = true});
  final Widget child;
  final EdgeInsets padding;
  final double radius;
  final bool glow;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(radius),
        gradient: const LinearGradient(colors: [Color(0x884B2BFF), Color(0x551E90FF)], begin: Alignment.topLeft, end: Alignment.bottomRight),
        boxShadow: glow ? [BoxShadow(color: Neon.purple.withAlpha(40), blurRadius: 18)] : null,
      ),
      padding: const EdgeInsets.all(1.2),
      child: Container(
        padding: padding,
        decoration: BoxDecoration(color: Neon.panel, borderRadius: BorderRadius.circular(radius - 1)),
        child: child,
      ),
    );
  }
}

/// Renders a button's icon: registry icon, "t:glyph" text/emoji, or a custom image file.
class ButtonGlyph extends StatelessWidget {
  const ButtonGlyph({super.key, required this.icon, this.imagePath, required this.color, this.size = 40});
  final String icon;
  final String? imagePath;
  final Color color;
  final double size;

  @override
  Widget build(BuildContext context) {
    if (icon == 'image' && imagePath != null && File(imagePath!).existsSync()) {
      return ClipRRect(
        borderRadius: BorderRadius.circular(size * 0.2),
        child: Image.file(File(imagePath!), width: size * 1.15, height: size * 1.15, fit: BoxFit.cover, errorBuilder: (_, __, ___) => Icon(Icons.broken_image, size: size, color: color)),
      );
    }
    if (icon.startsWith('t:')) {
      return Text(icon.substring(2), style: TextStyle(fontSize: size * 0.9, color: color, fontWeight: FontWeight.bold, height: 1.0));
    }
    return Icon(kIcons[icon] ?? Icons.bolt, size: size, color: color, shadows: [Shadow(color: color.withAlpha(180), blurRadius: 14)]);
  }
}

/// A glowing deck key. All appearance comes from the [DeckButton] data.
class NeonKey extends StatefulWidget {
  const NeonKey({super.key, required this.button, this.onTap, this.active = false, this.dim = false});
  final DeckButton button;
  final VoidCallback? onTap;
  final bool active;
  final bool dim;

  @override
  State<NeonKey> createState() => _NeonKeyState();
}

class _NeonKeyState extends State<NeonKey> {
  bool _down = false;

  @override
  Widget build(BuildContext context) {
    final b = widget.button;
    final c1 = Color(b.color1);
    final c2 = Color(b.color2);
    final glow = (b.glow * (widget.active ? 1.4 : 1.0)).clamp(0.0, 1.0);
    return GestureDetector(
      onTapDown: (_) => setState(() => _down = true),
      onTapCancel: () => setState(() => _down = false),
      onTapUp: (_) => setState(() => _down = false),
      onTap: widget.onTap,
      child: AnimatedScale(
        scale: _down ? 0.93 : 1.0,
        duration: const Duration(milliseconds: 90),
        child: Opacity(
          opacity: widget.dim ? 0.55 : 1,
          child: Container(
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(20),
              gradient: LinearGradient(colors: [c1, c2], begin: Alignment.topLeft, end: Alignment.bottomRight),
              boxShadow: [
                BoxShadow(color: c1.withAlpha((120 * glow).round()), blurRadius: 18 * glow + 2, spreadRadius: 0.5),
                if (widget.active) BoxShadow(color: c2.withAlpha(150), blurRadius: 26),
              ],
            ),
            padding: const EdgeInsets.all(2),
            child: Container(
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(18),
                gradient: LinearGradient(
                  begin: Alignment.topCenter,
                  end: Alignment.bottomCenter,
                  colors: [c1.withAlpha(_down ? 120 : 70), const Color(0xFF080818)],
                ),
              ),
              child: LayoutBuilder(builder: (context, box) {
                final s = box.biggest.shortestSide;
                final icon = (s * 0.42).clamp(20.0, 64.0);
                return Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    ButtonGlyph(icon: b.icon, imagePath: b.imagePath, color: Color.lerp(c1, Colors.white, 0.35)!, size: icon),
                    SizedBox(height: s * 0.06),
                    Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 4),
                      child: Text(
                        b.label,
                        maxLines: 2,
                        textAlign: TextAlign.center,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(color: Neon.text, fontSize: (s * 0.13).clamp(10.0, 15.0), fontWeight: FontWeight.w500, height: 1.1),
                      ),
                    ),
                  ],
                );
              }),
            ),
          ),
        ),
      ),
    );
  }
}

/// HSV color picker dialog (no external dependency).
Future<Color?> pickColor(BuildContext context, Color initial) {
  return showDialog<Color>(
    context: context,
    builder: (ctx) {
      var hsv = HSVColor.fromColor(initial);
      return StatefulBuilder(builder: (ctx, set) {
        final c = hsv.toColor();
        Widget slider(String label, double v, double max, Color active, ValueChanged<double> on) => Row(children: [
              SizedBox(width: 22, child: Text(label)),
              Expanded(child: Slider(value: v, max: max, activeColor: active, onChanged: (x) => set(() => on(x)))),
            ]);
        return AlertDialog(
          title: const Text('Pick a color'),
          content: Column(mainAxisSize: MainAxisSize.min, children: [
            Container(height: 56, decoration: BoxDecoration(color: c, borderRadius: BorderRadius.circular(12))),
            const SizedBox(height: 8),
            slider('H', hsv.hue, 360, c, (x) => hsv = hsv.withHue(x)),
            slider('S', hsv.saturation, 1, c, (x) => hsv = hsv.withSaturation(x)),
            slider('V', hsv.value, 1, c, (x) => hsv = hsv.withValue(x)),
          ]),
          actions: [
            TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
            FilledButton(onPressed: () => Navigator.pop(ctx, c), child: const Text('Use')),
          ],
        );
      });
    },
  );
}

void toast(BuildContext context, String msg, {bool error = false}) {
  final m = ScaffoldMessenger.maybeOf(context);
  if (m == null) return;
  m.hideCurrentSnackBar();
  m.showSnackBar(SnackBar(
    content: Text(msg),
    backgroundColor: error ? const Color(0xFF8E1B2E) : Neon.panel2,
    duration: Duration(milliseconds: error ? 3500 : 1600),
  ));
}
