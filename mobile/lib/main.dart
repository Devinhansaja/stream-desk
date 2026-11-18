import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'screens/shell_screen.dart';
import 'store.dart';
import 'widgets.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  SystemChrome.setPreferredOrientations(DeviceOrientation.values);
  SystemChrome.setSystemUIOverlayStyle(const SystemUiOverlayStyle(statusBarColor: Colors.transparent, systemNavigationBarColor: Neon.bg));
  final app = AppState();
  await app.load();
  runApp(StreamDeckApp(app: app));
}

class StreamDeckApp extends StatelessWidget {
  const StreamDeckApp({super.key, required this.app});
  final AppState app;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'StreamDeck',
      debugShowCheckedModeBanner: false,
      theme: neonTheme(),
      home: ShellScreen(app: app),
    );
  }
}
