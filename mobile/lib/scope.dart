import 'package:flutter/material.dart';

import 'connection.dart';
import 'store.dart';

/// Gives any widget access to the app state and the PC connection.
class AppScope extends InheritedWidget {
  const AppScope({super.key, required this.app, required this.conn, required super.child});
  final AppState app;
  final ConnectionManager conn;

  static AppScope of(BuildContext context) => context.dependOnInheritedWidgetOfExactType<AppScope>()!;

  @override
  bool updateShouldNotify(AppScope oldWidget) => app != oldWidget.app || conn != oldWidget.conn;
}

/// Rebuilds when app state or connection state changes.
class Live extends StatelessWidget {
  const Live({super.key, required this.builder});
  final Widget Function(BuildContext context, AppState app, ConnectionManager conn) builder;

  @override
  Widget build(BuildContext context) {
    final s = AppScope.of(context);
    return ListenableBuilder(
      listenable: Listenable.merge([s.app, s.conn]),
      builder: (ctx, _) => builder(ctx, s.app, s.conn),
    );
  }
}
