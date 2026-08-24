# OnePlus Phone Backup

I switched phones a few times and kept ending up with old Samsung Smart
Switch backups, half a dozen duplicate photo folders, and no easy way to
just copy everything off the new phone without also copying app cache junk.
This is a Windows app that backs up a OnePlus (or really any MTP Android
phone) to your PC incrementally, skips the junk, and can also clean up and
dedupe the mess left over from older backups.

## What it does

- **Incremental backup over MTP** (phone in File Transfer mode) — keeps a
  persistent index so re-runs only copy new or changed files, not the whole
  phone again.
- **Junk filtering** — skips `Android/data` and `Android/obb`, caches,
  `.trashed-*` leftovers from deleted photos, and files under a configurable
  size threshold, while always keeping documents and WhatsApp/Telegram media.
- **Content-aware duplicate protection** — before writing a file, checks
  whether identical content already exists anywhere in the destination
  (including an old backup from a previous phone sitting right next to it)
  by content hash, not filename, so it won't store the same photo twice just
  because it got renamed.
- **Overwrite protection** — never overwrites a file it doesn't recognize as
  its own. If something unrelated is sitting where a phone file would land,
  the phone's version is saved alongside it as `<name> (from phone).<ext>`
  and logged as a conflict instead of silently clobbering anything.
- **WhatsApp/Telegram media mirroring** into the phone's own Pictures folder.
- **Cleaner tab** for junk folders and old Samsung-specific leftovers
  (Smart Switch, AR Emoji, Bixby, Samsung Notes) when folding an old Galaxy
  backup into a new one.
- **Duplicate finder** with content hashing and nine auto-select strategies
  (oldest/newest/DCIM-preferred/path length/filename length/one-per-device-
  folder/prefer-a-folder/select-inside-a-folder), plus invert and clear.
- **Nothing is destroyed** — cleanup and dedupe both move files into a
  red-tinted `delete` folder with the original structure preserved, rather
  than deleting them outright.
- **History log** of every backup, cleanup, and dedupe run, browsable in its
  own tab or as a plain-text log file.
- Windows 11–styled UI, with both a portable build and an installer.

## How it works

Backups happen over MTP (the same "File Transfer" mode Windows Explorer
uses to browse a phone), through Shell/WPD interop for the actual file
copy. No phone-side app or root access needed — plug in, pick File
Transfer, and point the app at the phone.

Settings (destination folder, size thresholds, excluded folders/extensions,
duplicate strategy) are stored in
`%AppData%\OnePlusPhoneBackup\settings.json`. A destination content index is
cached as `.phonebackup-content-index.json` in the backup folder itself, so
repeat runs only hash files that are new or changed.

## Build

```powershell
dotnet publish PhoneBackup.csproj -c Release -r win-x64 --self-contained
```

An Inno Setup script is included at `installer/PhoneBackup.iss` for building
the installer alongside the portable exe.

## Files

- `MainWindow.xaml` / `MainWindow.xaml.cs` — the UI: backup, cleaner,
  duplicate finder, and history tabs
- `BackupEngine.cs` — the incremental backup + MTP interop
- `Cleaner.cs` — junk and Samsung-leftover cleanup
- `DuplicateFinder.cs` — content-hash duplicate detection and selection
  strategies
- `ContentIndex.cs` — the cached destination content index
- `DeleteVault.cs` — moves removed files into `delete/` instead of deleting
- `HistoryLog.cs` — persistent run history
- `Settings.cs` — app settings, load/save, legacy-setting migration
- `Hashing.cs` — content hashing helpers
