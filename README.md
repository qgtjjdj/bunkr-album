# Bunkr Album

Android-App in C# (**.NET 10 / .NET for Android**). Ein öffentlicher Bunkr-Album- oder Datei-Link wird gelesen, die Dateien lassen sich auswählen und werden gespeichert. Standardordner ist **Download/Bunkr**, über **Ändern** ein anderer Ordner. Ein laufender Download geht im Hintergrund weiter. Pause, Fortsetzen und Abbrechen stehen in der App und in der Benachrichtigung. Nach einem Neustart des Handys erinnert eine Benachrichtigung daran, den Lauf fortzusetzen; angefangene Dateien werden weitergeladen, schon vorhandene übersprungen. Weitere Alben können in die Warteschlange. Filter: Alle, Bilder, Videos. Optional nur über WLAN. Ein Link lässt sich aus dem Browser teilen oder direkt öffnen.

Die Signatur bleibt gleich, sobald die Repository-Secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` und `ANDROID_KEY_PASSWORD` gesetzt sind. Danach lassen sich Updates über die installierte App legen. Ohne diese Secrets erzeugt jeder Lauf ein neues Zertifikat, und ein Update geht nur nach dem Deinstallieren.

Beispiel: `https://bunkr.cr/a/cpdYatJu`

Die App spricht die öffentliche Albumseite (`?advanced=1`) an, holt zu jeder Datei-ID einen CDN-Pfad über `https://dl.bunkr.cr/api/_001_v2` und signiert ihn über `https://glb-apisign.cdn.cr/sign`. Es werden nur Dateien gespeichert, die der Link bereits öffentlich anbietet.

Nur Inhalte herunterladen, die du verwenden darfst.

## APK

Jeder Push auf `main` startet [Build APK](.github/workflows/android-apk.yml). Die fertige Datei liegt danach als Artifact **BunkrAlbum-apk** (`BunkrAlbum.apk`, arm64).

1. Actions öffnen und den Lauf abwarten.
2. Artifact herunterladen und die APK auf dem Handy installieren.
3. Installation aus unbekannten Quellen erlauben.

Ohne die Signatur-Secrets erzeugt der Lauf ein Wegwerf-Zertifikat. Ein Update geht dann nur nach dem Deinstallieren.

## Lokal

```bash
dotnet test tests/BunkrAlbum.Core.Tests/BunkrAlbum.Core.Tests.csproj
dotnet workload install android
dotnet build src/BunkrAlbum/BunkrAlbum.csproj -c Release -f net10.0-android -t:InstallAndroidDependencies -p:AcceptAndroidSdkLicenses=true
dotnet publish src/BunkrAlbum/BunkrAlbum.csproj -c Release -f net10.0-android
```

JDK 21. Ziel ist `net10.0-android`, arm64, min. Android 8 (API 26).
