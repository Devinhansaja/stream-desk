import 'package:flutter/material.dart';

import 'models.dart';

/// Registry of selectable icons. Button icons are stored by key so profiles stay portable.
const Map<String, IconData> kIcons = {
  'volume_up': Icons.volume_up,
  'volume_down': Icons.volume_down,
  'volume_off': Icons.volume_off,
  'mic': Icons.mic,
  'mic_off': Icons.mic_off,
  'sun': Icons.wb_sunny,
  'brightness_low': Icons.brightness_low,
  'brightness_high': Icons.brightness_high,
  'camera': Icons.camera_alt,
  'screenshot': Icons.screenshot_monitor,
  'lock': Icons.lock,
  'power': Icons.power_settings_new,
  'restart': Icons.restart_alt,
  'refresh': Icons.refresh,
  'web': Icons.public,
  'chat': Icons.chat,
  'games': Icons.sports_esports,
  'videocam': Icons.videocam,
  'record': Icons.fiber_manual_record,
  'stop': Icons.stop,
  'play': Icons.play_arrow,
  'pause': Icons.pause,
  'play_circle': Icons.play_circle,
  'next': Icons.skip_next,
  'prev': Icons.skip_previous,
  'image': Icons.image,
  'photos': Icons.photo_library,
  'terminal': Icons.terminal,
  'folder': Icons.folder,
  'keyboard': Icons.keyboard,
  'apps': Icons.apps,
  'settings': Icons.settings,
  'bolt': Icons.bolt,
  'home': Icons.home,
  'mail': Icons.mail,
  'music': Icons.music_note,
  'headphones': Icons.headphones,
  'headset': Icons.headset_mic,
  'monitor': Icons.monitor,
  'laptop': Icons.laptop,
  'desktop': Icons.desktop_windows,
  'sleep': Icons.bedtime,
  'logout': Icons.logout,
  'copy': Icons.content_copy,
  'paste': Icons.content_paste,
  'undo': Icons.undo,
  'redo': Icons.redo,
  'search': Icons.search,
  'star': Icons.star,
  'heart': Icons.favorite,
  'rocket': Icons.rocket_launch,
  'code': Icons.code,
  'memory': Icons.memory,
  'speed': Icons.speed,
  'wifi': Icons.wifi,
  'bluetooth': Icons.bluetooth,
  'cloud': Icons.cloud,
  'download': Icons.download,
  'upload': Icons.upload,
  'delete': Icons.delete,
  'mouse': Icons.mouse,
  'tv': Icons.tv,
  'movie': Icons.movie,
  'call': Icons.call,
  'bell': Icons.notifications,
  'timer': Icons.timer,
  'flash': Icons.flash_on,
  'fire': Icons.local_fire_department,
  'close': Icons.close,
  'shield': Icons.shield,
  'palette': Icons.palette,
};

/// Neon palette offered in the editor (plus a free color picker).
const List<int> kPalette = [
  0xFF1E90FF, 0xFF00E5FF, 0xFF7C4DFF, 0xFFD500F9, 0xFFFF1744, 0xFFFF6D00,
  0xFFFFC400, 0xFF76FF03, 0xFF00E676, 0xFF1DE9B6, 0xFFFF4081, 0xFFB0BEC5,
];

enum PK { text, number, choice, app, macro, wallpaper }

class P {
  final String key;
  final String label;
  final PK kind;
  final dynamic def;
  final List<String> options;
  final String? hint;
  const P(this.key, this.label, this.kind, {this.def, this.options = const [], this.hint});
}

class ActionDef {
  final String type;
  final String label;
  final String category;
  final IconData icon;
  final List<P> params;
  final bool dangerous;
  const ActionDef(this.type, this.label, this.category, this.icon, {this.params = const [], this.dangerous = false});
}

/// Every action the Windows host understands. The editor and the Macro creator are generated from this
/// table, so adding an action never needs new UI.
const List<ActionDef> kActions = [
  ActionDef('volume_up', 'Volume up', 'Audio', Icons.volume_up, params: [P('step', 'Step %', PK.number, def: 5)]),
  ActionDef('volume_down', 'Volume down', 'Audio', Icons.volume_down, params: [P('step', 'Step %', PK.number, def: 5)]),
  ActionDef('volume_set', 'Set volume', 'Audio', Icons.volume_up, params: [P('value', 'Volume %', PK.number, def: 50)]),
  ActionDef('mute_toggle', 'Mute / unmute speakers', 'Audio', Icons.volume_off),
  ActionDef('mic_toggle', 'Mute / unmute microphone', 'Audio', Icons.mic_off),
  ActionDef('media', 'Media key', 'Audio', Icons.play_arrow,
      params: [P('key', 'Key', PK.choice, def: 'playpause', options: ['playpause', 'next', 'prev', 'stop'])]),
  ActionDef('brightness_up', 'Brightness up', 'Display', Icons.brightness_high, params: [P('step', 'Step %', PK.number, def: 10)]),
  ActionDef('brightness_down', 'Brightness down', 'Display', Icons.brightness_low, params: [P('step', 'Step %', PK.number, def: 10)]),
  ActionDef('brightness_set', 'Set brightness', 'Display', Icons.wb_sunny, params: [P('value', 'Brightness %', PK.number, def: 70)]),
  ActionDef('screenshot', 'Screenshot (saved on PC)', 'Display', Icons.screenshot_monitor),
  ActionDef('wallpaper', 'Change wallpaper', 'Display', Icons.image, params: [
    P('mode', 'Mode', PK.choice, def: 'next', options: ['next', 'random', 'specific']),
    P('name', 'Wallpaper (for "specific")', PK.wallpaper),
    P('folder', 'Folder on PC (optional)', PK.text, hint: r'C:\Users\me\Pictures\Walls'),
  ]),
  ActionDef('hotkey', 'Keyboard shortcut', 'Input', Icons.keyboard,
      params: [P('keys', 'Keys', PK.text, hint: 'ctrl+shift+esc, win+d, alt+f4')]),
  ActionDef('text', 'Type text', 'Input', Icons.keyboard_alt, params: [P('text', 'Text', PK.text)]),
  ActionDef('app', 'Open installed app / game', 'Launch', Icons.apps, params: [P('appId', 'App', PK.app)]),
  ActionDef('launch', 'Open file or program path', 'Launch', Icons.folder_open,
      params: [P('target', 'Path', PK.text, hint: r'C:\Program Files\App\app.exe'), P('args', 'Arguments', PK.text)]),
  ActionDef('url', 'Open website', 'Launch', Icons.public, params: [P('url', 'URL', PK.text, hint: 'https://')]),
  ActionDef('close_app', 'Close app (process name)', 'Launch', Icons.close, params: [P('name', 'Process', PK.text, hint: 'chrome')]),
  ActionDef('shell', 'Run command (must be enabled on PC)', 'Launch', Icons.terminal, dangerous: true, params: [P('command', 'Command', PK.text)]),
  ActionDef('lock', 'Lock PC', 'Power', Icons.lock),
  ActionDef('sleep', 'Sleep', 'Power', Icons.bedtime, dangerous: true),
  ActionDef('logout', 'Sign out', 'Power', Icons.logout, dangerous: true),
  ActionDef('restart', 'Restart', 'Power', Icons.restart_alt, dangerous: true),
  ActionDef('shutdown', 'Shut down', 'Power', Icons.power_settings_new, dangerous: true),
  ActionDef('macro', 'Run macro', 'Flow', Icons.bolt, params: [P('macroId', 'Macro', PK.macro)]),
  ActionDef('delay', 'Wait (macro step)', 'Flow', Icons.timer, params: [P('ms', 'Milliseconds', PK.number, def: 500)]),
];

ActionDef? actionDef(String type) {
  for (final a in kActions) {
    if (a.type == type) return a;
  }
  return null;
}

Map<String, dynamic> defaultParams(ActionDef d) => {
      for (final p in d.params)
        if (p.def != null) p.key: p.def,
    };

/// One-tap presets shown on the System Controls screen. Users add them to a page as editable buttons.
class Preset {
  final String label;
  final String icon;
  final int c1, c2;
  final ActionSpec action;
  final bool confirm;
  Preset(this.label, this.icon, this.c1, this.c2, this.action, {this.confirm = false});
}

List<Preset> systemPresets() => [
      Preset('Volume Up', 'volume_up', 0xFF1E90FF, 0xFF00E5FF, ActionSpec('volume_up', {'step': 5})),
      Preset('Volume Down', 'volume_down', 0xFF1E90FF, 0xFF00E5FF, ActionSpec('volume_down', {'step': 5})),
      Preset('Mute', 'volume_off', 0xFFFF1744, 0xFFB71C1C, ActionSpec('mute_toggle')),
      Preset('Mic Mute', 'mic_off', 0xFF7C4DFF, 0xFFD500F9, ActionSpec('mic_toggle')),
      Preset('Brightness +', 'sun', 0xFFFFC400, 0xFFFF6D00, ActionSpec('brightness_up', {'step': 10})),
      Preset('Brightness -', 'brightness_low', 0xFFFFC400, 0xFF8D6E00, ActionSpec('brightness_down', {'step': 10})),
      Preset('Screenshot', 'screenshot', 0xFF7C4DFF, 0xFF1E90FF, ActionSpec('screenshot')),
      Preset('Lock PC', 'lock', 0xFF1E90FF, 0xFF0D47A1, ActionSpec('lock')),
      Preset('Sleep', 'sleep', 0xFF7C4DFF, 0xFF311B92, ActionSpec('sleep'), confirm: true),
      Preset('Shutdown', 'power', 0xFF00E676, 0xFF1B5E20, ActionSpec('shutdown'), confirm: true),
      Preset('Restart', 'restart', 0xFF00E5FF, 0xFF0277BD, ActionSpec('restart'), confirm: true),
      Preset('Sign out', 'logout', 0xFFFF6D00, 0xFFBF360C, ActionSpec('logout'), confirm: true),
      Preset('Play / Pause', 'play', 0xFF1DE9B6, 0xFF00897B, ActionSpec('media', {'key': 'playpause'})),
      Preset('Next', 'next', 0xFF1DE9B6, 0xFF00897B, ActionSpec('media', {'key': 'next'})),
      Preset('Previous', 'prev', 0xFF1DE9B6, 0xFF00897B, ActionSpec('media', {'key': 'prev'})),
      Preset('Show Desktop', 'desktop', 0xFF1E90FF, 0xFF7C4DFF, ActionSpec('hotkey', {'keys': 'win+d'})),
      Preset('Task Manager', 'speed', 0xFFB0BEC5, 0xFF455A64, ActionSpec('hotkey', {'keys': 'ctrl+shift+esc'})),
      Preset('Next Wallpaper', 'image', 0xFF1E90FF, 0xFF00E676, ActionSpec('wallpaper', {'mode': 'next'})),
    ];
