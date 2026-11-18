# Prepares the Flutter project (generates the android/ folder, patches the manifest) and builds the APK.
# Requires: Flutter SDK (3.27+), Android SDK, JDK 17 - run `flutter doctor` first.
param([switch]$SetupOnly)
$ErrorActionPreference = 'Stop'
$mobile = Join-Path $PSScriptRoot 'mobile'
Push-Location $mobile
try {
    if (-not (Test-Path 'android')) {
        flutter create --platforms=android --org com.streamdeck --project-name streamdeck_mobile .
        if ($LASTEXITCODE -ne 0) { throw "flutter create failed" }
    }
    Remove-Item 'test\widget_test.dart' -ErrorAction SilentlyContinue   # template test references the default app

    $manifest = 'android\app\src\main\AndroidManifest.xml'
    $xml = Get-Content $manifest -Raw
    if ($xml -notmatch 'android.permission.CAMERA') {
        $perms = @'
    <uses-permission android:name="android.permission.INTERNET"/>
    <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE"/>
    <uses-permission android:name="android.permission.ACCESS_WIFI_STATE"/>
    <uses-permission android:name="android.permission.CAMERA"/>
    <uses-feature android:name="android.hardware.camera" android:required="false"/>
'@
        $xml = $xml -replace '(<application)', "$perms`r`n    `$1"
        # The PC speaks plain ws:// on the local network / USB tunnel.
        $xml = $xml -replace '<application', '<application android:usesCleartextTraffic="true"'
        $xml = $xml -replace 'android:label="[^"]*"', 'android:label="StreamDeck"'
        Set-Content $manifest $xml -Encoding utf8
        Write-Host "Patched AndroidManifest.xml"
    }
    # Newer plugin versions need a newer toolchain than the generated template pins.
    $sg = 'android\settings.gradle'
    if (Test-Path $sg) {
        $c = (Get-Content $sg -Raw) -replace '("com.android.application" version ")[^"]+', '${1}8.7.3' -replace '("org.jetbrains.kotlin.android" version ")[^"]+', '${1}2.1.0'
        [IO.File]::WriteAllText((Resolve-Path $sg), $c.TrimStart([char]0xFEFF), (New-Object Text.UTF8Encoding $false))  # no BOM: Gradle rejects it
    }
    $gw = 'android\gradle\wrapper\gradle-wrapper.properties'
    if (Test-Path $gw) {
        $c = (Get-Content $gw -Raw) -replace 'gradle-[0-9.]+-(all|bin)\.zip', 'gradle-8.9-all.zip'
        [IO.File]::WriteAllText((Resolve-Path $gw), $c.TrimStart([char]0xFEFF), (New-Object Text.UTF8Encoding $false))
    }
    # CameraX (QR scanner) needs minSdk 23.
    $bg = 'android\app\build.gradle'
    if (Test-Path $bg) {
        $c = (Get-Content $bg -Raw) -replace 'minSdk\s*=\s*flutter\.minSdkVersion', 'minSdk = 23'
        [IO.File]::WriteAllText((Resolve-Path $bg), $c.TrimStart([char]0xFEFF), (New-Object Text.UTF8Encoding $false))
    }
    if ($SetupOnly) { return }

    flutter pub get
    if ($LASTEXITCODE -ne 0) { throw "flutter pub get failed" }
    flutter build apk --release
    if ($LASTEXITCODE -ne 0) { throw "flutter build apk failed" }
    New-Item -ItemType Directory -Force "$PSScriptRoot\dist" | Out-Null
    Copy-Item 'build\app\outputs\flutter-apk\app-release.apk' "$PSScriptRoot\dist\StreamDeck-Mobile.apk" -Force
    Write-Host "APK: dist\StreamDeck-Mobile.apk"
} finally { Pop-Location }
