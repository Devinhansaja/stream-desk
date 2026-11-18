import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'connection.dart';
import 'scope.dart';
import 'screens/home.dart';
import 'store.dart';
import 'widgets.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  SystemChrome.setPreferredOrientations(DeviceOrientation.values);
  SystemChrome.setSystemUIOverlayStyle(const SystemUiOverlayStyle(statusBarColor: Colors.transparent, systemNavigationBarColor: Neon.bg));

  final app = AppState();
  await app.load();
  final conn = ConnectionManager(app);
  runApp(StreamDeckApp(app: app, conn: conn));
}

class StreamDeckApp extends StatefulWidget {
  const StreamDeckApp({super.key, required this.app, required this.conn});
  final AppState app;
  final ConnectionManager conn;

  @override
  State<StreamDeckApp> createState() => _StreamDeckAppState();
}

class _StreamDeckAppState extends State<StreamDeckApp> with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    widget.conn.start(); // connects and keeps reconnecting automatically
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    widget.conn.stop();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) widget.conn.nudge();
  }

  @override
  Widget build(BuildContext context) {
    return AppScope(
      app: widget.app,
      conn: widget.conn,
      child: MaterialApp(
        title: 'StreamDeck',
        debugShowCheckedModeBanner: false,
        theme: neonTheme(),
        home: const HomeShell(),
      ),
    );
  }
}
