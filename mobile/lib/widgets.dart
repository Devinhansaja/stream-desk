import 'package:flutter/material.dart';

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
    colorScheme: const ColorScheme.dark(primary: Neon.purple, secondary: Neon.blue, surface: Neon.panel),
    appBarTheme: const AppBarTheme(backgroundColor: Neon.bg, elevation: 0),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: Neon.panel2,
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(12), borderSide: BorderSide.none),
    ),
  );
}

void toast(BuildContext context, String msg, {bool error = false}) {
  final m = ScaffoldMessenger.maybeOf(context);
  if (m == null) return;
  m.hideCurrentSnackBar();
  m.showSnackBar(SnackBar(
    content: Text(msg),
    behavior: SnackBarBehavior.floating,
    backgroundColor: error ? const Color(0xFF8E1B2E) : Neon.panel2,
  ));
}
