# Bunkr Album

Android app in C# (**.NET 10 / .NET for Android**). Paste a public Bunkr album or file link, pick the files, and save them. The default folder is **Download/Bunkr**. **Change** opens the system folder picker and the choice is remembered.

Downloads keep running in the background. Pause, resume, and cancel are in the app and in the notification, which also shows speed. An interrupted or short file continues later, including after a reboot. Files that are already saved are skipped. **Retry failed** runs only the ones that failed. Tap a saved row to open it. More albums can join the queue. Filters, search, and sort cover large albums. Previews load next to each file. Parallel downloads are 1–4. Optional Wi-Fi only waits for a network change instead of polling. Open a link from the browser or share it into the app.

The signing key stays the same once the repository secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, and `ANDROID_KEY_PASSWORD` are set. Updates can then install over the existing app. Without those secrets every build uses a new certificate, and an update requires uninstalling first.

Example: `https://bunkr.cr/a/cpdYatJu`

The app reads the public album page (`?advanced=1`), asks `https://dl.bunkr.cr/api/_001_v2` for a CDN path per file id, and signs it via `https://glb-apisign.cdn.cr/sign`. It only saves files the link already exposes publicly.

Only download content you are allowed to use.

## APK

Every push to `main` starts [Build APK](.github/workflows/android-apk.yml). The file is attached as the **BunkrAlbum-apk** artifact (`BunkrAlbum.apk`, arm64). Releases also attach that APK.

1. Wait for the workflow to finish.
2. Download the APK and install it.
3. Allow installs from unknown sources.

## Local

```bash
dotnet test tests/BunkrAlbum.Core.Tests/BunkrAlbum.Core.Tests.csproj
dotnet workload install android
dotnet build src/BunkrAlbum/BunkrAlbum.csproj -c Release -f net10.0-android -t:InstallAndroidDependencies -p:AcceptAndroidSdkLicenses=true
dotnet publish src/BunkrAlbum/BunkrAlbum.csproj -c Release -f net10.0-android
```

JDK 21. Target is `net10.0-android`, arm64, minimum Android 8 (API 26).
