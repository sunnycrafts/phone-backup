# Changelog

All notable changes to OnePlus Phone Backup are documented here.

## 1.1.0 — Content-aware duplicate/overwrite protection, History log

### Added
- **Content-aware duplicate protection.** Before writing any phone file, the backup
  now checks whether identical content already exists anywhere in the destination
  tree — including an old S23 Ultra backup sitting alongside it — and skips storing
  it a second time if so. Matching is by content hash, not filename, so it catches
  files that were copied phone-to-phone under a different name. Toggle: *"Don't
  store a file twice if its content already exists in this backup"* (on by default).
- **Overwrite protection (always on).** The backup engine now refuses to overwrite
  any file at the destination that it doesn't recognize as its own. If an unrecognized
  file is sitting where a phone file would land: identical content is silently
  adopted (nothing duplicated); different content is preserved untouched and the
  phone's version is saved alongside it as `<name> (from phone).<ext>`, logged as a
  conflict. This runs regardless of the duplicate-protection toggle above.
- **History tab and log.** Every backup run, cleanup, and duplicate-removal
  operation is now recorded persistently (separate from the per-run log box, which
  still clears each time). Browse past runs in the new History tab, or open the
  plain-text log directly via "Open log file". Stored in
  `%AppData%\OnePlusPhoneBackup\history.json` / `history.log`.
- New live stats during a backup: duplicates avoided, and conflicts kept separate.

### Changed
- The destination content index is cached (`.phonebackup-content-index.json` in the
  backup folder) so repeat runs only hash new or changed files, not the whole tree
  every time.

## 1.0.0 — Initial release

- Incremental phone-to-PC backup over MTP (File Transfer mode), with a persistent
  index so re-runs only copy new/changed files.
- Junk filtering: Android/data & Android/obb, caches, `.trashed-*` deleted-photo
  leftovers, tiny files below a configurable size threshold, always-keep rules for
  documents and WhatsApp/Telegram media.
- WhatsApp/Telegram photo & video mirroring into the phone's own Pictures folder.
- Backup cleaner tab: junk folders/files, Samsung-specific leftovers (SmartSwitch,
  AR Emoji, Bixby, Samsung Notes, …) for old Galaxy backups being folded into a
  OnePlus backup folder.
- Duplicate finder with content hashing and 9 auto-select strategies (oldest/newest/
  DCIM-preferred/path length/filename length/one-per-device-folder/prefer-a-folder/
  select-inside-a-folder), plus Invert and Clear.
- Deletions (cleaner and duplicate removal) move files into a red-tinted `delete`
  folder with the original structure preserved, instead of destroying them.
- Windows 11–styled UI, self-contained installer and portable build.
