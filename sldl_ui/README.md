# sldl UI

A graphical user interface for [slsk-batchdl](https://github.com/fiso64/slsk-batchdl) — the smart Soulseek music downloader.

Built with Flutter/Dart, sldl UI runs natively on **Windows**, **Linux**, and **macOS**.

---

## Features

- **Settings GUI** — Configure all sldl options without touching config files
- **Download queue** — View queued, running, succeeded, and failed downloads with live progress
- **Soulseek login management** — Prompts at startup if credentials are missing; re-prompts on connection loss
- **All input types** — Search strings, Spotify/YouTube/Bandcamp/MusicBrainz URLs, CSV files, list files, Soulseek links
- **Name format builder** — Visual tag picker to build download naming templates
- **Spotify & YouTube setup wizard** — Guided first-run setup with step-by-step instructions
- **Cross-platform installers** — Inno Setup for Windows, shell scripts for Linux/macOS

---

## Prerequisites

1. **Flutter SDK** ≥ 3.3 — [Install Flutter](https://docs.flutter.dev/get-started/install)
2. **sldl binary** — Download from the [slsk-batchdl releases page](https://github.com/fiso64/slsk-batchdl/releases)
3. **Platform tools**:
   - Windows: Visual Studio Build Tools (included with Flutter setup)
   - Linux: `ninja-build`, `libgtk-3-dev`, `pkg-config`
   - macOS: Xcode

---

## Quick Start

### 1. Bootstrap (first time only)

After cloning, run the bootstrap script to generate the Flutter platform runner files:

```bash
git clone https://github.com/fiso64/slsk-batchdl
cd slsk-batchdl/sldl_ui

# Generates platform-specific files (windows/, linux/, macos/)
bash setup/bootstrap.sh
```

### 2. Build

```bash
cd sldl_ui
flutter build windows --release   # Windows
flutter build linux   --release   # Linux
flutter build macos   --release   # macOS
```

### 2. Run

```bash
flutter run   # debug mode on your connected desktop
```

### 3. Install

**Windows (recommended)** — download the pre-built installer from the [Releases page](https://github.com/fiso64/slsk-batchdl/releases). It is built automatically by GitHub Actions on every version tag and includes `sldl.exe`.

To build the installer locally (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):
```
; Stage the Flutter build output first:
xcopy /E /I sldl_ui\build\windows\x64\runner\Release sldl_ui\setup\windows\dist
; Then compile:
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" sldl_ui\setup\windows\installer.iss
; Output: sldl_ui\setup\windows\output\sldl-ui-setup-*.exe
```

**Linux**:
```bash
bash setup/linux/install.sh
```

**macOS**:
```bash
bash setup/macos/install.sh
```

## Automated releases (GitHub Actions)

Push a tag like `ui-v1.0.0` to automatically build and publish a release:
```bash
git tag ui-v1.0.0 && git push origin ui-v1.0.0
```
The workflow (`.github/workflows/release-ui.yml`) will:
1. Build the Flutter Windows app
2. Download the latest `sldl.exe` from the slsk-batchdl releases
3. Compile the Inno Setup installer
4. Build a Linux tarball
5. Publish both as a GitHub Release

---

## First-Run Setup Wizard

On first launch, sldl UI guides you through:

1. **sldl executable path** — locate your sldl binary
2. **Soulseek login** — enter username and password
3. **Spotify setup** (optional) — create an app at developer.spotify.com, enter Client ID and Secret
4. **YouTube setup** (optional) — get a YouTube Data API v3 key from Google Cloud Console
5. **Done!**

All settings can be changed later via the Settings button.

---

## Configuration

sldl UI reads and writes the standard `sldl.conf` file used by slsk-batchdl:

- **Windows**: `%APPDATA%\sldl\sldl.conf`
- **Linux**: `~/.config/sldl/sldl.conf`
- **macOS**: `~/.config/sldl/sldl.conf`

Changes made in the Settings screen are saved back to this file when you press **Save**. Clearing a field removes that key from the config file entirely.

---

## Name Format

The main screen includes a **Name Format Builder** with a visual tag picker. Click any tag chip to insert `{tagname}` at the cursor position in the format string.

Available tags:

| Category | Tags |
|---|---|
| File Tags | `artist`, `artists`, `albumartist`, `albumartists`, `title`, `album`, `year`, `track`, `disc`, `length` |
| Source | `sartist`, `stitle`, `salbum`, `slength`, `uri`, `snum`, `row` |
| Soulseek | `slsk-filename`, `slsk-foldername`, `extractor`, `input`, `item-name` |
| Path & Status | `default-folder`, `bindir`, `path`, `path-noext`, `ext`, `type`, `state`, `failure-reason`, `is-audio`, `artist-maybe-wrong` |

**Examples:**
- `{artist( - )title|slsk-filename}` — Artist - Title, falling back to original filename
- `{albumartist(/)album(/)track(. )title|(missing-tags/)slsk-foldername(/)slsk-filename}` — Sort into artist/album folders

---

## Spotify Setup Details

1. Go to [https://developer.spotify.com/dashboard](https://developer.spotify.com/dashboard)
2. Click **Create App** and fill in any name/description
3. Under **Redirect URIs**, add: `http://127.0.0.1:48721/callback`
4. Click **Settings** in your new app and copy the **Client ID** and **Client Secret**
5. Paste them in Settings → Spotify

On first use with a private playlist or liked songs, sldl will open a browser for OAuth. After authorization, copy the printed **token** and **refresh token** into Settings → Spotify for future runs.

---

## YouTube Setup Details

A YouTube Data API v3 key enables reliable retrieval of all playlist videos.

1. Go to [https://console.cloud.google.com](https://console.cloud.google.com)
2. Create or select a project
3. **Enable APIs & Services** → search for **YouTube Data API v3** → Enable
4. **Credentials** → **Create Credentials** → **API Key**
5. Copy the key into Settings → YouTube

---

## Architecture

sldl UI is a pure Flutter wrapper around the sldl CLI tool:

- Spawns `sldl` as a subprocess with appropriate arguments derived from the GUI configuration and job settings
- Parses stdout/stderr line-by-line to update download queue status
- Reads/writes `sldl.conf` in standard INI format
- Stores UI-specific settings (sldl path, setup status) separately using `shared_preferences`

The base slsk-batchdl project is **not modified**. sldl UI works with any sldl binary ≥ 2.x.

---

## Building the Installer (Windows)

Requirements: [Inno Setup 6](https://jrsoftware.org/isinfo.php)

```bat
cd setup\windows
build_and_install.bat
```

The installer bundles:
- The Flutter Windows app (from `build\windows\x64\runner\Release\`)
- Optionally `sldl.exe` (place it next to `installer.iss` before building)
- License file

---

## Development

```bash
flutter pub get
flutter run   # Runs in debug mode on connected desktop
flutter test  # Run tests
```

### Project structure

```
sldl_ui/
├── lib/
│   ├── main.dart              # Entry point
│   ├── app.dart               # App widget & theming
│   ├── models/
│   │   ├── sldl_config.dart   # All sldl config options (maps to sldl.conf)
│   │   └── download_item.dart # Download queue item model
│   ├── services/
│   │   ├── config_service.dart     # Read/write sldl.conf
│   │   ├── process_service.dart    # Launch & parse sldl subprocess
│   │   └── app_config_service.dart # UI settings (shared_preferences)
│   ├── providers/
│   │   └── app_provider.dart  # Main app state (ChangeNotifier)
│   ├── screens/
│   │   ├── main_screen.dart         # Main UI
│   │   ├── settings_screen.dart     # Settings (all config options)
│   │   └── setup_wizard_screen.dart # First-run wizard
│   └── widgets/
│       ├── login_dialog.dart         # Soulseek login dialog
│       ├── download_queue_widget.dart # Download queue list
│       ├── input_panel_widget.dart    # Input type & mode selector
│       └── name_format_builder.dart   # Name format with tag helper
└── setup/
    ├── windows/
    │   ├── installer.iss         # Inno Setup installer script
    │   └── build_and_install.bat # Windows build helper
    ├── linux/
    │   └── install.sh            # Linux installer
    └── macos/
        └── install.sh            # macOS installer
```
