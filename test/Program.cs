using System.IO;
using System.Text;
using PhoneBackup;

class Program
{
    static int _failures;

    static void Check(bool cond, string what)
    {
        Console.WriteLine((cond ? "PASS  " : "FAIL  ") + what);
        if (!cond) _failures++;
    }

    static void W(string root, string rel, int size, byte fill = 0)
    {
        string p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var data = new byte[size];
        Array.Fill(data, fill);
        File.WriteAllBytes(p, data);
    }

    [STAThread] // DeleteVault renders the folder icon with WPF
    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "pbk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // --- layout mimicking a backup: <Device>\<Storage>\... ---
            string s = @"OnePlus 15\Internal shared storage";
            W(root, s + @"\DCIM\Camera\IMG_0001.jpg", 3_000_000, 1);          // keep
            W(root, s + @"\DCIM\Camera\IMG_0002.jpg", 3_000_000, 1);          // duplicate of 0001
            W(root, s + @"\Pictures\copy_of_img.jpg", 3_000_000, 1);          // duplicate of 0001
            W(root, s + @"\DCIM\Camera\IMG_0003.jpg", 3_000_000, 2);          // same size, diff content -> not dup
            W(root, s + @"\DCIM\Camera\.trashed-1700000000-IMG_9.jpg", 2_000_000); // hidden/deleted
            W(root, s + @"\Android\data\com.app\cache\blob.bin", 500_000);    // junk folder (path rule)
            W(root, s + @"\Android\media\com.whatsapp\WhatsApp\Media\WhatsApp Images\IMG-W.jpg", 200_000); // small but app media -> keep
            W(root, s + @"\Download\notes.pdf", 50_000);                      // small but document -> keep
            W(root, s + @"\Download\tiny1.dat", 10_000);                      // tiny -> clean
            W(root, s + @"\Download\tiny2.dat", 12_000);                      // tiny -> clean
            W(root, s + @"\Music\song.tmp", 5_000_000);                       // junk extension
            W(root, s + @"\LOST.DIR\frag01", 4_000_000);                      // junk folder (name rule)
            Directory.CreateDirectory(Path.Combine(root, s, @"Movies\emptydir"));
            W(root, s + @"\Samsung\SmartSwitch\backup.bin", 9_000_000);       // samsung folder
            W(root, s + @"\AR Emoji camera\emoji.gif", 2_100_000, 7);         // samsung folder (prefix)
            W(root, s + @"\Documents\note.snb", 2_200_000, 9);                // samsung-only format
            W(root, @"delete\old\leftover.jpg", 3_000_000, 1);                // pre-existing vault: must be ignored
            W(root, s + @"\delete\nested_dupe.jpg", 3_000_000, 1);            // nested vault: must be ignored too
            string s2 = @"Galaxy S23 Ultra\Internal shared storage";
            W(root, s2 + @"\DCIM\Camera\IMG_0001.jpg", 3_000_000, 1);         // cross-device copy (copy-pasted from S23U)

            // ---------- Cleaner ----------
            var defaults = new AppSettings();
            var rules = new Cleaner.Rules
            {
                MinSizeBytes = 1024 * 1024,
                AlwaysKeepDocuments = true,
                SkipDotEntries = true,
                ExcludedFolders = AppSettings.ParseList(defaults.ExcludedFolders),
                ExcludedExtensions = AppSettings.ParseList(defaults.ExcludedExtensions),
                DocumentExtensions = AppSettings.ParseList(defaults.DocumentExtensions),
            };
            var cats = Cleaner.Scan(root, rules, _ => { }, CancellationToken.None);
            var byName = cats.ToDictionary(c => c.Name);

            List<string> CatPaths(string name) =>
                byName.TryGetValue(name, out var c) ? c.Paths : new List<string>();

            Check(CatPaths("Junk folders").Any(p => p.EndsWith(@"Android\data")), "cleaner: Android\\data flagged via path rule");
            Check(CatPaths("Junk folders").Any(p => p.EndsWith(@"LOST.DIR")), "cleaner: LOST.DIR flagged via name rule");
            Check(!CatPaths("Junk folders").Any(p => p.EndsWith(@"Android\media") || p.EndsWith("Android")), "cleaner: Android\\media NOT flagged");
            Check(CatPaths("Hidden / deleted-on-phone files").Any(p => p.Contains(".trashed-")), "cleaner: .trashed file flagged");
            Check(CatPaths("Junk file types").Any(p => p.EndsWith("song.tmp")), "cleaner: .tmp flagged");
            var small = CatPaths("Tiny files");
            Check(small.Any(p => p.EndsWith("tiny1.dat")) && small.Any(p => p.EndsWith("tiny2.dat")), "cleaner: tiny files flagged");
            Check(!small.Any(p => p.EndsWith("notes.pdf")), "cleaner: small PDF kept (document safeguard)");
            Check(!small.Any(p => p.EndsWith("IMG-W.jpg")), "cleaner: small WhatsApp image kept (app media safeguard)");
            Check(!cats.SelectMany(c => c.Paths).Any(p => p.EndsWith("IMG_0001.jpg")), "cleaner: normal photo untouched");
            Check(CatPaths("Empty folders").Any(p => p.EndsWith("Movies")), "cleaner: empty tree flagged at top-most folder");
            Check(!CatPaths("Empty folders").Any(p => p.EndsWith("emptydir")), "cleaner: nested empty child rolled up into parent");
            Check(CatPaths("Samsung-specific folders").Any(p => p.EndsWith("Samsung")), "cleaner: Samsung folder flagged");
            Check(CatPaths("Samsung-specific folders").Any(p => p.EndsWith("AR Emoji camera")), "cleaner: AR Emoji folder flagged");
            Check(CatPaths("Samsung-only file types").Any(p => p.EndsWith("note.snb")), "cleaner: Samsung Notes file flagged");
            Check(!cats.SelectMany(c => c.Paths).Any(p => p.Contains(@"\delete\")), "cleaner: delete vault ignored");

            // ---------- Duplicate finder: everywhere ----------
            var groupsAll = DuplicateFinder.Scan(root, 1024 * 1024, false, (_, _) => { }, CancellationToken.None);
            foreach (var g in groupsAll)
                Console.WriteLine($"  group ({g.Size}B): {string.Join(" | ", g.Files.Select(f => f.Path.Substring(root.Length)))}");
            Check(groupsAll.Count == 1, $"dup(all): exactly one group (got {groupsAll.Count})");
            if (groupsAll.Count == 1)
            {
                Check(groupsAll[0].Files.Count == 4, $"dup(all): 3 OnePlus + 1 S23U, vault copies ignored (got {groupsAll[0].Files.Count})");
                Check(!groupsAll[0].Files.Any(f => f.Path.EndsWith("IMG_0003.jpg")), "dup(all): same-size different-content excluded");
                Check(!groupsAll[0].Files.Any(f => DeleteVault.IsInsideVault(f.Path)), "dup(all): nested + root delete vaults ignored");
            }

            // ---------- Duplicate finder: same device folder only (cross-device protection) ----------
            var groupsSame = DuplicateFinder.Scan(root, 1024 * 1024, true, (_, _) => { }, CancellationToken.None);
            Check(groupsSame.Count == 1, $"dup(same-folder): one group (got {groupsSame.Count})");
            if (groupsSame.Count == 1)
            {
                Check(groupsSame[0].Files.Count == 3, $"dup(same-folder): only the 3 OnePlus copies (got {groupsSame[0].Files.Count})");
                Check(groupsSame[0].Files.All(f => f.Path.Contains("OnePlus 15")), "dup(same-folder): S23U copy NOT matched across devices");
            }

            // ---------- TopLevel + vault detection ----------
            Check(DuplicateFinder.TopLevel(root, Path.Combine(root, @"OnePlus 15\a\b.jpg")) == "OnePlus 15", "toplevel: device folder");
            Check(DuplicateFinder.TopLevel(root, Path.Combine(root, "loose.jpg")) == "", "toplevel: file at root has none");
            Check(DeleteVault.IsInsideVault(@"C:\b\delete\x.jpg"), "vault-detect: root delete");
            Check(DeleteVault.IsInsideVault(@"C:\b\OnePlus 15\delete\x.jpg"), "vault-detect: nested delete");
            Check(!DeleteVault.IsInsideVault(@"C:\b\deleted photos\x.jpg"), "vault-detect: 'deleted photos' is not the vault");
            Check(!DeleteVault.IsInsideVault(@"C:\b\Pictures\x.jpg"), "vault-detect: normal path");

            // ---------- Auto-select strategies ----------
            var rows = new List<DupRow>
            {
                new() { Path = @"C:\x\Pictures\sub\deep\copy.jpg", Modified = new DateTime(2024, 6, 1), Group = 1 },
                new() { Path = @"C:\x\DCIM\Camera\orig.jpg",       Modified = new DateTime(2024, 6, 5), Group = 1 },
                new() { Path = @"C:\x\Download\c.jpg",             Modified = new DateTime(2024, 6, 9), Group = 1 },
            };
            Check(DuplicateFinder.ChooseKeeper(rows, DuplicateFinder.KeepStrategy.Oldest).Path.EndsWith("copy.jpg"), "strategy: oldest");
            Check(DuplicateFinder.ChooseKeeper(rows, DuplicateFinder.KeepStrategy.Newest).Path.EndsWith("c.jpg"), "strategy: newest");
            Check(DuplicateFinder.ChooseKeeper(rows, DuplicateFinder.KeepStrategy.PreferDcim).Path.EndsWith("orig.jpg"), "strategy: prefer DCIM");
            Check(DuplicateFinder.ChooseKeeper(rows, DuplicateFinder.KeepStrategy.ShortestPath).Path.EndsWith("c.jpg"), "strategy: shortest path");
            Check(DuplicateFinder.ChooseKeeper(rows, DuplicateFinder.KeepStrategy.LongestPath).Path.EndsWith("copy.jpg"), "strategy: longest path");
            Check(DuplicateFinder.ChooseKeeper(rows, DuplicateFinder.KeepStrategy.ShortestName).Path.EndsWith("c.jpg"), "strategy: shortest filename");

            // ---------- Delete vault: move with structure + red icon ----------
            var junkDir = Path.Combine(root, s, @"Android\data");
            string tinyFile = Path.Combine(root, s, @"Download\tiny1.dat");
            var movedItems = DeleteVault.MoveToDelete(root, new List<string> { junkDir, tinyFile });
            Check(movedItems.Count == 2, $"vault: both items moved (got {movedItems.Count})");
            Check(!Directory.Exists(junkDir) && !File.Exists(tinyFile), "vault: originals gone");
            Check(Directory.Exists(Path.Combine(root, "delete", s, @"Android\data")), "vault: folder structure preserved");
            Check(File.Exists(Path.Combine(root, "delete", s, @"Download\tiny1.dat")), "vault: file structure preserved");
            Check(File.Exists(Path.Combine(root, "delete", "desktop.ini")) &&
                  File.Exists(Path.Combine(root, "delete", ".folder.ico")), "vault: red folder icon applied");

            // moving again under the same name gets a unique suffix
            W(root, s + @"\Download\tiny1.dat", 8_000);
            var again = DeleteVault.MoveToDelete(root, new List<string> { tinyFile });
            Check(again.Count == 1 && File.Exists(Path.Combine(root, "delete", s, @"Download\tiny1 (2).dat")),
                  "vault: name collision handled");

            // ---------- ShellCopyFallback partial-file guard exists at the engine level;
            // here we verify the legacy settings migration that protects WhatsApp media ----------
            var legacy = new AppSettings { ExcludedFolders = "Android, LOST.DIR, cache" };
            var mig = typeof(AppSettings).GetMethod("MigrateLegacyExclusions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            mig.Invoke(null, new object[] { legacy });
            var migParts = AppSettings.ParseList(legacy.ExcludedFolders);
            Check(!migParts.Contains("Android") && migParts.Contains("Android/data") && migParts.Contains("Android/obb")
                  && migParts.Contains("LOST.DIR"),
                  "settings: legacy whole-Android exclusion migrated to data/obb");

            // ---------- ContentIndex: scan, candidates, hashing, live registration ----------
            {
                string ciRoot = Path.Combine(root, "ci-test");
                W(ciRoot, "a.bin", 500, 1);
                W(ciRoot, @"sub\b.bin", 500, 1);   // same size AND content as a.bin
                W(ciRoot, "c.bin", 500, 2);        // same size, different content

                var ci = ContentIndex.LoadOrScan(ciRoot, _ => { }, CancellationToken.None);
                var candidates = ci.CandidatesForSize(500, exclude: "a.bin").ToList();
                Check(candidates.Count == 2, $"contentindex: 2 candidates for size 500 excluding self (got {candidates.Count})");

                string hashA = ci.GetOrComputeHash("a.bin", CancellationToken.None);
                string hashB = ci.GetOrComputeHash(Path.Combine("sub", "b.bin"), CancellationToken.None);
                string hashC = ci.GetOrComputeHash("c.bin", CancellationToken.None);
                Check(hashA == hashB, "contentindex: identical content hashes equal");
                Check(hashA != hashC, "contentindex: different content hashes differ");

                ci.Register("newly-written.bin", 777, "deadbeef");
                Check(ci.CandidatesForSize(777, null).Contains("newly-written.bin", StringComparer.OrdinalIgnoreCase),
                      "contentindex: Register makes same-run files visible immediately");
            }

            // ---------- BackupEngine.PromoteStagedFile: the actual "no overwrite, no duplicate"
            // safety logic — exercised directly since it touches only local files, no phone/COM ----------
            {
                string proRoot = Path.Combine(root, "promote-test");
                Directory.CreateDirectory(proRoot);

                W(proRoot, @"S23U\photo.jpg", 1000, 5);       // existing file from an old backup
                W(proRoot, @"OnePlus\note.txt", 40, 9);       // foreign, untracked file sitting in the way
                W(proRoot, @"OnePlus\same.bin", 60, 3);       // untracked but content-identical to what's incoming

                var ci = ContentIndex.LoadOrScan(proRoot, _ => { }, CancellationToken.None);
                var opt = new BackupOptions { DestRoot = proRoot };
                var engine = new BackupEngine(opt, new LiveStats(), CancellationToken.None);
                engine.AttachContentIndexForTests(ci);

                // Case 1: content already exists elsewhere (old S23U backup) -> not stored twice.
                string part1 = Path.Combine(proRoot, "OnePlus", "newphoto.jpg.part");
                File.WriteAllBytes(part1, Enumerable.Repeat((byte)5, 1000).ToArray());
                string dest1 = Path.Combine(proRoot, "OnePlus", "newphoto.jpg");
                var r1 = engine.PromoteStagedFile(part1, dest1, @"OnePlus\newphoto.jpg", 1000, weOwnDestPath: false);
                Check(r1.Kind == BackupEngine.PromoteKind.DuplicateElsewhere, $"promote: duplicate elsewhere detected (got {r1.Kind})");
                Check(r1.RefPath == @"S23U\photo.jpg", $"promote: duplicate points at the existing S23U file (got {r1.RefPath})");
                Check(!File.Exists(part1), "promote: staged file removed, not stored twice");
                Check(!File.Exists(dest1), "promote: no new physical file created for the duplicate");

                // Case 2: destination already occupied by a FOREIGN file with different content -> never overwrite.
                string part2 = Path.Combine(proRoot, "OnePlus", "note.txt.part");
                File.WriteAllBytes(part2, Enumerable.Repeat((byte)1, 40).ToArray());
                string dest2 = Path.Combine(proRoot, "OnePlus", "note.txt");
                var r2 = engine.PromoteStagedFile(part2, dest2, @"OnePlus\note.txt", 40, weOwnDestPath: false);
                Check(r2.Kind == BackupEngine.PromoteKind.Conflict, $"promote: conflict detected for foreign file (got {r2.Kind})");
                Check(File.Exists(dest2) && File.ReadAllBytes(dest2).All(b => b == 9), "promote: foreign file left completely untouched");
                Check(r2.RefPath != null && File.Exists(Path.Combine(proRoot, r2.RefPath)), "promote: phone's version saved alongside under a new name");
                Check(r2.RefPath != @"OnePlus\note.txt", "promote: alt name differs from the foreign file's own name");

                // Case 3: destination occupied by an untracked file whose content is ALREADY identical -> adopt, don't duplicate.
                string part3 = Path.Combine(proRoot, "OnePlus", "same.bin.part");
                File.WriteAllBytes(part3, Enumerable.Repeat((byte)3, 60).ToArray());
                string dest3 = Path.Combine(proRoot, "OnePlus", "same.bin");
                var r3 = engine.PromoteStagedFile(part3, dest3, @"OnePlus\same.bin", 60, weOwnDestPath: false);
                Check(r3.Kind == BackupEngine.PromoteKind.AlreadyAtDest, $"promote: recognizes identical untracked file already there (got {r3.Kind})");
                Check(!File.Exists(part3), "promote: staged file cleaned up when content was already present");

                // Case 4: destination is OUR OWN previous file (weOwnDestPath) -> safe to overwrite with the update.
                W(proRoot, @"OnePlus\ours.dat", 20, 7);
                string part4 = Path.Combine(proRoot, "OnePlus", "ours.dat.part");
                File.WriteAllBytes(part4, Enumerable.Repeat((byte)8, 25).ToArray());
                string dest4 = Path.Combine(proRoot, "OnePlus", "ours.dat");
                var r4 = engine.PromoteStagedFile(part4, dest4, @"OnePlus\ours.dat", 25, weOwnDestPath: true);
                Check(r4.Kind == BackupEngine.PromoteKind.Written, $"promote: own file safely updated (got {r4.Kind})");
                Check(File.Exists(dest4) && File.ReadAllBytes(dest4).All(b => b == 8), "promote: own file now holds the new content");
            }

            // ---------- History log: exercise the real Add/Load path, then restore whatever
            // was really there so running the tests never pollutes the user's actual history ----------
            {
                string jsonPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnePlusPhoneBackup", "history.json");
                string textPath = HistoryLog.TextLogPath;
                string? origJson = File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : null;
                string? origText = File.Exists(textPath) ? File.ReadAllText(textPath) : null;
                try
                {
                    int before = HistoryLog.Load().Count;
                    string marker = "TEST-ENTRY-" + Guid.NewGuid();
                    HistoryLog.Add("Backup", marker, "test details");
                    var after = HistoryLog.Load();
                    Check(after.Count == before + 1, "history: Add appends exactly one entry");
                    Check(after.Count > 0 && after[0].Summary == marker, "history: newest entry is listed first");
                    Check(File.Exists(textPath), "history: text log file is created");
                }
                finally
                {
                    if (origJson != null) File.WriteAllText(jsonPath, origJson); else { try { File.Delete(jsonPath); } catch { } }
                    if (origText != null) File.WriteAllText(textPath, origText); else { try { File.Delete(textPath); } catch { } }
                }
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} FAILURES");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
