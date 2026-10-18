# Bunkr Album

Android-App in C# (**.NET 10 / .NET for Android**). Ein öffentlicher Bunkr-Album- oder Datei-Link wird gelesen, die Dateien lassen sich auswählen und landen auf dem Gerät unter **Download/Bunkr**.

Beispiel: `https://bunkr.cr/a/cpdYatJu`

Die App spricht die öffentliche Albumseite (`?advanced=1`) an, holt zu jeder Datei-ID einen CDN-Pfad über `https://dl.bunkr.cr/api/_001_v2` und signiert ihn über `https://glb-apisign.cdn.cr/sign`. Es werden nur Dateien gespeichert, die der Link bereits öffentlich anbietet.

Nur Inhalte herunterladen, die du verwenden darfst.

## APK

Jeder Push auf `main` startet [Build APK](.github/workflows/android-apk.yml). Die fertige Datei liegt danach als Artifact **BunkrAlbum-apk** (`BunkrAlbum.apk`, arm64).

1. Actions öffnen und den Lauf abwarten.
2. Artifact herunterladen und die APK auf dem Handy installieren.
3. Installation aus unbekannten Quellen erlauben.

Ohne hinterlegte Signatur-Secrets erzeugt der Lauf jedes Mal ein neues Zertifikat. Ein Update über eine schon installierte Version geht dann nur nach dem Deinstallieren. Für eine feste Signatur diese Repository-Secrets setzen:

| Secret | Inhalt |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | Base64 der `.keystore` |
| `ANDROID_KEYSTORE_PASSWORD` | Store-Passwort |
| `ANDROID_KEY_ALIAS` | Key-Alias |
| `ANDROID_KEY_PASSWORD` | Key-Passwort |

## Lokal

```bash
dotnet test tests/BunkrAlbum.Core.Tests/BunkrAlbum.Core.Tests.csproj
dotnet workload install android
dotnet build src/BunkrAlbum/BunkrAlbum.csproj -c Release -f net10.0-android -t:InstallAndroidDependencies -p:AcceptAndroidSdkLicenses=true
dotnet publish src/BunkrAlbum/BunkrAlbum.csproj -c Release -f net10.0-android
```

JDK 21. Ziel ist `net10.0-android`, arm64, min. Android 8 (API 26).
