import 'dart:convert';
import 'dart:io';

/// Keep in sync with `version:` in pubspec.yaml.
const kAppVersion = '1.2.2';
const kRepo = 'Devinhansaja/stream-desk';

class AppUpdate {
  final String version;
  final String apkUrl;
  final String pageUrl;
  const AppUpdate(this.version, this.apkUrl, this.pageUrl);
}

class UpdateResult {
  final AppUpdate? update;
  final String? error;
  const UpdateResult({this.update, this.error});
}

List<int> _parts(String v) => v.replaceAll(RegExp(r'^[vV]'), '').split('.').map((p) => int.tryParse(p.replaceAll(RegExp(r'[^0-9]'), '')) ?? 0).toList();

bool isNewer(String candidate, String current) {
  final a = _parts(candidate), b = _parts(current);
  for (var i = 0; i < 3; i++) {
    final x = i < a.length ? a[i] : 0, y = i < b.length ? b[i] : 0;
    if (x != y) return x > y;
  }
  return false;
}

/// Looks at the latest GitHub Release. Returns [UpdateResult.update] == null when already up to date.
Future<UpdateResult> checkAppUpdate() async {
  final c = HttpClient()..connectionTimeout = const Duration(seconds: 8);
  try {
    final req = await c.getUrl(Uri.parse('https://api.github.com/repos/$kRepo/releases/latest'));
    req.headers.set('User-Agent', 'StreamDeck-Mobile');
    req.headers.set('Accept', 'application/vnd.github+json');
    final resp = await req.close().timeout(const Duration(seconds: 12));
    if (resp.statusCode != 200) return UpdateResult(error: 'GitHub answered ${resp.statusCode}');
    final j = jsonDecode(await resp.transform(utf8.decoder).join()) as Map<String, dynamic>;
    final tag = (j['tag_name'] ?? '') as String;
    String? apk;
    for (final a in (j['assets'] ?? []) as List) {
      if (a['name'] == 'StreamDeck-Mobile.apk') apk = a['browser_download_url'] as String?;
    }
    if (apk == null) return const UpdateResult(error: 'The latest release has no APK yet.');
    if (!isNewer(tag, kAppVersion)) return const UpdateResult();
    return UpdateResult(update: AppUpdate(tag.replaceAll(RegExp(r'^[vV]'), ''), apk, (j['html_url'] ?? '') as String));
  } catch (e) {
    return UpdateResult(error: 'Could not check (no internet?): $e');
  } finally {
    c.close(force: true);
  }
}
