// Duplicate File Finder: finds duplicate files and lets you choose which copies to
// delete (to the Recycle Bin).
//
// Build with build.cmd, which uses the C# compiler included with .NET Framework
// 4.x (part of Windows), so the code is kept to C# 5.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// Without this, .NET treats the program as a 4.0 app and turns off long path support
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("Duplicate File Finder")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace DuplicateFileFinder
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            // Allow paths longer than 260 characters
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    // ------------------------------------------------------------------ model

    public class FileEntry
    {
        public string FullPath;
        public long Size;
        public DateTime Modified;      // UTC
        public string Hash;
        public bool Checked;
        public bool Deleted;
        public bool Stale;             // changed or missing since the results were saved
        public string StaleReason;
        public DupGroup Group;
        public TreeNode Node;

        // Files inside archives: FullPath is <archive>\<path inside>. They are compared
        // and shown, but can't be deleted and never count as the copy that is kept.
        public string Archive;
        public DateTime ArchiveModified;   // UTC, to notice a changed archive after loading
        public string PartialHash;         // of the first 64 KB, computed while extracted

        public string Name { get { return Path.GetFileName(FullPath); } }
        public bool InArchive { get { return Archive != null; } }
    }

    public class DupGroup
    {
        public long Size;
        public string Hash;
        public List<FileEntry> Files = new List<FileEntry>();
        public TreeNode Node;

        // Set only from the menu ("Delete this copy too"): the user chose to delete
        // even the copy that would be kept. Cleared as soon as any copy is unchecked.
        public bool DeleteAll;

        // Files that can still take part: not deleted, not changed since saved
        public List<FileEntry> Valid
        {
            get { return Files.Where(f => !f.Stale && !f.Deleted).ToList(); }
        }

        // Usable copies that are ordinary files (not inside an archive): only these can
        // be deleted, and at least one of them always stays
        public List<FileEntry> Loose
        {
            get { return Files.Where(f => !f.Stale && !f.Deleted && !f.InArchive).ToList(); }
        }

        public long Wasted
        {
            get
            {
                // Every copy but one can be deleted (the one kept may be inside an archive)
                int n = Files.Count(f => !f.Stale && !f.Deleted);
                return n > 1 ? Size * (n - 1) : 0;
            }
        }
    }

    public class ScanOptions
    {
        public long MinSize = 1;
        public bool IncludeHidden;
        public bool SameName;
        public bool IncludeArchives;
        public string TempDir = Path.GetTempPath();
    }

    public class ScanProgress
    {
        public string Phase;
        public int Done;
        public int Total;
        public string Current;
    }

    // ---------------------------------------------------------------- archives

    // Extracts archives with the tools available: WinRAR's Rar.exe (.rar), 7-Zip,
    // WinRAR, then Windows' tar.exe; if one fails the next is tried.
    public static class ArchiveReader
    {
        // .iso and .wim are left out: usually multi-gigabyte disc or system images
        public static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".zipx", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz",
            ".lz", ".lzma", ".tlz", ".lz4", ".zst", ".tzst", ".z", ".taz", ".cab", ".arj", ".lzh", ".lha",
            ".ace", ".cpio", ".sit", ".sitx"
        };

        public const int StallSeconds = 120;

        internal static readonly string winRarExe, rarExe, sevenZipExe, tarExe;

        static ArchiveReader()
        {
            var winRarCandidates = new List<string>();
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinRAR"))
                    if (key != null)
                    {
                        winRarCandidates.Add(key.GetValue("exe64") as string);
                        winRarCandidates.Add(key.GetValue("exe32") as string);
                    }
            }
            catch (Exception) { }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            winRarCandidates.Add(Path.Combine(pf, @"WinRAR\WinRAR.exe"));
            winRarCandidates.Add(Path.Combine(pf86, @"WinRAR\WinRAR.exe"));
            winRarExe = winRarCandidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
            if (winRarExe != null)
            {
                string rar = Path.Combine(Path.GetDirectoryName(winRarExe), "Rar.exe");
                if (File.Exists(rar)) rarExe = rar;
            }
            sevenZipExe = new[] { Path.Combine(pf, @"7-Zip\7z.exe"), Path.Combine(pf86, @"7-Zip\7z.exe") }.FirstOrDefault(File.Exists);
            string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            if (File.Exists(tar)) tarExe = tar;
        }

        public static string AvailableTools
        {
            get
            {
                var tools = new List<string>();
                if (rarExe != null) tools.Add("Rar.exe");
                if (sevenZipExe != null) tools.Add("7-Zip");
                if (winRarExe != null) tools.Add("WinRAR");
                if (tarExe != null) tools.Add("Windows tar");
                return string.Join(", ", tools);
            }
        }

        // Extracts archive into the empty folder dest. Returns null on success, or why
        // it could not be read (every tool's reason).
        public static string Extract(string archive, string dest, Func<bool> cancelled)
        {
            string ext = Path.GetExtension(archive);
            var reasons = new List<string>();
            var attempts = new List<KeyValuePair<string, string>>();   // tool name, "exe|arguments"
            string q = "\"" + archive + "\"";
            if (rarExe != null && ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
                // -p- fails on encrypted archives instead of asking; -op avoids the trailing-backslash quoting trap
                attempts.Add(new KeyValuePair<string, string>("Rar.exe", rarExe + "|x -idq -y -p- \"-op" + dest + "\" -- " + q));
            if (sevenZipExe != null)
                attempts.Add(new KeyValuePair<string, string>("7-Zip", sevenZipExe + "|x " + q + " \"-o" + dest + "\" -y -p_ -bso0 -bsp0"));
            if (winRarExe != null)
                // -ibck: in the background, -inul: no message boxes
                attempts.Add(new KeyValuePair<string, string>("WinRAR", winRarExe + "|x -ibck -inul -y -p- " + q + " \"" + dest + "\\\""));
            if (tarExe != null)
                // A dummy passphrase makes encrypted archives fail instead of asking for one
                attempts.Add(new KeyValuePair<string, string>("tar", tarExe + "|--passphrase _ -xf " + q + " -C \"" + dest + "\""));
            if (attempts.Count == 0) return "no tool to extract archives (install WinRAR or 7-Zip)";

            foreach (var attempt in attempts)
            {
                // Start each attempt from an empty folder
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.CreateDirectory(dest);
                var parts = attempt.Value.Split(new[] { '|' }, 2);
                string output;
                int code = Run(parts[0], parts[1], cancelled, out output);
                if (code == -2) return "cancelled";
                if (code == 0)
                {
                    if (attempt.Key == "7-Zip") UnwrapTar(archive, dest, cancelled);
                    return null;
                }
                reasons.Add(attempt.Key + ": " + Describe(code, output));
            }
            return string.Join(" | ", reasons);
        }

        // Extracts just one file (inner = its path inside the archive) into dest, for
        // previewing. Returns the extracted file's path, or null and the reason.
        // Zips are read directly; other archives use the tools with that one name, and
        // as a last resort (e.g. .tar.gz with 7-Zip) the whole archive is extracted.
        public static string ExtractOne(string archive, string inner, string dest, out string error)
        {
            error = null;
            string target = Path.Combine(dest, inner);
            Directory.CreateDirectory(dest);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
                    {
                        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('/', '\\').TrimStart('\\')
                                                                     .Equals(inner, StringComparison.OrdinalIgnoreCase));
                        if (entry != null)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, target, true);
                            return target;
                        }
                    }
                }
                catch (Exception) { }   // e.g. an encrypted or unusual zip: the tools below may manage
            }

            string q = "\"" + archive + "\"", name = "\"" + inner + "\"";
            var attempts = new List<string[]>();
            if (rarExe != null && archive.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                attempts.Add(new[] { rarExe, "x -idq -y -p- \"-op" + dest + "\" -- " + q + " " + name });
            if (sevenZipExe != null)
                attempts.Add(new[] { sevenZipExe, "x " + q + " \"-o" + dest + "\" -y -p_ -bso0 -bsp0 -- " + name });
            if (winRarExe != null)
                attempts.Add(new[] { winRarExe, "x -ibck -inul -y -p- " + q + " " + name + " \"" + dest + "\\\"" });
            if (tarExe != null)
                attempts.Add(new[] { tarExe, "--passphrase _ -xf " + q + " -C \"" + dest + "\" \"" + inner.Replace('\\', '/') + "\"" });
            foreach (var attempt in attempts)
            {
                string output;
                Run(attempt[0], attempt[1], null, out output);
                if (File.Exists(target)) return target;
            }

            string whole = Extract(archive, dest, null);
            if (File.Exists(target)) return target;
            error = whole ?? "the file was not found in the archive";
            return null;
        }

        // For .tar.gz/.tgz etc. 7-Zip only removes the outer compression, leaving a .tar
        static void UnwrapTar(string archive, string dest, Func<bool> cancelled)
        {
            var inner = Directory.GetFiles(dest, "*", SearchOption.AllDirectories);
            if (inner.Length != 1 || !inner[0].EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
                archive.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return;
            string output;
            if (Run(sevenZipExe, "x \"" + inner[0] + "\" \"-o" + dest + "\" -y -bso0 -bsp0", cancelled, out output) == 0)
                File.Delete(inner[0]);
        }

        internal static string Describe(int code, string output)
        {
            if (code == -1) return "stopped after " + StallSeconds + " s without activity (probably waiting for a password or an answer)";
            var lines = (output ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var password = lines.FirstOrDefault(l => l.IndexOf("passphrase", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     l.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     l.IndexOf("encrypt", StringComparison.OrdinalIgnoreCase) >= 0);
            if (password != null || code == 11) return "password-protected";
            var detail = lines.FirstOrDefault(l => l.IndexOf("Error exit delayed", StringComparison.OrdinalIgnoreCase) < 0);
            return detail ?? ("failed (exit code " + code + ")");
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll")]
        static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

        // Runs a tool with no console input (so a question fails at once instead of
        // waiting for an answer nobody sees) and stops it when it does no reading or
        // writing for StallSeconds. Returns the exit code, -1 if stalled, -2 if cancelled.
        internal static int Run(string exe, string arguments, Func<bool> cancelled, out string output)
        {
            output = "";
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                ulong lastIo = ulong.MaxValue;
                DateTime lastChange = DateTime.Now;
                while (!p.WaitForExit(500))
                {
                    bool stop = cancelled != null && cancelled();
                    IO_COUNTERS io;
                    if (!stop && GetProcessIoCounters(p.Handle, out io))
                    {
                        ulong total = io.ReadTransferCount + io.WriteTransferCount;
                        if (total != lastIo) { lastIo = total; lastChange = DateTime.Now; }
                        else if ((DateTime.Now - lastChange).TotalSeconds >= StallSeconds) stop = true;
                    }
                    if (stop)
                    {
                        try { p.Kill(); } catch (Exception) { }
                        p.WaitForExit(10000);
                        return cancelled != null && cancelled() ? -2 : -1;
                    }
                }
                p.WaitForExit();
                output = stdout.Result + "\n" + stderr.Result;
                return p.ExitCode;
            }
        }
    }

    // Removes files from archives. The archive is extracted, the files to remove are
    // compared with the scan, a new archive is built without them and checked by
    // extracting it again; only then is the original replaced. An archive left
    // without files is deleted instead.
    public static class ArchiveWriter
    {
        // Single-file extensions that, by name, always hold a tar
        static readonly string[] TarOnly = { ".tar", ".tgz", ".tbz", ".tbz2", ".txz", ".tzst", ".tlz", ".taz" };

        // tar.exe switch for each compression, by the archive's last extension
        static readonly Dictionary<string, string> TarCompression = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".tar", "" }, { ".gz", "-z" }, { ".tgz", "-z" }, { ".bz2", "-j" }, { ".tbz", "-j" }, { ".tbz2", "-j" },
            { ".xz", "-J" }, { ".txz", "-J" }, { ".zst", "--zstd" }, { ".tzst", "--zstd" }, { ".lzma", "--lzma" },
            { ".lz", "--lzip" }, { ".tlz", "--lzip" }, { ".z", "-Z" }, { ".taz", "-Z" }
        };

        static bool IsTarBased(string archive)
        {
            string ext = Path.GetExtension(archive);
            return TarOnly.Contains(ext, StringComparer.OrdinalIgnoreCase) ||
                   (TarCompression.ContainsKey(ext) &&
                    Path.GetExtension(Path.GetFileNameWithoutExtension(archive)).Equals(".tar", StringComparison.OrdinalIgnoreCase));
        }

        // Whether files can be removed from this kind of archive (it can always be
        // deleted whole once every file in it is removed)
        public static bool CanModify(string archive)
        {
            string ext = Path.GetExtension(archive).ToLowerInvariant();
            if (ext == ".zip") return true;
            if (ext == ".rar") return ArchiveReader.rarExe != null;
            if (ext == ".cab") return File.Exists(MakecabExe);
            if (ext == ".7z" || IsTarBased(archive)) return ArchiveReader.tarExe != null;
            return false;
        }

        static string MakecabExe
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "makecab.exe"); }
        }

        // remove: paths inside the archive (with \) and the hash each must have.
        // deleteFile sends a file to the Recycle Bin or deletes it, returning null or
        // the reason it failed. progress (may be null) gets what is being done and how
        // far along this archive is, from 0 to 1. Returns null on success, or why
        // nothing was changed.
        public static string RemoveEntries(string archive, Dictionary<string, string> remove, string tempDir,
                                           Func<string, string> deleteFile, out bool archiveDeleted,
                                           Action<string, double> progress = null)
        {
            archiveDeleted = false;
            Action<string, double> report = (stage, fraction) => { if (progress != null) progress(stage, fraction); };
            string work = Path.Combine(tempDir, "DF-edit-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string extracted = Path.Combine(work, "x");
            // The full name, so "t.tar.gz" is read back as a .tar.gz, not as a plain .gz
            string rebuilt = Path.Combine(work, "new-" + Path.GetFileName(archive));
            try
            {
                // Rough share of the work per stage: extract 0-25%, check 25-50%,
                // remove 50-80%, check the new archive 80-95%, replace 95-100%
                Directory.CreateDirectory(work);
                report("extracting the archive", 0);
                string error = ArchiveReader.Extract(archive, extracted, null);
                if (error != null) return "could not extract the archive: " + error;

                var all = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories)
                                   .Select(p => p.Substring(extracted.Length + 1)).ToList();
                int checkedCount = 0;
                foreach (var kv in remove)
                {
                    report(string.Format("checking file {0:N0} of {1:N0} against the scan", checkedCount + 1, remove.Count),
                           0.25 + 0.25 * checkedCount / remove.Count);
                    checkedCount++;
                    string path = Path.Combine(extracted, kv.Key);
                    if (!File.Exists(path)) return "'" + kv.Key + "' is no longer in the archive";
                    if (Scanner.HashFile(path, -1, null) != kv.Value) return "'" + kv.Key + "' changed inside the archive";
                }

                int remaining = all.Count(p => !remove.ContainsKey(p));
                if (remaining == 0)
                {
                    // Nothing would be left: delete the whole archive. Success is judged by
                    // the file being gone, whatever deleteFile reports.
                    report("deleting the archive (no files would be left in it)", 0.95);
                    string reason = deleteFile(archive);
                    if (File.Exists(archive)) return "the archive is now empty but could not be deleted: " + reason;
                    archiveDeleted = true;
                    return null;
                }

                if (!CanModify(archive))
                    return "files can't be removed from " + Path.GetExtension(archive) + " archives (it can only be deleted whole, once all its files are checked)";

                string buildError = Build(archive, extracted, rebuilt, remove.Keys.ToList(),
                                          (stage, fraction) => report(stage, 0.5 + 0.3 * fraction));
                if (buildError != null) return buildError;

                // Check the new archive by extracting it: it must hold exactly the remaining files
                report("checking the new archive", 0.8);
                string check = Path.Combine(work, "check");
                error = ArchiveReader.Extract(rebuilt, check, null);
                if (error != null) return "the rebuilt archive could not be read back: " + error;
                int count = Directory.GetFiles(check, "*", SearchOption.AllDirectories).Length;
                if (count != remaining) return string.Format("the rebuilt archive holds {0} files instead of {1}", count, remaining);

                // Replace the original so that one of the two versions always exists on
                // disk: the new archive is first put next to it, then the original is
                // removed (success judged by the file being gone), then the new one renamed
                report("replacing the original archive", 0.95);
                string beside = archive + ".new";
                if (File.Exists(beside)) return "a file named " + Path.GetFileName(beside) + " is in the way";
                File.Move(rebuilt, beside);
                string removeOld = deleteFile(archive);
                if (File.Exists(archive))
                {
                    try { File.Delete(beside); } catch (Exception) { }
                    return "the original archive could not be replaced: " + removeOld;
                }
                try
                {
                    File.Move(beside, archive);
                }
                catch (Exception ex)
                {
                    return "the original archive was removed but the new one could not be renamed (" + ex.Message + "); the new archive is " + beside;
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
        }

        // Builds rebuilt = archive without the files in remove (paths inside).
        // progress gets the stage and how far along this step is (0 to 1).
        static string Build(string archive, string extracted, string rebuilt, List<string> remove, Action<string, double> progress)
        {
            string ext = Path.GetExtension(archive).ToLowerInvariant();
            string output;
            if (ext == ".zip")
            {
                progress("copying the zip", 0);
                File.Copy(archive, rebuilt);
                using (var zip = System.IO.Compression.ZipFile.Open(rebuilt, System.IO.Compression.ZipArchiveMode.Update))
                {
                    for (int i = 0; i < remove.Count; i++)
                    {
                        string inner = remove[i];
                        progress(string.Format("removing file {0:N0} of {1:N0} from the zip", i + 1, remove.Count), 0.6 * i / remove.Count);
                        var entries = zip.Entries.Where(e => e.FullName.Replace('/', '\\').TrimStart('\\')
                                                              .Equals(inner, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (entries.Count != 1) return "could not find '" + inner + "' inside the zip";
                        entries[0].Delete();
                    }
                    // The zip is actually rewritten when it is closed
                    progress("writing the new zip", 0.6);
                }
                return null;
            }
            if (ext == ".rar")
            {
                progress("copying the rar", 0);
                File.Copy(archive, rebuilt);
                // In batches, to stay well under the command-line length limit
                for (int i = 0; i < remove.Count; i += 50)
                {
                    progress(string.Format("removing files {0:N0}-{1:N0} of {2:N0} from the rar", i + 1, Math.Min(i + 50, remove.Count), remove.Count),
                             (double)i / remove.Count);
                    string names = string.Join(" ", remove.Skip(i).Take(50).Select(n => "\"" + n + "\""));
                    int code = ArchiveReader.Run(ArchiveReader.rarExe, "d -idq -y -- \"" + rebuilt + "\" " + names, null, out output);
                    if (code != 0) return "Rar.exe: " + ArchiveReader.Describe(code, output);
                }
                return null;
            }

            // Rebuilt from the extracted files: remove the files, pack the rest
            foreach (string inner in remove) File.Delete(Path.Combine(extracted, inner));
            progress("packing the remaining files into a new " + (ext == ".cab" ? "cab" : Path.GetFileName(archive).Contains(".tar") ? "tar" : ext.TrimStart('.')), 0.1);
            if (ext == ".cab") return BuildCab(extracted, rebuilt);
            var items = Directory.GetFileSystemEntries(extracted).Select(p => "\"" + Path.GetFileName(p) + "\"");
            string flags = ext == ".7z" ? "--format=7zip" : TarCompression[ext];
            int result = ArchiveReader.Run(ArchiveReader.tarExe,
                                           flags + " -c -f \"" + rebuilt + "\" -C \"" + extracted + "\" " + string.Join(" ", items), null, out output);
            return result == 0 ? null : "tar: " + ArchiveReader.Describe(result, output);
        }

        // makecab reads its directive file as ANSI, so names it can't represent are refused
        static string BuildCab(string folder, string cab)
        {
            var ansi = Encoding.Default;
            var lines = new List<string>
            {
                ".OPTION EXPLICIT",
                ".Set CabinetNameTemplate=\"" + Path.GetFileName(cab) + "\"",
                ".Set DiskDirectoryTemplate=\"" + Path.GetDirectoryName(cab) + "\"",
                ".Set Cabinet=on", ".Set Compress=on", ".Set CompressionType=LZX", ".Set CompressionMemory=21",
                ".Set MaxDiskSize=0", ".Set UniqueFiles=off", ".Set RptFileName=nul", ".Set InfFileName=nul"
            };
            foreach (var path in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (ansi.GetString(ansi.GetBytes(path)) != path) return "makecab can't store the file name '" + Path.GetFileName(path) + "'";
                lines.Add("\"" + path + "\" \"" + path.Substring(folder.Length + 1) + "\"");
            }
            string ddf = Path.ChangeExtension(cab, ".ddf");
            File.WriteAllLines(ddf, lines, ansi);
            string output;
            int code = ArchiveReader.Run(MakecabExe, "/F \"" + ddf + "\"", null, out output);
            return code == 0 && File.Exists(cab) ? null : "makecab: " + ArchiveReader.Describe(code, output);
        }
    }

    // ---------------------------------------------------------------- scanning

    public static class Scanner
    {
        const int PartialBytes = 64 * 1024;

        // Returns the duplicate groups, largest wasted space first, or null if cancelled.
        // Folders and files that cannot be read are added to errors.
        public static List<DupGroup> Scan(IList<string> roots, ScanOptions opt, Action<ScanProgress> report,
                                          Func<bool> cancelled, List<string> errors)
        {
            var throttle = Stopwatch.StartNew();
            var archives = new List<FileEntry>();
            var files = ListFiles(roots, opt, report, cancelled, errors, throttle, archives);
            if (files == null) return null;
            if (opt.IncludeArchives)
            {
                var entries = ReadArchives(archives, opt, report, cancelled, errors);
                if (entries == null) return null;
                files.AddRange(entries);
            }

            // 1. Only files of the same size (and name, if required) can be duplicates
            var bySize = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                string key = f.Size.ToString(CultureInfo.InvariantCulture);
                if (opt.SameName) key += "|" + f.Name;
                List<FileEntry> list;
                if (!bySize.TryGetValue(key, out list))
                {
                    list = new List<FileEntry>();
                    bySize.Add(key, list);
                }
                list.Add(f);
            }
            var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
            int total = candidates.Sum(l => l.Count);
            int done = 0;
            var groups = new List<DupGroup>();

            foreach (var sameSize in candidates)
            {
                // 2. Big files: compare the first 64 KB before reading them in full
                var stages = new List<List<FileEntry>>();
                if (sameSize[0].Size > PartialBytes * 2)
                {
                    var byStart = new Dictionary<string, List<FileEntry>>();
                    foreach (var f in sameSize)
                    {
                        if (cancelled()) return null;
                        Report(report, throttle, "Comparing contents", done, total, f.FullPath);
                        string h = f.InArchive ? f.PartialHash : HashFile(f.FullPath, PartialBytes, cancelled);
                        if (cancelled()) return null;
                        if (h == null) { errors.Add(f.FullPath + ": could not be read"); continue; }
                        AddTo(byStart, h, f);
                    }
                    foreach (var l in byStart.Values) if (l.Count > 1) stages.Add(l);
                    done += sameSize.Count - stages.Sum(l => l.Count);
                }
                else
                {
                    stages.Add(sameSize);
                }

                // 3. Full content
                foreach (var stage in stages)
                {
                    var byHash = new Dictionary<string, List<FileEntry>>();
                    foreach (var f in stage)
                    {
                        if (cancelled()) return null;
                        Report(report, throttle, "Comparing contents", done, total, f.FullPath);
                        // Files inside archives were hashed while extracted
                        if (!f.InArchive) f.Hash = HashFile(f.FullPath, -1, cancelled);
                        done++;
                        if (cancelled()) return null;
                        if (f.Hash == null) { errors.Add(f.FullPath + ": could not be read"); continue; }
                        AddTo(byHash, f.Hash, f);
                    }
                    foreach (var kv in byHash)
                    {
                        if (kv.Value.Count < 2) continue;
                        var g = new DupGroup { Size = kv.Value[0].Size, Hash = kv.Key };
                        foreach (var f in kv.Value.OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase))
                        {
                            f.Group = g;
                            g.Files.Add(f);
                        }
                        groups.Add(g);
                    }
                }
            }
            groups.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
            return groups;
        }

        // Also fills archives with every archive found, whatever its size: the minimum
        // size applies to the files inside it, and a small archive may hold big files
        static List<FileEntry> ListFiles(IList<string> roots, ScanOptions opt, Action<ScanProgress> report,
                                         Func<bool> cancelled, List<string> errors, Stopwatch throttle, List<FileEntry> archives)
        {
            var files = new List<FileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long minSize = Math.Max(1, opt.MinSize);
            foreach (string root in roots)
            {
                var dirs = new Stack<DirectoryInfo>();
                dirs.Push(new DirectoryInfo(root));
                while (dirs.Count > 0)
                {
                    if (cancelled()) return null;
                    DirectoryInfo dir = dirs.Pop();
                    Report(report, throttle, "Listing files", files.Count, 0, dir.FullName);
                    FileSystemInfo[] items;
                    try { items = dir.GetFileSystemInfos(); }
                    catch (Exception ex) { errors.Add(dir.FullName + ": " + ex.Message); continue; }

                    foreach (var item in items)
                    {
                        FileAttributes attr;
                        try { attr = item.Attributes; }
                        catch { continue; }
                        if (!opt.IncludeHidden && (attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                        var sub = item as DirectoryInfo;
                        if (sub != null)
                        {
                            // Junctions and symbolic links could loop or lead outside the chosen folders
                            if ((attr & FileAttributes.ReparsePoint) == 0) dirs.Push(sub);
                            continue;
                        }
                        var fi = (FileInfo)item;
                        long length;
                        DateTime modified;
                        try { length = fi.Length; modified = fi.LastWriteTimeUtc; }
                        catch { continue; }
                        // Overlapping folders (e.g. D:\ and D:\Photos) must not list a file twice
                        if (length == 0 || !seen.Add(fi.FullName)) continue;
                        var entry = new FileEntry { FullPath = fi.FullName, Size = length, Modified = modified };
                        if (opt.IncludeArchives && ArchiveReader.Extensions.Contains(fi.Extension)) archives.Add(entry);
                        if (length >= minSize) files.Add(entry);
                    }
                }
            }
            return files;
        }

        // Extracts each archive to a temporary folder, hashes the files inside, then
        // removes the folder before the next archive. Archives inside archives are
        // not opened. Returns null if cancelled.
        static List<FileEntry> ReadArchives(List<FileEntry> archives, ScanOptions opt, Action<ScanProgress> report,
                                            Func<bool> cancelled, List<string> errors)
        {
            var entries = new List<FileEntry>();
            long minSize = Math.Max(1, opt.MinSize);
            string tempBase = Path.Combine(opt.TempDir, "DF-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                for (int i = 0; i < archives.Count; i++)
                {
                    if (cancelled()) return null;
                    var archive = archives[i];
                    // Always reported: extracting one archive can take a while
                    if (report != null)
                        report(new ScanProgress { Phase = "Reading archives", Done = i, Total = archives.Count, Current = archive.FullPath });
                    string dest = Path.Combine(tempBase, i.ToString(CultureInfo.InvariantCulture));
                    try
                    {
                        string error = ArchiveReader.Extract(archive.FullPath, dest, cancelled);
                        if (cancelled()) return null;
                        if (error != null) { errors.Add(archive.FullPath + ": " + error); continue; }
                        foreach (var path in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories))
                        {
                            var fi = new FileInfo(path);
                            if (fi.Length < minSize) continue;
                            var entry = new FileEntry
                            {
                                FullPath = archive.FullPath + "\\" + path.Substring(dest.Length + 1),
                                Size = fi.Length,
                                Modified = fi.LastWriteTimeUtc,
                                Archive = archive.FullPath,
                                ArchiveModified = archive.Modified,
                                Hash = HashFile(path, -1, cancelled),
                                PartialHash = fi.Length > PartialBytes * 2 ? HashFile(path, PartialBytes, cancelled) : null
                            };
                            if (entry.Hash != null) entries.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(archive.FullPath + ": " + ex.Message);
                    }
                    finally
                    {
                        try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch (Exception) { }
                    }
                }
            }
            finally
            {
                try { if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true); } catch (Exception) { }
            }
            return entries;
        }

        static void AddTo(Dictionary<string, List<FileEntry>> dict, string key, FileEntry f)
        {
            List<FileEntry> list;
            if (!dict.TryGetValue(key, out list))
            {
                list = new List<FileEntry>();
                dict.Add(key, list);
            }
            list.Add(f);
        }

        static void Report(Action<ScanProgress> report, Stopwatch throttle, string phase, int done, int total, string current)
        {
            if (report == null || throttle.ElapsedMilliseconds < 100) return;
            throttle.Restart();
            report(new ScanProgress { Phase = phase, Done = done, Total = total, Current = current });
        }

        // SHA-256 of the whole file, or of its first maxBytes when maxBytes >= 0.
        // Returns null if the file cannot be read or the scan was cancelled.
        public static string HashFile(string path, long maxBytes, Func<bool> cancelled)
        {
            try
            {
                using (var sha = new SHA256CryptoServiceProvider())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                {
                    var buffer = new byte[1 << 20];
                    long remaining = maxBytes < 0 ? long.MaxValue : maxBytes;
                    int n;
                    while (remaining > 0 && (n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
                    {
                        sha.TransformBlock(buffer, 0, n, null, 0);
                        remaining -= n;
                        if (cancelled != null && cancelled()) return null;
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    return BitConverter.ToString(sha.Hash).Replace("-", "");
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // ------------------------------------------------------- selection rules

    public enum KeepRule { Newest, Oldest, ShortestPath, LongestPath, FirstAlphabetically }

    public static class Selection
    {
        // At least one copy of every file always stays unchecked; a copy inside an
        // archive counts too. Listed with ordinary files first: when the program itself
        // chooses which copy to keep, it keeps an ordinary file whenever there is one.
        public static List<FileEntry> MustKeepOneOf(DupGroup g)
        {
            return g.Valid.OrderBy(f => f.InArchive ? 1 : 0).ToList();
        }

        // Checks every ordinary copy except the one the rule keeps. Copies inside
        // archives are left alone (see CheckArchiveCopies).
        public static void KeepOne(IEnumerable<DupGroup> groups, KeepRule rule)
        {
            foreach (var g in groups)
            {
                var loose = g.Loose;
                if (loose.Count < 2) continue;
                var keep = Choose(loose, rule);
                foreach (var f in loose) f.Checked = f != keep;
            }
        }

        // The same, but only among the given files (e.g. those inside one folder): for
        // every file with 2+ copies among them, one is kept and the others checked.
        // Copies elsewhere, and files with a single copy among them, are not touched.
        // Returns how many files had copies to choose from.
        public static int KeepOneAmong(IEnumerable<FileEntry> files, KeepRule rule)
        {
            int changed = 0;
            foreach (var sameFile in files.Where(f => !f.Stale && !f.Deleted).GroupBy(f => f.Group))
            {
                var copies = sameFile.ToList();
                if (copies.Count < 2) continue;
                // The kept copy is an ordinary file when one is among them
                var candidates = copies.Where(f => !f.InArchive).ToList();
                var keep = Choose(candidates.Count > 0 ? candidates : copies, rule);
                foreach (var f in copies) f.Checked = f != keep;
                changed++;
            }
            return changed;
        }

        // Checks every copy inside an archive whose file also has an ordinary copy
        // (that stays). Returns how many were checked.
        public static int CheckArchiveCopies(IEnumerable<DupGroup> groups)
        {
            int count = 0;
            foreach (var g in groups)
            {
                if (g.Loose.Count == 0) continue;
                foreach (var f in g.Valid.Where(x => x.InArchive && !x.Checked))
                {
                    f.Checked = true;
                    count++;
                }
            }
            return count;
        }

        static FileEntry Choose(List<FileEntry> files, KeepRule rule)
        {
            var byPath = StringComparer.OrdinalIgnoreCase;
            switch (rule)
            {
                case KeepRule.Newest:
                    return files.OrderByDescending(f => f.Modified).ThenBy(f => f.FullPath, byPath).First();
                case KeepRule.Oldest:
                    return files.OrderBy(f => f.Modified).ThenBy(f => f.FullPath, byPath).First();
                case KeepRule.ShortestPath:
                    return files.OrderBy(f => f.FullPath.Length).ThenBy(f => f.FullPath, byPath).First();
                case KeepRule.LongestPath:
                    return files.OrderByDescending(f => f.FullPath.Length).ThenBy(f => f.FullPath, byPath).First();
                default:
                    return files.OrderBy(f => f.FullPath, byPath).First();
            }
        }

        // Checks (or unchecks) every copy inside folder and its subfolders. Never
        // checks every copy of a group: if all copies are inside, the first one is
        // left unchecked. Returns how many groups needed that.
        public static int CheckInFolder(IEnumerable<DupGroup> groups, string folder, bool check)
        {
            string prefix = folder.TrimEnd('\\') + "\\";
            var inside = groups.SelectMany(g => g.Files)
                               .Where(f => f.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return SetChecked(inside, check);
        }

        // Checks (or unchecks) the given files. Never checks every copy of a group:
        // where that would happen, the first of the given files from that group is
        // left unchecked. Returns how many groups needed that.
        public static int SetChecked(IEnumerable<FileEntry> files, bool check)
        {
            var list = files.Where(f => !f.Stale && !f.Deleted).ToList();
            foreach (var f in list) f.Checked = check;
            if (!check) return 0;
            int kept = 0;
            foreach (var g in list.Select(f => f.Group).Distinct().ToList())
            {
                if (g.DeleteAll) continue;   // the user explicitly chose to delete every copy
                var mustKeep = MustKeepOneOf(g);
                if (!mustKeep.All(f => f.Checked)) continue;
                // Leave the first of the given files unchecked, an ordinary one if possible
                var keep = list.Where(f => f.Group == g).OrderBy(f => f.InArchive ? 1 : 0)
                               .ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? mustKeep[0];
                keep.Checked = false;
                kept++;
            }
            return kept;
        }

        // A file may be checked only while another copy (ordinary or inside an archive)
        // remains unchecked
        public static bool CanCheck(FileEntry f)
        {
            if (f.Stale || f.Deleted) return false;
            return MustKeepOneOf(f.Group).Any(x => x != f && !x.Checked);
        }

        // Last check right before deleting f. Returns null if it is safe, otherwise
        // the reason to skip it. keeper is the unchecked copy that stays.
        // For a file inside an archive, the archive is checked here and the file's own
        // content when it is removed from the archive (ArchiveWriter).
        public static string VerifyBeforeDelete(FileEntry f, bool compareContents,
                                                Dictionary<DupGroup, FileEntry> verifiedKeepers, out FileEntry keeper)
        {
            keeper = null;
            if (f.InArchive)
            {
                if (!ArchiveUnchanged(f)) return "its archive changed or is missing since the scan";
            }
            else
            {
                var fi = new FileInfo(f.FullPath);
                if (!fi.Exists) return "it no longer exists";
                if (fi.Length != f.Size) return "its size changed";
                if (compareContents && Scanner.HashFile(f.FullPath, -1, null) != f.Group.Hash) return "its content changed";
            }

            FileEntry known;
            if (verifiedKeepers.TryGetValue(f.Group, out known) && KeeperStillThere(known, false))
            {
                keeper = known;
                return null;
            }
            foreach (var k in MustKeepOneOf(f.Group))
            {
                if (k.Checked || !KeeperStillThere(k, compareContents)) continue;
                verifiedKeepers[f.Group] = k;
                keeper = k;
                return null;
            }
            if (f.Group.DeleteAll)
            {
                // Deleting every copy was chosen explicitly: a copy inside an archive may
                // still remain (keeper), or none at all (keeper stays null)
                keeper = f.Group.Valid.FirstOrDefault(k => !k.Checked && KeeperStillThere(k, false));
                return null;
            }
            return "no unchecked copy of it was found on disk";
        }

        static bool ArchiveUnchanged(FileEntry f)
        {
            var ai = new FileInfo(f.Archive);
            return ai.Exists && ai.LastWriteTimeUtc.Ticks == f.ArchiveModified.Ticks;
        }

        static bool KeeperStillThere(FileEntry k, bool compareContents)
        {
            if (k.InArchive) return ArchiveUnchanged(k);
            var ki = new FileInfo(k.FullPath);
            if (!ki.Exists || ki.Length != k.Size) return false;
            return !compareContents || Scanner.HashFile(k.FullPath, -1, null) == k.Group.Hash;
        }
    }

    // ------------------------------------------------- saving / loading results

    public static class ResultsFile
    {
        const string Header = "DuplicateFinder results v1";

        // Tab-separated text: G <size> <hash>, then per copy F <checked> <modified UTC ticks> <path>,
        // or for a copy inside an archive A <checked> <modified UTC ticks> <archive modified UTC ticks> <archive> <path inside>
        public static void Save(string path, IList<DupGroup> groups, string folders)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine(Header);
                w.WriteLine("Folders\t" + folders);
                w.WriteLine("Saved\t" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                foreach (var g in groups)
                {
                    var files = g.Files.Where(f => !f.Deleted).ToList();
                    if (files.Count < 2) continue;
                    // A 4th field "D": the user chose to delete every copy of this file
                    w.WriteLine("G\t" + g.Size.ToString(CultureInfo.InvariantCulture) + "\t" + g.Hash + (g.DeleteAll ? "\tD" : ""));
                    foreach (var f in files)
                    {
                        if (f.InArchive)
                            w.WriteLine("A\t" + (f.Checked ? "1" : "0") + "\t" + f.Modified.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" +
                                        f.ArchiveModified.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" +
                                        f.Archive + "\t" + f.FullPath.Substring(f.Archive.Length + 1));
                        else
                            w.WriteLine("F\t" + (f.Checked ? "1" : "0") + "\t" +
                                        f.Modified.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" + f.FullPath);
                    }
                }
            }
        }

        // Every file is compared with the disk: missing or changed files (other size
        // or modified time) are marked Stale and can't be checked. Groups with fewer
        // than two usable copies are dropped. changed = number of stale files.
        public static List<DupGroup> Load(string path, out string folders, out int changed)
        {
            folders = "";
            changed = 0;
            var groups = new List<DupGroup>();
            DupGroup current = null;
            using (var r = new StreamReader(path, Encoding.UTF8, true))
            {
                if (r.ReadLine() != Header) throw new InvalidDataException("This is not a Duplicate File Finder results file.");
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    var parts = line.Split('\t');
                    if (parts[0] == "Folders" && parts.Length > 1) folders = parts[1];
                    else if (parts[0] == "G" && parts.Length >= 3)
                    {
                        current = new DupGroup { Size = long.Parse(parts[1], CultureInfo.InvariantCulture), Hash = parts[2],
                                                 DeleteAll = parts.Length >= 4 && parts[3] == "D" };
                        groups.Add(current);
                    }
                    else if (parts[0] == "F" && parts.Length >= 4 && current != null)
                    {
                        var f = new FileEntry
                        {
                            FullPath = parts[3],
                            Size = current.Size,
                            Hash = current.Hash,
                            Checked = parts[1] == "1",
                            Modified = new DateTime(long.Parse(parts[2], CultureInfo.InvariantCulture), DateTimeKind.Utc),
                            Group = current
                        };
                        var fi = new FileInfo(f.FullPath);
                        if (!fi.Exists) f.StaleReason = "missing";
                        else if (fi.Length != f.Size) f.StaleReason = "size changed";
                        else if (fi.LastWriteTimeUtc.Ticks != f.Modified.Ticks) f.StaleReason = "modified since saved";
                        if (f.StaleReason != null)
                        {
                            f.Stale = true;
                            f.Checked = false;
                            changed++;
                        }
                        current.Files.Add(f);
                    }
                    else if (parts[0] == "A" && parts.Length >= 5 && current != null)
                    {
                        // Older files had no "checked" field
                        int o = parts.Length >= 6 ? 1 : 0;
                        var f = new FileEntry
                        {
                            FullPath = parts[3 + o] + "\\" + parts[4 + o],
                            Archive = parts[3 + o],
                            Size = current.Size,
                            Hash = current.Hash,
                            Checked = o == 1 && parts[1] == "1",
                            Modified = new DateTime(long.Parse(parts[1 + o], CultureInfo.InvariantCulture), DateTimeKind.Utc),
                            ArchiveModified = new DateTime(long.Parse(parts[2 + o], CultureInfo.InvariantCulture), DateTimeKind.Utc),
                            Group = current
                        };
                        // The archive isn't reopened: an unchanged archive still holds the same file
                        var ai = new FileInfo(f.Archive);
                        if (!ai.Exists) f.StaleReason = "archive missing";
                        else if (ai.LastWriteTimeUtc.Ticks != f.ArchiveModified.Ticks) f.StaleReason = "archive modified since saved";
                        if (f.StaleReason != null)
                        {
                            f.Stale = true;
                            f.Checked = false;
                            changed++;
                        }
                        current.Files.Add(f);
                    }
                }
            }
            groups.RemoveAll(g => g.Valid.Count < 2);
            foreach (var g in groups)
            {
                // Never load a group with every copy that must stay checked, unless deleting
                // every copy was chosen explicitly (and nothing changed since)
                var mustKeep = Selection.MustKeepOneOf(g);
                if (g.DeleteAll && g.Files.All(f => !f.Stale) && mustKeep.All(f => f.Checked)) continue;
                g.DeleteAll = false;
                if (mustKeep.All(f => f.Checked)) mustKeep[0].Checked = false;
            }
            return groups;
        }
    }

    // ------------------------------------------------------------ recycle bin

    static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
                     FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        // Sends a file to the Recycle Bin. If it can't be recycled (too big, network
        // drive...), Windows asks before deleting it permanently. Returns null on
        // success, or the reason it was not deleted.
        public static string Delete(string path)
        {
            if (path.Length >= 260) return "path too long for the Recycle Bin";
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0",   // the list must end with two nulls; the marshaller adds one
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING)
            };
            int result = SHFileOperation(ref op);
            if (op.fAnyOperationsAborted) return "cancelled";
            if (result != 0) return "Windows error 0x" + result.ToString("X");
            if (File.Exists(path)) return "the file is still there";
            return null;
        }
    }

    // ----------------------------------------------------------- folder picker

    // The regular Explorer-style open dialog in folder mode (.NET's FolderBrowserDialog
    // only offers the old folder tree). Falls back to that tree if the dialog fails.
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCoClass { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int place);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IShellItemArray items);
            void GetSelectedItems(out IShellItemArray items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemArray
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
            void GetPropertyStore(int flags, ref Guid riid, out IntPtr result);
            void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr result);
            void GetAttributes(int flags, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
            void EnumItems(out IntPtr items);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
                                                       [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_ALLOWMULTISELECT = 0x200, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int ERROR_CANCELLED_HR = unchecked((int)0x800704C7);

        // Returns the chosen folder(s), or null if cancelled
        public static List<string> Pick(IWin32Window owner, string title, string startFolder, bool multiple)
        {
            IFileOpenDialog dialog;
            try { dialog = (IFileOpenDialog)new FileOpenDialogCoClass(); }
            catch (Exception) { return PickWithTree(owner, title, startFolder); }
            try
            {
                dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | (multiple ? FOS_ALLOWMULTISELECT : 0));
                dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
                {
                    try
                    {
                        Guid shellItemId = typeof(IShellItem).GUID;
                        IShellItem folder;
                        SHCreateItemFromParsingName(startFolder, IntPtr.Zero, ref shellItemId, out folder);
                        dialog.SetFolder(folder);
                    }
                    catch (Exception) { }   // just start in the default folder
                }
                int hr = dialog.Show(owner == null ? IntPtr.Zero : owner.Handle);
                if (hr == ERROR_CANCELLED_HR) return null;
                Marshal.ThrowExceptionForHR(hr);

                IShellItemArray items;
                dialog.GetResults(out items);
                uint count;
                items.GetCount(out count);
                var paths = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    IShellItem item;
                    items.GetItemAt(i, out item);
                    string path;
                    item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    paths.Add(path);
                }
                return paths;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        static List<string> PickWithTree(IWin32Window owner, string title, string startFolder)
        {
            using (var dlg = new FolderBrowserDialog { Description = title })
            {
                if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder)) dlg.SelectedPath = startFolder;
                return dlg.ShowDialog(owner) == DialogResult.OK ? new List<string> { dlg.SelectedPath } : null;
            }
        }
    }

    // ------------------------------------------------- Windows preview handlers

    // Hosts the same preview handlers as File Explorer's preview pane (PDF, Office,
    // PowerToys' SVG/Markdown/code previewers...) and gets Explorer's thumbnails.
    // Handlers are always started out of process (in Windows' prevhost.exe, as
    // Explorer does), so a misbehaving handler can't crash this program.
    static class ShellPreview
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RECT(Rectangle r) { Left = r.Left; Top = r.Top; Right = r.Right; Bottom = r.Bottom; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam, lParam;
            public int time, x, y;
        }

        [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPreviewHandler
        {
            void SetWindow(IntPtr hwnd, ref RECT rect);
            void SetRect(ref RECT rect);
            void DoPreview();
            void Unload();
            void SetFocus();
            void QueryFocus(out IntPtr hwnd);
            [PreserveSig] int TranslateAccelerator(ref MSG msg);
        }

        [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IInitializeWithFile
        {
            void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
        }

        [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IInitializeWithItem
        {
            void Initialize([MarshalAs(UnmanagedType.IUnknown)] object item, uint mode);
        }

        [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IInitializeWithStream
        {
            void Initialize(System.Runtime.InteropServices.ComTypes.IStream stream, uint mode);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder result, ref int length);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template,
                                                 out System.Runtime.InteropServices.ComTypes.IStream stream);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
                                                      [MarshalAs(UnmanagedType.IUnknown)] out object item);

        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
                                           [MarshalAs(UnmanagedType.IUnknown)] out object result);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);

        const uint CLSCTX_LOCAL_SERVER = 4, STGM_READ = 0, STGM_SHARE_DENY_NONE = 0x40;
        const int ASSOCSTR_SHELLEXTENSION = 16, SIIGBF_THUMBNAILONLY = 0x8;
        static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        static readonly Guid IID_IShellItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        const string PreviewHandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

        // The preview handler registered for this file type, if any
        public static Guid? HandlerFor(string path)
        {
            var result = new StringBuilder(64);
            int length = result.Capacity;
            if (AssocQueryString(0, ASSOCSTR_SHELLEXTENSION, Path.GetExtension(path), PreviewHandlerKey, result, ref length) != 0) return null;
            Guid clsid;
            return Guid.TryParse(result.ToString(), out clsid) ? clsid : (Guid?)null;
        }

        // Shows the file in host with its preview handler. Returns the handler (to be
        // passed to Close later), or null with the reason. resource is what must be
        // released together with the handler (a stream), if any.
        public static IPreviewHandler Open(string path, Control host, out object resource, out string error)
        {
            resource = null;
            error = null;
            var clsid = HandlerFor(path);
            if (clsid == null) { error = "no preview handler for this file type"; return null; }
            Guid id = clsid.Value, iunknown = IID_IUnknown;
            object handler;
            int hr = CoCreateInstance(ref id, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iunknown, out handler);
            if (hr != 0 || handler == null) { error = "the preview handler could not be started (0x" + hr.ToString("X8") + ")"; return null; }
            try
            {
                if (handler is IInitializeWithFile)
                    ((IInitializeWithFile)handler).Initialize(path, STGM_READ);
                else if (handler is IInitializeWithItem)
                {
                    Guid iid = IID_IShellItem;
                    object item;
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
                    resource = item;
                    ((IInitializeWithItem)handler).Initialize(item, STGM_READ);
                }
                else if (handler is IInitializeWithStream)
                {
                    System.Runtime.InteropServices.ComTypes.IStream stream;
                    Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out stream));
                    resource = stream;
                    ((IInitializeWithStream)handler).Initialize(stream, STGM_READ);
                }
                else
                {
                    error = "the preview handler can't be given this file";
                    Close((IPreviewHandler)null, handler, resource);
                    resource = null;
                    return null;
                }
                var preview = (IPreviewHandler)handler;
                var rect = new RECT(host.ClientRectangle);
                preview.SetWindow(host.Handle, ref rect);
                preview.DoPreview();
                return preview;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Close(handler as IPreviewHandler, handler, resource);
                resource = null;
                return null;
            }
        }

        public static void Resize(IPreviewHandler preview, Control host)
        {
            try
            {
                var rect = new RECT(host.ClientRectangle);
                preview.SetRect(ref rect);
            }
            catch (Exception) { }
        }

        public static void Close(IPreviewHandler preview, object handler, object resource)
        {
            try { if (preview != null) preview.Unload(); } catch (Exception) { }
            try { if (handler != null && Marshal.IsComObject(handler)) Marshal.FinalReleaseComObject(handler); } catch (Exception) { }
            try { if (resource != null && Marshal.IsComObject(resource)) Marshal.FinalReleaseComObject(resource); } catch (Exception) { }
        }

        // Explorer's thumbnail of the file (video frame, picture...), or null if it has none
        public static Bitmap Thumbnail(string path, int size)
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            object item;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item) != 0 || item == null) return null;
            try
            {
                IntPtr bitmap;
                if (((IShellItemImageFactory)item).GetImage(new SIZE { cx = size, cy = size }, SIIGBF_THUMBNAILONLY, out bitmap) != 0) return null;
                try { return Image.FromHbitmap(bitmap); }
                finally { DeleteObject(bitmap); }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(item);
            }
        }
    }

    // ------------------------------------------------------------------- tree

    // The checkboxes are drawn as state images, because the built-in TreeView
    // checkboxes have no "partly checked" state for folders
    class DupTree : TreeView
    {
        public const int Unchecked = 0, Checked = 1, Mixed = 2, Disabled = 3;

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        const int TVM_SETEXTENDEDSTYLE = 0x112C, TVS_EX_DOUBLEBUFFER = 0x4;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Less flicker when many nodes change at once
            SendMessage(Handle, TVM_SETEXTENDEDSTYLE, (IntPtr)TVS_EX_DOUBLEBUFFER, (IntPtr)TVS_EX_DOUBLEBUFFER);
        }

        public void UseCheckImages()
        {
            int size;
            using (var g = CreateGraphics()) size = (int)Math.Round(16 * g.DpiX / 96f);
            var images = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
            // Mixed is drawn as an empty box with a solid square inside: the themed "mixed"
            // glyph is a faint gray tick, too easy to mistake for "checked"
            var states = new[] { System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal,
                                 System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal,
                                 System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal,
                                 System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled };
            for (int i = 0; i < states.Length; i++)
            {
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    var glyph = CheckBoxRenderer.GetGlyphSize(g, states[i]);
                    var at = new Point((size - glyph.Width) / 2, (size - glyph.Height) / 2);
                    CheckBoxRenderer.DrawCheckBox(g, at, states[i]);
                    if (i == Mixed)
                    {
                        int inset = Math.Max(3, glyph.Width / 4);
                        g.FillRectangle(Brushes.DimGray, at.X + inset, at.Y + inset, glyph.Width - 2 * inset, glyph.Height - 2 * inset);
                    }
                }
                images.Images.Add(bmp);
            }
            StateImageList = images;
        }

        public static void SetState(TreeNode node, int state)
        {
            if (node.StateImageIndex != state) node.StateImageIndex = state;
        }
    }

    // Tag of a folder node in the folder view
    class FolderTag
    {
        public string FullPath;
        public string Name;     // as shown, e.g. D:\Sync\Photos for a merged chain
        public bool IsArchive;  // an archive file shown as a folder of its contents
    }

    // --------------------------------------------------------------- settings

    // Settings kept between sessions, as key=value lines in
    // %APPDATA%\Duplicate File Finder\settings.ini. Problems reading or writing it are
    // ignored: the app then simply starts with its defaults.
    static class Settings
    {
        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Duplicate File Finder", "settings.ini");
        // Where versions named "Duplicate Finder" kept them; read once, until the first save
        static readonly string OldFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Duplicate Finder", "settings.ini");

        public static Dictionary<string, string> Load()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = File.Exists(FilePath) ? FilePath : OldFilePath;
                if (!File.Exists(path)) return values;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
                }
            }
            catch (Exception) { }
            return values;
        }

        public static void Save(Dictionary<string, string> values)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, values.Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(true));
            }
            catch (Exception) { }
        }
    }

    // -------------------------------------------------------------- main form

    public class MainForm : Form
    {
        static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico" };
        static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".csv", ".log", ".xml", ".json", ".ini", ".html", ".htm", ".css", ".js", ".sql",
              ".ps1", ".bat", ".cmd", ".py", ".cs", ".c", ".h", ".cpp", ".java", ".fasta", ".fa", ".fas", ".seq",
              ".nex", ".phy", ".tsv", ".yml", ".yaml", ".reg", ".inf", ".srt" };
        // Played in the preview pane by the built-in browser engine (which knows these formats)
        static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v" };
        static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac" };

        Font menuBoldFont;                          // "Open" in the right-click menu, created once

        // Windows allows a program 10,000 drawing (GDI) and 10,000 window (USER) handles;
        // past that, text draws wrongly and the program crashes. Checked regularly.
        [DllImport("user32.dll")]
        static extern uint GetGuiResources(IntPtr process, uint flags);
        Timer resourceTimer;
        bool resourceWarningShown;

        Panel previewHost, handlerPanel;
        WebBrowser mediaView;                       // created the first time a video or song is previewed
        string mediaFolder;                         // temp folder for the player page
        ShellPreview.IPreviewHandler previewHandler;
        object previewHandlerResource;

        readonly string[] startArgs;
        List<DupGroup> groups = new List<DupGroup>();
        List<DupGroup> shownGroups = new List<DupGroup>();   // the groups passing the filter
        List<string> errors = new List<string>();
        bool busy;
        BackgroundWorker worker;
        Image previewImage;
        MemoryStream previewStream;
        Font groupFont, fileFont;

        TextBox txtFolders;
        Button btnAddFolder, btnFolderList, btnScan, btnCancel, btnDelete, btnOpen, btnShowFolder;
        LinkLabel lblFolderCount;
        ContextMenuStrip folderList;
        // Every folder added this session, in order; the ticked ones are those in the Folders box
        List<string> knownFolders = new List<string>();
        NumericUpDown numMinKb;
        CheckBox chkHidden, chkSameName, chkArchives, chkVerify, chkPermanent;
        ToolStrip tools;
        ToolStripTextBox txtFilter;
        ToolStripComboBox cboSort, cboView;
        DupTree tree;
        SplitContainer split;
        TextBox txtInfo, txtPreview;
        PictureBox picture;
        Label lblNoPreview, lblChecked;
        ToolStripStatusLabel lblStatus, lblErrors;
        ToolStripProgressBar progress;
        ContextMenuStrip fileMenu, groupMenu, folderMenu;
        Timer filterTimer;

        public MainForm(string[] args)
        {
            startArgs = args;
            Text = "Duplicate File Finder";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            Size = new Size(1280, 800);
            MinimumSize = new Size(800, 500);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("DuplicateFileFinder.ico"))
                if (icon != null) Icon = new Icon(icon);
            BuildLayout();
            UpdateTotals();
            SetStatus("Choose one or more folders and click Scan. Tip: you can also drag folders onto this window.");
            ApplySettings();
            DeleteOldPreviewFolders();
            UpdateFolderCount();
            resourceTimer = new Timer { Interval = 5000 };
            resourceTimer.Tick += (s, e) => CheckResources();
            resourceTimer.Start();
        }

        // Handles waiting for the garbage collector are freed when they pile up; if the
        // counts stay high anyway, the user is warned before things break
        void CheckResources()
        {
            IntPtr me = Process.GetCurrentProcess().Handle;
            uint gdi = GetGuiResources(me, 0), user = GetGuiResources(me, 1);
            if (gdi < 4000 && user < 4000) return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            gdi = GetGuiResources(me, 0);
            user = GetGuiResources(me, 1);
            if ((gdi >= 7000 || user >= 7000) && !resourceWarningShown)
            {
                resourceWarningShown = true;
                SetStatus(string.Format("Windows resources are running low ({0:N0} drawing, {1:N0} window handles of 10,000): " +
                                        "save your results (Ctrl+S) and restart the program.", gdi, user));
                System.Media.SystemSounds.Exclamation.Play();
            }
        }

        // ------------------------------------------------------- settings

        // True until the window has been laid out, so restoring settings doesn't save them
        bool loadingSettings = true;
        double? savedSplitter;
        Timer saveTimer;

        static bool Flag(Dictionary<string, string> s, string key)
        {
            string v;
            return s.TryGetValue(key, out v) && v == "1";
        }

        static int? Number(Dictionary<string, string> s, string key)
        {
            string v;
            int n;
            return s.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : (int?)null;
        }

        void ApplySettings()
        {
            var s = Settings.Load();
            string v;
            if (s.TryGetValue("KnownFolders", out v)) knownFolders = v.Split('|').Where(x => x.Length > 0).ToList();
            if (s.TryGetValue("Folders", out v)) txtFolders.Text = v;
            decimal kb;
            if (s.TryGetValue("MinKb", out v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out kb))
                numMinKb.Value = Math.Max(numMinKb.Minimum, Math.Min(numMinKb.Maximum, kb));
            chkHidden.Checked = Flag(s, "IncludeHidden");
            chkSameName.Checked = Flag(s, "SameName");
            chkArchives.Checked = Flag(s, "IncludeArchives");
            chkVerify.Checked = Flag(s, "RecompareContents");
            // "Delete permanently" is deliberately not restored: it always starts unticked
            int? view = Number(s, "View"), sort = Number(s, "Sort");
            if (view >= 0 && view < cboView.Items.Count) cboView.SelectedIndex = view.Value;
            if (sort >= 0 && sort < cboSort.Items.Count) cboSort.SelectedIndex = sort.Value;

            // Window position, unless it would be off-screen (e.g. a monitor was removed)
            if (s.TryGetValue("Window", out v))
            {
                var p = v.Split(',').Select(x => { int n; return int.TryParse(x, out n) ? n : -1; }).ToArray();
                if (p.Length == 4 && p[2] >= MinimumSize.Width && p[3] >= MinimumSize.Height)
                {
                    var r = new Rectangle(p[0], p[1], p[2], p[3]);
                    if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
                    {
                        StartPosition = FormStartPosition.Manual;
                        Bounds = r;
                    }
                }
            }
            if (Flag(s, "Maximized")) WindowState = FormWindowState.Maximized;
            videoSupportDeclined = Flag(s, "VideoSupportDeclined");
            double ratio;
            if (s.TryGetValue("Splitter", out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) && ratio > 0.1 && ratio < 0.9)
                savedSplitter = ratio;

            saveTimer = new Timer { Interval = 500 };
            saveTimer.Tick += (sender, e) => { saveTimer.Stop(); SaveSettings(); };
            EventHandler changed = (sender, e) => ScheduleSave();
            txtFolders.TextChanged += changed;
            numMinKb.ValueChanged += changed;
            chkHidden.CheckedChanged += changed;
            chkSameName.CheckedChanged += changed;
            chkArchives.CheckedChanged += changed;
            chkVerify.CheckedChanged += changed;
            cboView.SelectedIndexChanged += changed;
            cboSort.SelectedIndexChanged += changed;
            split.SplitterMoved += (sender, e) => ScheduleSave();
            ResizeEnd += changed;
            Resize += changed;   // also catches maximize / restore
        }

        // Saved half a second after the last change, so typing doesn't write on every key
        void ScheduleSave()
        {
            if (loadingSettings || saveTimer == null) return;
            saveTimer.Stop();
            saveTimer.Start();
        }

        void SaveSettings()
        {
            if (loadingSettings) return;
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var s = new Dictionary<string, string>
            {
                { "Folders", txtFolders.Text },
                { "KnownFolders", string.Join("|", knownFolders) },
                { "MinKb", numMinKb.Value.ToString(CultureInfo.InvariantCulture) },
                { "IncludeHidden", chkHidden.Checked ? "1" : "0" },
                { "SameName", chkSameName.Checked ? "1" : "0" },
                { "IncludeArchives", chkArchives.Checked ? "1" : "0" },
                { "RecompareContents", chkVerify.Checked ? "1" : "0" },
                { "View", cboView.SelectedIndex.ToString(CultureInfo.InvariantCulture) },
                { "Sort", cboSort.SelectedIndex.ToString(CultureInfo.InvariantCulture) },
                { "Window", string.Join(",", bounds.X, bounds.Y, bounds.Width, bounds.Height) },
                { "Maximized", WindowState == FormWindowState.Maximized ? "1" : "0" },
                { "VideoSupportDeclined", videoSupportDeclined ? "1" : "0" }
            };
            if (split.Width > 0)
                s["Splitter"] = ((double)split.SplitterDistance / split.Width).ToString("0.000", CultureInfo.InvariantCulture);
            Settings.Save(s);
        }

        // ---------------------------------------------------------- layout

        void BuildLayout()
        {
            // Top: folders and options
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Padding = new Padding(8, 8, 8, 2) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(new Label { Text = "Folders:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) }, 0, 0);
            txtFolders = new TextBox { Dock = DockStyle.Fill, AllowDrop = true };
            new ToolTip().SetToolTip(txtFolders, "Folders to search, separated by ;  (all are compared with each other)");
            top.Controls.Add(txtFolders, 1, 0);
            // "Add folder..." with an arrow next to it listing every folder added, with ticks
            btnAddFolder = new Button { Text = "Add folder...", AutoSize = true, Margin = new Padding(3, 3, 0, 3) };
            btnFolderList = new Button { Text = "\u25BE", Width = 24, Margin = new Padding(0, 3, 3, 3) };
            btnFolderList.Height = btnAddFolder.PreferredSize.Height;
            new ToolTip().SetToolTip(btnFolderList, "All folders added: tick the ones to search, or clear the list");
            btnScan = new Button { Text = "Scan", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            btnCancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
            top.Controls.Add(btnAddFolder, 2, 0);
            top.Controls.Add(btnFolderList, 3, 0);
            top.Controls.Add(btnScan, 4, 0);
            top.Controls.Add(btnCancel, 5, 0);

            var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
            options.Controls.Add(new Label { Text = "Ignore files smaller than", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            numMinKb = new NumericUpDown { Minimum = 0, Maximum = 10000000, Value = 1, Width = 80 };
            options.Controls.Add(numMinKb);
            options.Controls.Add(new Label { Text = "KB", AutoSize = true, Margin = new Padding(2, 6, 16, 0) });
            chkHidden = new CheckBox { Text = "Include hidden/system files", AutoSize = true, Margin = new Padding(0, 4, 16, 0) };
            chkSameName = new CheckBox { Text = "Same file name required", AutoSize = true, Margin = new Padding(0, 4, 16, 0) };
            new ToolTip().SetToolTip(chkSameName, "Copies must also have the same file name to count as duplicates");
            chkArchives = new CheckBox { Text = "Include archives (zip, rar, 7z, cab, ...)", AutoSize = true, Margin = new Padding(0, 4, 16, 0) };
            new ToolTip { AutoPopDelay = 20000 }.SetToolTip(chkArchives,
                "Also compare the files inside compressed files. They are shown in blue under their archive and\r\n" +
                "can be checked like any file: they are then removed from the archive, and an archive left empty\r\n" +
                "is deleted. One copy of every file always stays; a copy inside an archive counts too.\r\n" +
                "Uses " + (ArchiveReader.AvailableTools.Length > 0 ? ArchiveReader.AvailableTools : "no tool found: install WinRAR or 7-Zip") + ".");
            options.Controls.Add(chkHidden);
            options.Controls.Add(chkSameName);
            options.Controls.Add(chkArchives);
            top.Controls.Add(options, 0, 1);
            top.SetColumnSpan(options, 2);
            // Under "Add folder...": how many folders are listed, and how many are ticked
            // (spans the button columns, so a long text never widens them)
            lblFolderCount = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(3, 0, 3, 0),
                                             LinkBehavior = LinkBehavior.HoverUnderline };
            lblFolderCount.LinkClicked += (s, e) => ShowFolderList();
            new ToolTip().SetToolTip(lblFolderCount, "Click to see the folders and tick or untick them");
            top.Controls.Add(lblFolderCount, 2, 1);
            top.SetColumnSpan(lblFolderCount, 4);

            // Toolbar
            tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 0, 6, 0) };
            var auto = new ToolStripDropDownButton("Auto-select");
            auto.ToolTipText = "Check copies automatically in the groups currently shown";
            auto.DropDownItems.Add("Keep the newest copy, check the others", null, (s, e) => ApplyKeepRule(KeepRule.Newest));
            auto.DropDownItems.Add("Keep the oldest copy, check the others", null, (s, e) => ApplyKeepRule(KeepRule.Oldest));
            auto.DropDownItems.Add("Keep the copy with the shortest path, check the others", null, (s, e) => ApplyKeepRule(KeepRule.ShortestPath));
            auto.DropDownItems.Add("Keep the copy with the longest path, check the others", null, (s, e) => ApplyKeepRule(KeepRule.LongestPath));
            auto.DropDownItems.Add("Keep the first copy alphabetically, check the others", null, (s, e) => ApplyKeepRule(KeepRule.FirstAlphabetically));
            auto.DropDownItems.Add(new ToolStripSeparator());
            auto.DropDownItems.Add("Also check the copies inside archives (an ordinary copy stays)", null, (s, e) => CheckArchiveCopies());
            auto.DropDownItems.Add(new ToolStripSeparator());
            auto.DropDownItems.Add("Check all copies inside a folder...", null, (s, e) => PickFolderAndCheck(true));
            auto.DropDownItems.Add("Uncheck all copies inside a folder...", null, (s, e) => PickFolderAndCheck(false));
            auto.DropDownItems.Add(new ToolStripSeparator());
            auto.DropDownItems.Add("Uncheck everything", null, (s, e) => UncheckAll());
            tools.Items.Add(auto);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(new ToolStripLabel("View:"));
            cboView = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 140,
                                              ToolTipText = "Folders: the folder tree with the duplicates in it.  Duplicate groups: each set of identical files together." };
            cboView.Items.AddRange(new object[] { "Folders", "Duplicate groups" });
            cboView.SelectedIndex = 0;
            tools.Items.Add(cboView);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(new ToolStripLabel("Filter:"));
            txtFilter = new ToolStripTextBox { Width = 220, ToolTipText = "Show only groups with a path containing this text (Ctrl+F)",
                                               BorderStyle = BorderStyle.FixedSingle };
            tools.Items.Add(txtFilter);
            tools.Items.Add(new ToolStripLabel("Sort by:"));
            cboSort = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 150 };
            cboSort.Items.AddRange(new object[] { "Space wasted", "Number of copies", "File name", "Folder" });
            cboSort.SelectedIndex = 0;
            tools.Items.Add(cboSort);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(new ToolStripButton("Expand all", null, (s, e) => { tree.BeginUpdate(); tree.ExpandAll(); tree.EndUpdate(); }));
            tools.Items.Add(new ToolStripButton("Collapse all", null, (s, e) => { tree.BeginUpdate(); tree.CollapseAll(); tree.EndUpdate(); }));
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(new ToolStripButton("Save results...", null, (s, e) => SaveResults()) { ToolTipText = "Save the results and checkmarks to continue later (Ctrl+S)" });
            tools.Items.Add(new ToolStripButton("Load results...", null, (s, e) => LoadResults()) { ToolTipText = "Load saved results (Ctrl+O)" });
            tools.Items.Add(new ToolStripButton("Export CSV...", null, (s, e) => ExportCsv()));
            var help = new ToolStripDropDownButton("Help") { Alignment = ToolStripItemAlignment.Right };
            help.DropDownItems.Add("How to use Duplicate File Finder", null, (s, e) => ShowHelp());
            installVideoItem = new ToolStripMenuItem("Install video playback support (Windows Server)...", null, (s, e) => InstallMediaFoundation())
            {
                ToolTipText = "Installs the Windows feature Media Foundation, which the preview needs to play videos on Windows Server",
                Enabled = IsWindowsServer()
            };
            help.DropDownItems.Add(installVideoItem);
            tools.Items.Add(help);

            // Middle: tree | preview
            split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.None };
            groupFont = new Font(Font, FontStyle.Bold);
            fileFont = new Font(Font, FontStyle.Regular);
            // The tree font is bold so bold group/folder names aren't clipped; files use a regular font
            // The text is drawn here (Tree_DrawNode) rather than with a font per node: with
            // NodeFont, Windows Forms makes a new font handle every time a line is drawn,
            // freed only by the garbage collector. With thousands of files that can run
            // out of Windows drawing handles (names shown as one letter, then a crash).
            tree = new DupTree { Dock = DockStyle.Fill, HideSelection = false, Font = groupFont, ShowNodeToolTips = true,
                                 DrawMode = TreeViewDrawMode.OwnerDrawText };
            tree.DrawNode += Tree_DrawNode;
            tree.UseCheckImages();
            split.Panel1.Controls.Add(tree);

            var preview = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(4) };
            preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            txtInfo = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
                                    BackColor = SystemColors.Control, ScrollBars = ScrollBars.Vertical, WordWrap = false };
            preview.Controls.Add(txtInfo, 0, 0);
            var host = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
            picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
            txtPreview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                                       Font = new Font("Consolas", 9f), Visible = false, BackColor = Color.White };
            lblNoPreview = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText,
                                       Text = "Click a file to preview it." };
            // Window that Windows preview handlers draw into
            handlerPanel = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Color.White };
            handlerPanel.Resize += (s, e) => { if (previewHandler != null) ShellPreview.Resize(previewHandler, handlerPanel); };
            host.Controls.Add(handlerPanel);
            host.Controls.Add(picture);
            host.Controls.Add(txtPreview);
            host.Controls.Add(lblNoPreview);
            previewHost = host;
            preview.Controls.Add(host, 0, 1);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            btnOpen = new Button { Text = "Open file", AutoSize = true, Enabled = false };
            btnShowFolder = new Button { Text = "Show in folder", AutoSize = true, Enabled = false };
            buttons.Controls.Add(btnOpen);
            buttons.Controls.Add(btnShowFolder);
            preview.Controls.Add(buttons, 0, 2);
            split.Panel2.Controls.Add(preview);

            // Bottom: checked totals and Delete
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(8, 4, 8, 4) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblChecked = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) };
            chkVerify = new CheckBox { Text = "Re-compare contents before deleting (slower, safest)", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(3, 6, 12, 3) };
            chkPermanent = new CheckBox { Text = "Delete permanently (skip the Recycle Bin)", AutoSize = true, Anchor = AnchorStyles.Right,
                                          Margin = new Padding(3, 6, 12, 3), ForeColor = Color.Firebrick };
            new ToolTip().SetToolTip(chkPermanent, "Deleted files can't be restored from the Recycle Bin. Faster, and frees the space at once.");
            btnDelete = new Button { Text = "Delete checked files...", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Font = new Font(Font, FontStyle.Bold) };
            bottom.Controls.Add(lblChecked, 0, 0);
            bottom.Controls.Add(chkVerify, 1, 0);
            bottom.Controls.Add(chkPermanent, 2, 0);
            bottom.Controls.Add(btnDelete, 3, 0);

            var status = new StatusStrip();
            lblStatus = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            lblErrors = new ToolStripStatusLabel { IsLink = true, Visible = false };
            progress = new ToolStripProgressBar { Width = 200, Visible = false };
            status.Items.Add(lblStatus);
            status.Items.Add(lblErrors);
            status.Items.Add(progress);

            // Docking: the control added last is placed outermost
            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(status);
            Controls.Add(tools);
            Controls.Add(top);

            // Built when it opens, for the file clicked
            fileMenu = new ContextMenuStrip();
            fileMenu.Opening += (s, e) =>
            {
                // Dispose the previous items: their submenus are windows, which would
                // otherwise never be destroyed (a few window handles lost per right-click)
                var old = fileMenu.Items.Cast<ToolStripItem>().ToList();
                fileMenu.Items.Clear();
                foreach (var item in old) item.Dispose();
                var f = SelectedFile();
                if (f == null) { e.Cancel = true; return; }
                fileMenu.Items.AddRange(FileMenuItems(f, true));
                e.Cancel = false;
            };
            groupMenu = new ContextMenuStrip();
            groupMenu.Items.Add("Keep the first copy, check the others", null, (s, e) => KeepFirstInSelectedGroup());
            groupMenu.Items.Add("Uncheck this group", null, (s, e) => UncheckSelectedGroup());
            folderMenu = new ContextMenuStrip();
            folderMenu.Items.Add("Check all duplicates in this folder", null, (s, e) => SetNodeChecked(tree.SelectedNode, true));
            folderMenu.Items.Add("Uncheck all duplicates in this folder", null, (s, e) => SetNodeChecked(tree.SelectedNode, false));
            folderMenu.Items.Add(new ToolStripSeparator());
            var folderAuto = new ToolStripMenuItem("Auto-select inside this folder");
            folderAuto.ToolTipText = "For each file with several copies in this folder and its subfolders, keep one of them and check the others. Copies elsewhere are not changed.";
            folderAuto.DropDownItems.Add("Keep the newest copy, check the others", null, (s, e) => ApplyKeepRuleInFolder(tree.SelectedNode, KeepRule.Newest));
            folderAuto.DropDownItems.Add("Keep the oldest copy, check the others", null, (s, e) => ApplyKeepRuleInFolder(tree.SelectedNode, KeepRule.Oldest));
            folderAuto.DropDownItems.Add("Keep the copy with the shortest path, check the others", null, (s, e) => ApplyKeepRuleInFolder(tree.SelectedNode, KeepRule.ShortestPath));
            folderAuto.DropDownItems.Add("Keep the copy with the longest path, check the others", null, (s, e) => ApplyKeepRuleInFolder(tree.SelectedNode, KeepRule.LongestPath));
            folderAuto.DropDownItems.Add("Keep the first copy alphabetically, check the others", null, (s, e) => ApplyKeepRuleInFolder(tree.SelectedNode, KeepRule.FirstAlphabetically));
            folderMenu.Items.Add(folderAuto);
            folderMenu.Items.Add(new ToolStripSeparator());
            folderMenu.Items.Add("Open folder", null, (s, e) => OpenFolder(tree.SelectedNode));
            folderMenu.Items.Add("Expand everything below", null, (s, e) => { if (tree.SelectedNode != null) tree.SelectedNode.ExpandAll(); });

            filterTimer = new Timer { Interval = 400 };
            filterTimer.Tick += (s, e) => { filterTimer.Stop(); BuildTree(); };

            // Events
            btnAddFolder.Click += (s, e) => AddFolder();
            txtFolders.TextChanged += (s, e) => UpdateFolderCount();
            folderList = new ContextMenuStrip();
            // Stay open while folders are ticked and unticked
            folderList.Closing += (s, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
            btnFolderList.Click += (s, e) => ShowFolderList();
            btnScan.Click += (s, e) => StartScan();
            btnCancel.Click += (s, e) => { if (worker != null) worker.CancelAsync(); btnCancel.Enabled = false; SetStatus("Cancelling..."); };
            btnDelete.Click += (s, e) => DeleteChecked();
            btnOpen.Click += (s, e) => OpenFile(SelectedFile());
            btnShowFolder.Click += (s, e) => { if (SelectedFile() != null) ShowInFolder(SelectedFile()); else OpenFolder(tree.SelectedNode); };
            txtFilter.TextChanged += (s, e) => { filterTimer.Stop(); filterTimer.Start(); };
            cboSort.SelectedIndexChanged += (s, e) => BuildTree();
            cboView.SelectedIndexChanged += (s, e) => { cboSort.Enabled = cboView.SelectedIndex == 1; BuildTree(); };
            cboSort.Enabled = false;
            tree.AfterSelect += (s, e) => ShowPreview(e.Node);
            tree.NodeMouseClick += Tree_NodeMouseClick;
            tree.NodeMouseDoubleClick += (s, e) =>
            {
                // A double-click on a checkbox is just a click (handled already); elsewhere it opens the file
                if (e.Button != MouseButtons.Left || tree.HitTest(e.Location).Location == TreeViewHitTestLocations.StateImage) return;
                if (e.Node.Tag is FileEntry) OpenFile((FileEntry)e.Node.Tag);
            };
            tree.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { OpenFile(SelectedFile()); e.Handled = true; }
                if (e.KeyCode == Keys.Space && tree.SelectedNode != null)
                {
                    ToggleNode(tree.SelectedNode);
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            lblErrors.Click += (s, e) => ShowTextDialog("Items that could not be read", string.Join(Environment.NewLine, errors));
            DragEnter += Form_DragEnter;
            DragDrop += Form_DragDrop;
            txtFolders.DragEnter += Form_DragEnter;
            txtFolders.DragDrop += Form_DragDrop;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            split.SplitterDistance = (int)(split.Width * (savedSplitter ?? 0.62));
            // From here on, changes are saved
            loadingSettings = false;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // DuplicateFileFinder.exe "D:\Photos" "E:\Backup" starts scanning; a .dupes file is loaded
            var folders = startArgs.Where(Directory.Exists).ToList();
            var results = startArgs.FirstOrDefault(a => a.EndsWith(".dupes", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            if (results != null && Path.GetFileName(results).StartsWith(RestartFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Results carried over by RestartProgram: load them as they were, then
                // remove the temporary file (and keep the "re-compare" choice as it was)
                bool recompare = chkVerify.Checked;
                LoadResults(results);
                chkVerify.Checked = recompare;
                try { File.Delete(results); } catch (Exception) { }
                SetStatus("Duplicate File Finder restarted; your results and checkmarks were kept.");
            }
            else if (results != null) LoadResults(results);
            else if (folders.Count > 0)
            {
                txtFolders.Text = string.Join("; ", folders);
                StartScan();
            }
            CheckVideoSupport();
        }

        // ------------------------------------------- video support on Windows Server

        // On Windows Server the preview's video player needs the optional Windows feature
        // Media Foundation (Server-Media-Foundation). At startup the program checks for
        // it in the background and offers to install it.
        bool videoSupportDeclined;
        ToolStripMenuItem installVideoItem;

        static bool IsWindowsServer()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions"))
                    return key != null && !string.Equals(key.GetValue("ProductType") as string, "WinNT", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // True/false, or null when Windows doesn't list the feature at all
        static bool? MediaFoundationInstalled()
        {
            using (var search = new System.Management.ManagementObjectSearcher(
                "SELECT InstallState FROM Win32_OptionalFeature WHERE Name = 'ServerMediaFoundation'"))
            {
                foreach (System.Management.ManagementObject feature in search.Get())
                    using (feature) return Convert.ToUInt32(feature["InstallState"]) == 1;   // 1 = enabled
            }
            return null;
        }

        void CheckVideoSupport()
        {
            if (videoSupportDeclined || !IsWindowsServer()) return;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                bool? installed = null;
                try { installed = MediaFoundationInstalled(); } catch (Exception) { }
                if (installed != false) return;
                try { BeginInvoke(new Action(OfferVideoSupport)); } catch (InvalidOperationException) { }
            });
        }

        void OfferVideoSupport()
        {
            if (IsDisposed || busy) return;
            var answer = MessageBox.Show(this,
                "Video previews on Windows Server need the Windows feature \"Media Foundation\", which is not installed on this computer. " +
                "Without it, some or all videos can't be played in the preview.\r\n\r\n" +
                "Install it now? Windows will ask for administrator permission, and the computer has to be restarted afterwards.\r\n\r\n" +
                "(If you choose No you won't be asked again; it can still be installed later from Help.)",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) InstallMediaFoundation();
            else
            {
                videoSupportDeclined = true;
                ScheduleSave();
            }
        }

        // Runs Windows' own installer (DISM) with administrator rights: Windows shows its
        // permission prompt. It runs in the background; the program stays usable.
        void InstallMediaFoundation()
        {
            if (!IsWindowsServer()) return;
            installVideoItem.Enabled = false;
            SetStatus("Installing Media Foundation (video playback support)... this can take a few minutes.");
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                int code;
                string error = null;
                try
                {
                    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
                                                     "/Online /Enable-Feature /FeatureName:ServerMediaFoundation /All /NoRestart /Quiet")
                    { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    using (var process = Process.Start(start))
                    {
                        process.WaitForExit();
                        code = process.ExitCode;
                    }
                }
                catch (Win32Exception ex)
                {
                    // 1223: the user said No to Windows' permission prompt
                    code = ex.NativeErrorCode == 1223 ? -1 : -2;
                    error = ex.Message;
                }
                try { BeginInvoke(new Action(() => MediaFoundationInstallFinished(code, error))); } catch (InvalidOperationException) { }
            });
        }

        // Starts a new copy of the program and closes this one. Loaded results (with the
        // checkmarks) are passed on through a temporary .dupes file, deleted once loaded.
        const string RestartFilePrefix = "DF-restart-";

        void RestartProgram()
        {
            if (busy)
            {
                MessageBox.Show(this, "Media Foundation is installed. Restart Duplicate File Finder when the current scan or deletion has finished, to play videos in the preview.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string arguments = "";
            try
            {
                if (groups.Count > 0)
                {
                    string resultsFile = Path.Combine(Path.GetTempPath(), RestartFilePrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dupes");
                    ResultsFile.Save(resultsFile, groups, txtFolders.Text);
                    arguments = "\"" + resultsFile + "\"";
                }
                if (saveTimer != null) saveTimer.Stop();
                SaveSettings();
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, arguments) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Media Foundation is installed, but Duplicate File Finder could not restart itself (" + ex.Message + "). Please restart it.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Close();
        }

        void MediaFoundationInstallFinished(int code, string error)
        {
            installVideoItem.Enabled = true;
            if (code == -1)
            {
                SetStatus("Media Foundation was not installed (administrator permission was not given).");
                return;
            }
            if (code == 0)
            {
                // Installed and ready: the program restarts itself to use it
                SetStatus("Media Foundation is installed. Restarting Duplicate File Finder to use it...");
                RestartProgram();
                return;
            }
            if (code == 3010)   // installed; Windows needs a restart to finish
            {
                SetStatus("Media Foundation is installed; Windows must restart to finish.");
                MessageBox.Show(this, "Media Foundation is installed, but Windows must restart to finish installing it.\r\n\r\n" +
                                      "Video previews will work after you restart the computer.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetStatus("Media Foundation could not be installed.");
            MessageBox.Show(this, "Media Foundation could not be installed (" + (error ?? "DISM error code " + code) + ").\r\n\r\n" +
                                  "Details are in C:\\Windows\\Logs\\DISM\\dism.log. It can also be installed from Server Manager > Add Roles and Features > Features > Media Foundation.",
                            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!busy)
            {
                if (keyData == (Keys.Control | Keys.S)) { SaveResults(); return true; }
                if (keyData == (Keys.Control | Keys.O)) { LoadResults(); return true; }
                if (keyData == Keys.F5) { StartScan(); return true; }
                if (keyData == (Keys.Control | Keys.F)) { txtFilter.Focus(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && MessageBox.Show(this, "A scan or deletion is still running. Stop it and close?", Text,
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            if (worker != null) worker.CancelAsync();
            if (saveTimer != null) saveTimer.Stop();
            SaveSettings();
            CloseRichPreview();
            ClearPreviewFolder();
            foreach (var dir in previewLeftovers) TryDeleteFolder(dir);
            if (mediaFolder != null) TryDeleteFolder(mediaFolder);
            base.OnFormClosing(e);
        }

        // --------------------------------------------------------- folders

        void AddFolder()
        {
            var existing = ParseFolders();
            var picked = FolderPicker.Pick(this, "Choose folders to search for duplicates (Ctrl+click to choose several)",
                                           existing.LastOrDefault(), true);
            if (picked == null) return;
            existing.AddRange(picked);
            txtFolders.Text = string.Join("; ", existing.Distinct(StringComparer.OrdinalIgnoreCase));
            RememberFolders();
        }

        // Adds the folders in the Folders box to the list of known folders
        void RememberFolders()
        {
            int before = knownFolders.Count;
            foreach (var f in ParseFolders())
                if (!knownFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) knownFolders.Add(f);
            if (knownFolders.Count != before) ScheduleSave();
            UpdateFolderCount();
        }

        // "3 folders added, 2 ticked" under the Add folder button
        void UpdateFolderCount()
        {
            if (lblFolderCount == null) return;
            var ticked = ParseFolders();
            int total = knownFolders.Union(ticked, StringComparer.OrdinalIgnoreCase).Count();
            lblFolderCount.Text = total == 0 ? "no folders added"
                                : total == ticked.Count ? string.Format("{0:N0} folder{1} added", total, total == 1 ? "" : "s")
                                : string.Format("{0:N0} folders added, {1:N0} ticked", total, ticked.Count);
        }

        void ShowFolderList()
        {
            RememberFolders();
            var ticked = new HashSet<string>(ParseFolders(), StringComparer.OrdinalIgnoreCase);
            var oldItems = folderList.Items.Cast<ToolStripItem>().ToList();
            folderList.Items.Clear();
            foreach (var item in oldItems) item.Dispose();
            if (knownFolders.Count == 0)
                folderList.Items.Add(new ToolStripMenuItem("(no folders added yet)") { Enabled = false });
            foreach (var folder in knownFolders)
            {
                var item = new ToolStripMenuItem(folder) { CheckOnClick = true, Checked = ticked.Contains(folder),
                                                           ToolTipText = Directory.Exists(folder) ? folder : folder + "  (not found)" };
                item.CheckedChanged += (s, e) => ApplyFolderTicks();
                folderList.Items.Add(item);
            }
            folderList.Items.Add(new ToolStripSeparator());
            folderList.Items.Add("Add folder...", null, (s, e) => { folderList.Close(); AddFolder(); });
            folderList.Items.Add("Clear", null, (s, e) =>
            {
                folderList.Close();
                int checkedCount = CheckedFiles().Count;
                if (checkedCount > 0 &&
                    MessageBox.Show(this, string.Format("Clear also discards the current results and {0:N0} checkmark(s). Continue?\r\n\r\n" +
                                                        "(Use \"Save results\" first to keep them.)", checkedCount),
                                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                knownFolders.Clear();
                txtFolders.Text = "";
                ClearResults();
                UpdateFolderCount();
                ScheduleSave();
            });
            folderList.Show(btnFolderList, new Point(btnFolderList.Width - folderList.PreferredSize.Width, btnFolderList.Height));
        }

        // Back to the starting state: no results, no preview, no list of unreadable items
        void ClearResults()
        {
            groups = new List<DupGroup>();
            errors.Clear();
            ShowErrorsLink();
            BuildTree();   // empties the tree, the preview and the details box, and updates the totals
            SetStatus("Cleared. Choose one or more folders and click Scan.");
        }

        // The Folders box holds the ticked folders, in the list's order
        void ApplyFolderTicks()
        {
            var ticked = folderList.Items.OfType<ToolStripMenuItem>().Where(i => i.CheckOnClick && i.Checked).Select(i => i.Text);
            txtFolders.Text = string.Join("; ", ticked);
        }

        void Form_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void Form_DragDrop(object sender, DragEventArgs e)
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            var dupes = paths.FirstOrDefault(p => p.EndsWith(".dupes", StringComparison.OrdinalIgnoreCase));
            if (dupes != null) { LoadResults(dupes); return; }
            var dirs = paths.Where(Directory.Exists).ToList();
            if (dirs.Count == 0) return;
            var existing = ParseFolders();
            existing.AddRange(dirs);
            txtFolders.Text = string.Join("; ", existing.Distinct(StringComparer.OrdinalIgnoreCase));
            RememberFolders();
        }

        List<string> ParseFolders()
        {
            return txtFolders.Text.Split(';').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ------------------------------------------------------------ scan

        void StartScan()
        {
            if (busy) return;
            RememberFolders();
            var roots = ParseFolders();
            if (roots.Count == 0) { MessageBox.Show(this, "Choose at least one folder to search.", Text); return; }
            var missing = roots.Where(r => !Directory.Exists(r)).ToList();
            if (missing.Count > 0) { MessageBox.Show(this, "Folder not found:\r\n" + string.Join("\r\n", missing), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (CheckedFiles().Count > 0 &&
                MessageBox.Show(this, "Scanning again discards the current results and checkmarks. Continue?\r\n\r\n(Use \"Save results\" first to keep them.)",
                                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            var opt = new ScanOptions
            {
                MinSize = Math.Max(1, (long)numMinKb.Value * 1024),
                IncludeHidden = chkHidden.Checked,
                SameName = chkSameName.Checked,
                IncludeArchives = chkArchives.Checked
            };
            roots = roots.Select(Path.GetFullPath).ToList();
            groups = new List<DupGroup>();
            BuildTree();
            SetBusy(true);
            btnCancel.Enabled = true;
            progress.Style = ProgressBarStyle.Marquee;

            worker = new BackgroundWorker { WorkerReportsProgress = true, WorkerSupportsCancellation = true };
            worker.DoWork += (s, e) =>
            {
                var w = (BackgroundWorker)s;
                var errs = new List<string>();
                var result = Scanner.Scan(roots, opt, p => w.ReportProgress(0, p), () => w.CancellationPending, errs);
                e.Result = new object[] { result, errs };
            };
            worker.ProgressChanged += (s, e) =>
            {
                var p = (ScanProgress)e.UserState;
                if (p.Total > 0)
                {
                    progress.Style = ProgressBarStyle.Continuous;
                    progress.Maximum = p.Total;
                    progress.Value = Math.Min(p.Done, p.Total);
                    SetStatus(string.Format("{0}: {1:N0} of {2:N0} files   {3}", p.Phase, p.Done, p.Total, p.Current));
                }
                else
                {
                    SetStatus(string.Format("{0}: {1:N0} found   {2}", p.Phase, p.Done, p.Current));
                }
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                SetBusy(false);
                if (e.Error != null)
                {
                    MessageBox.Show(this, "The scan failed:\r\n" + e.Error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                var r = (object[])e.Result;
                errors = (List<string>)r[1];
                ShowErrorsLink();
                var result = (List<DupGroup>)r[0];
                if (result == null) { SetStatus("Scan cancelled."); return; }
                groups = result;
                BuildTree();
                SetStatus(string.Format("Scan finished: {0:N0} groups of duplicates, {1:N0} files, {2} could be freed.",
                                        groups.Count, groups.Sum(g => g.Files.Count), FormatSize(groups.Sum(g => g.Wasted))));
            };
            worker.RunWorkerAsync();
        }

        void SetBusy(bool value)
        {
            busy = value;
            btnScan.Enabled = !value;
            btnAddFolder.Enabled = !value;
            btnFolderList.Enabled = !value;
            btnDelete.Enabled = !value;
            tools.Enabled = !value;
            tree.Enabled = !value;
            txtFolders.Enabled = !value;
            if (!value) btnCancel.Enabled = false;
            progress.Visible = value;
            UseWaitCursor = false;
        }

        void ShowErrorsLink()
        {
            lblErrors.Visible = errors.Count > 0;
            lblErrors.Text = string.Format("{0:N0} item(s) could not be read", errors.Count);
        }

        // ------------------------------------------------------------ tree

        void BuildTree()
        {
            string filter = txtFilter.Text.Trim();
            IEnumerable<DupGroup> shown = groups.Where(g => g.Valid.Count > 1);
            if (filter.Length > 0)
                shown = shown.Where(g => g.Files.Any(f => f.FullPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0));
            switch (cboSort.SelectedIndex)
            {
                case 1: shown = shown.OrderByDescending(g => g.Valid.Count).ThenByDescending(g => g.Wasted); break;
                case 2: shown = shown.OrderBy(g => g.Files[0].Name, StringComparer.CurrentCultureIgnoreCase); break;
                case 3: shown = shown.OrderBy(g => Path.GetDirectoryName(g.Files[0].FullPath), StringComparer.CurrentCultureIgnoreCase); break;
                default: shown = shown.OrderByDescending(g => g.Wasted); break;
            }
            shownGroups = shown.ToList();

            foreach (var g in groups)
            {
                g.Node = null;
                foreach (var f in g.Files) f.Node = null;
            }
            var nodes = cboView.SelectedIndex == 1 ? BuildGroupNodes() : BuildFolderNodes();

            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                tree.Nodes.AddRange(nodes.ToArray());
                tree.ExpandAll();
            }
            finally
            {
                tree.EndUpdate();
            }
            // After EndUpdate: inside BeginUpdate the scroll position isn't applied
            if (nodes.Count > 0) tree.TopNode = nodes[0];
            RefreshStates();
            ShowPreview(null);
        }

        List<TreeNode> BuildGroupNodes()
        {
            var nodes = new List<TreeNode>();
            foreach (var g in shownGroups)
            {
                var gn = new TreeNode(GroupText(g)) { Tag = g };
                foreach (var f in g.Files)
                    if (!f.Deleted) gn.Nodes.Add(MakeFileNode(f, FileText(f)));
                g.Node = gn;
                nodes.Add(gn);
            }
            return nodes;
        }

        TreeNode MakeFileNode(FileEntry f, string text)
        {
            var others = f.Group.Files.Where(x => x != f && !x.Deleted).Select(x => x.FullPath).ToList();
            string tip = f.FullPath + "\r\n\r\nOther copies:\r\n  " + string.Join("\r\n  ", others.Take(10)) +
                         (others.Count > 10 ? "\r\n  ..." : "");
            // No NodeFont: the tree draws files in the regular font itself (see Tree_DrawNode)
            var node = new TreeNode(text) { Tag = f, ToolTipText = tip };
            f.Node = node;
            return node;
        }

        // A folder of the folder view, before it becomes a tree node
        class DirEntry
        {
            public string Name, FullPath;
            public SortedDictionary<string, DirEntry> Subs = new SortedDictionary<string, DirEntry>(StringComparer.CurrentCultureIgnoreCase);
            public List<FileEntry> Files = new List<FileEntry>();
        }

        // Drives, then the folders leading to every duplicate file, then the files
        HashSet<string> archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<TreeNode> BuildFolderNodes()
        {
            // Files inside an archive appear under it, as if the archive were a folder
            archivePaths = new HashSet<string>(shownGroups.SelectMany(g => g.Files).Where(x => x.InArchive).Select(x => x.Archive),
                                               StringComparer.OrdinalIgnoreCase);
            var roots = new SortedDictionary<string, DirEntry>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var f in shownGroups.SelectMany(g => g.Files).Where(x => !x.Deleted))
            {
                string dir = Path.GetDirectoryName(f.FullPath);
                string root = Path.GetPathRoot(dir);    // "D:\" or "\\server\share"
                DirEntry d;
                if (!roots.TryGetValue(root, out d))
                {
                    d = new DirEntry { Name = root, FullPath = root };
                    roots.Add(root, d);
                }
                foreach (string part in dir.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    DirEntry sub;
                    if (!d.Subs.TryGetValue(part, out sub))
                    {
                        sub = new DirEntry { Name = part, FullPath = Path.Combine(d.FullPath, part) };
                        d.Subs.Add(part, sub);
                    }
                    d = sub;
                }
                d.Files.Add(f);
            }
            return roots.Values.Select(MakeFolderNode).ToList();
        }

        TreeNode MakeFolderNode(DirEntry d)
        {
            // A chain of folders that only lead to one subfolder becomes one line: D:\Sync\Photos
            // (An archive ends a chain, so it keeps a line of its own)
            string name = d.Name;
            while (d.Files.Count == 0 && d.Subs.Count == 1 && !archivePaths.Contains(d.FullPath))
            {
                d = d.Subs.Values.First();
                name = name.EndsWith("\\") ? name + d.Name : name + "\\" + d.Name;
            }
            var tag = new FolderTag { FullPath = d.FullPath, Name = name, IsArchive = archivePaths.Contains(d.FullPath) };
            var node = new TreeNode(name) { Tag = tag, ToolTipText = d.FullPath };
            foreach (var sub in d.Subs.Values) node.Nodes.Add(MakeFolderNode(sub));
            foreach (var f in d.Files.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                node.Nodes.Add(MakeFileNode(f, FolderFileText(f)));
            return node;
        }

        static string FolderFileText(FileEntry f)
        {
            int copies = f.Group.Files.Count(x => !x.Deleted && !x.Stale);
            string text = string.Format("{0}     {1}     {2:yyyy-MM-dd HH:mm}     {3} copies",
                                        f.Name, FormatSize(f.Size), f.Modified.ToLocalTime(), copies);
            if (f.InArchive) text += "     [inside archive]";
            if (f.Stale) text = "(" + f.StaleReason + " - ignored)   " + text;
            return text;
        }

        static string GroupText(DupGroup g)
        {
            // The most common name among the copies (shortest on a tie)
            string name = g.Files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                                 .OrderByDescending(n => n.Count()).ThenBy(n => n.Key.Length).First().Key;
            bool sameNames = g.Files.All(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            return string.Format("{0}{1}   \u2014   {2} copies \u00D7 {3}   ({4} could be freed)",
                                 name, sameNames ? "" : "  (and other names)", g.Valid.Count, FormatSize(g.Size), FormatSize(g.Wasted));
        }

        // Name first, so it stays visible however long the folder path is
        static string FileText(FileEntry f)
        {
            string text = f.Name + "     " + f.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                          "     in  " + Path.GetDirectoryName(f.FullPath);
            if (f.InArchive) text += "     [inside archive]";
            if (f.Stale) text = "(" + f.StaleReason + " - ignored)   " + text;
            return text;
        }

        // Red: will be deleted. Green: the copy that will be kept, because every other
        // copy that could be kept is checked (and something of this file is checked at
        // all: a lone ordinary copy isn't green while nothing is being deleted).
        // Blue: inside an archive.
        static void StyleNode(FileEntry f)
        {
            if (f.Node == null) return;
            Color color;
            var mustKeep = Selection.MustKeepOneOf(f.Group);
            if (f.Stale) color = SystemColors.GrayText;
            else if (f.Checked) color = Color.Firebrick;
            else if (mustKeep.Contains(f) && mustKeep.Where(x => x != f).All(x => x.Checked) &&
                     f.Group.Valid.Any(x => x != f && x.Checked)) color = Color.ForestGreen;
            else if (f.InArchive) color = Color.SteelBlue;
            else color = SystemColors.WindowText;
            if (f.Node.ForeColor != color) f.Node.ForeColor = color;
        }

        // Draws a line's text: folders and groups in bold (the tree's font), files in the
        // regular font, both created once. Colors come from the node (see StyleNode).
        void Tree_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null || e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
            var font = e.Node.Tag is FileEntry ? fileFont : groupFont;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            var size = TextRenderer.MeasureText(e.Graphics, e.Node.Text, font, new Size(int.MaxValue, e.Bounds.Height), flags);
            var area = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(e.Bounds.Width, size.Width + 4), e.Bounds.Height);
            Color color = e.Node.ForeColor.IsEmpty ? tree.ForeColor : e.Node.ForeColor;
            if (selected)
            {
                // Like the standard tree: blue when it has focus, grey otherwise
                e.Graphics.FillRectangle(tree.Focused ? SystemBrushes.Highlight : SystemBrushes.Control, area);
                if (tree.Focused) color = SystemColors.HighlightText;
            }
            TextRenderer.DrawText(e.Graphics, e.Node.Text, font, new Rectangle(area.X + 2, area.Y, area.Width - 2, area.Height), color, flags);
        }

        void Tree_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Left && tree.HitTest(e.Location).Location == TreeViewHitTestLocations.StateImage)
            {
                ToggleNode(e.Node);
                return;
            }
            if (e.Button != MouseButtons.Right) return;
            tree.SelectedNode = e.Node;
            var menu = e.Node.Tag is FileEntry ? fileMenu : e.Node.Tag is FolderTag ? folderMenu : groupMenu;
            menu.Show(tree, e.Location);
        }

        static List<FileEntry> FilesUnder(TreeNode node)
        {
            var list = new List<FileEntry>();
            if (node == null) return list;
            var stack = new Stack<TreeNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                var f = n.Tag as FileEntry;
                if (f != null && !f.Stale && !f.Deleted) list.Add(f);
                foreach (TreeNode child in n.Nodes) stack.Push(child);
            }
            return list;
        }

        // Click on a checkbox. A file toggles. A folder or group gets everything below
        // checked; if it already is (as far as allowed), everything is unchecked.
        void ToggleNode(TreeNode node)
        {
            if (busy || node == null) return;
            var f = node.Tag as FileEntry;
            if (f != null)
            {
                ToggleFile(f);
                return;
            }
            var files = FilesUnder(node);
            int before = files.Count(x => x.Checked);
            if (before == files.Count) { SetNodeChecked(node, false); return; }
            int kept = Selection.SetChecked(files, true);
            if (files.Count(x => x.Checked) == before)
            {
                // Nothing more could be checked: treat the click as "uncheck"
                SetNodeChecked(node, false);
                return;
            }
            RefreshStates();
            ReportChecked(node, files.Count(x => x.Checked), kept);
        }

        // Checks or unchecks one file (checkbox, Space key or menu)
        void ToggleFile(FileEntry f)
        {
            if (busy) return;
            if (f.Checked) f.Checked = false;
            else if (Selection.CanCheck(f)) f.Checked = true;
            else
            {
                System.Media.SystemSounds.Beep.Play();
                SetStatus(f.Stale ? "This file changed since the results were saved, so it can't be selected."
                          : "This is the last unchecked copy of this file: it must stay. Right-click > \"Delete this copy too\" to delete it anyway.");
                return;
            }
            RefreshStates();
        }

        void SetNodeChecked(TreeNode node, bool check)
        {
            if (busy || node == null) return;
            var files = FilesUnder(node);
            int kept = Selection.SetChecked(files, check);
            RefreshStates();
            if (check) ReportChecked(node, files.Count(x => x.Checked), kept);
            else SetStatus(string.Format("Unchecked {0:N0} file(s) in {1}.", files.Count, NodeName(node)));
        }

        void ReportChecked(TreeNode node, int count, int kept)
        {
            string msg = string.Format("Checked {0:N0} file(s) in {1}.", count, NodeName(node));
            if (kept > 0)
                msg += string.Format(" {0:N0} file(s) had all their copies here, so one copy of each was left unchecked (shown in green).", kept);
            SetStatus(msg);
        }

        static string NodeName(TreeNode node)
        {
            var folder = node.Tag as FolderTag;
            return folder != null ? folder.FullPath : "this group";
        }

        // Updates checkboxes, colors and folder counts after checkmarks changed
        void RefreshStates()
        {
            // "Delete every copy" lasts only while every copy that would be kept is checked
            foreach (var g in groups)
                if (g.DeleteAll && Selection.MustKeepOneOf(g).Any(x => !x.Checked)) g.DeleteAll = false;
            tree.BeginUpdate();
            try
            {
                foreach (TreeNode n in tree.Nodes) UpdateNode(n);
            }
            finally
            {
                tree.EndUpdate();
            }
            UpdateTotals();
        }

        // Returns how many usable files are below node, and how many of them are checked
        int[] UpdateNode(TreeNode node)
        {
            // Counts: [0] files that can be checked, [1] checked
            var f = node.Tag as FileEntry;
            if (f != null)
            {
                DupTree.SetState(node, f.Stale ? DupTree.Disabled : f.Checked ? DupTree.Checked : DupTree.Unchecked);
                StyleNode(f);
                return new[] { f.Stale ? 0 : 1, !f.Stale && f.Checked ? 1 : 0 };
            }
            int total = 0, chk = 0;
            foreach (TreeNode child in node.Nodes)
            {
                var r = UpdateNode(child);
                total += r[0];
                chk += r[1];
            }
            DupTree.SetState(node, total == 0 ? DupTree.Disabled
                                 : chk == 0 ? DupTree.Unchecked
                                 : chk == total ? DupTree.Checked : DupTree.Mixed);
            var folder = node.Tag as FolderTag;
            if (folder != null)
            {
                string text = string.Format(folder.IsArchive ? "{0}     [archive: {1:N0} duplicate{2} inside{3}]" : "{0}     ({1:N0} duplicate{2}{3})",
                                            folder.Name, total, total == 1 ? "" : "s",
                                            chk > 0 ? string.Format(", {0:N0} checked", chk) : "");
                if (node.Text != text) node.Text = text;
            }
            return new[] { total, chk };
        }

        // The right-click menu of a file. In the "Copies" submenu, every other copy gets
        // these same items for itself (top = false), with "Jump to this copy" in place
        // of the list of copies.
        ToolStripItem[] FileMenuItems(FileEntry f, bool top)
        {
            var items = new List<ToolStripItem>();
            var open = new ToolStripMenuItem("Open", null, (s, e) => OpenFile(f));
            if (top)
            {
                if (menuBoldFont == null) menuBoldFont = new Font(fileMenu.Font, FontStyle.Bold);
                open.Font = menuBoldFont;
            }
            items.Add(open);
            items.Add(new ToolStripMenuItem("Show in folder", null, (s, e) => ShowInFolder(f)));
            items.Add(new ToolStripMenuItem("Copy path", null, (s, e) => Clipboard.SetText(f.FullPath)));
            if (top)
            {
                var copies = new ToolStripMenuItem("Copies");
                foreach (var c in f.Group.Files.Where(x => x != f && !x.Deleted))
                {
                    string mark = c.Stale ? "[changed]" : c.Checked ? "[delete]" : "[keep]";
                    var item = new ToolStripMenuItem(mark + "   " + c.FullPath + (c.InArchive ? "   (inside archive)" : ""));
                    if (c.Node == null)
                    {
                        // Not in the tree right now: its group is hidden by the filter
                        item.Text += "   (hidden by the filter)";
                        item.Enabled = false;
                    }
                    else
                    {
                        item.DropDownItems.AddRange(FileMenuItems(c, false));
                    }
                    copies.DropDownItems.Add(item);
                }
                copies.Enabled = copies.DropDownItems.Count > 0;
                items.Add(copies);
            }
            else
            {
                items.Add(new ToolStripMenuItem("Jump to this copy", null, (s, e) => JumpTo(f)) { Enabled = f.Node != null });
            }
            items.Add(new ToolStripSeparator());
            if (!f.Stale)
            {
                if (f.Checked)
                    items.Add(new ToolStripMenuItem("Uncheck (keep this copy)", null, (s, e) => ToggleFile(f)));
                else if (Selection.CanCheck(f))
                    items.Add(new ToolStripMenuItem("Check for deletion", null, (s, e) => ToggleFile(f)));
                else
                    // The copy that is kept: only here, never through its checkbox
                    items.Add(new ToolStripMenuItem("Delete this copy too...", null, (s, e) => DeleteKeptCopy(f)) { ForeColor = Color.Firebrick });
            }
            items.Add(new ToolStripMenuItem("Keep only this copy (check the others)", null, (s, e) => KeepOnly(f)));
            items.Add(new ToolStripMenuItem("Check all copies in this folder and its subfolders", null,
                                            (s, e) => CheckFolder(Path.GetDirectoryName(f.FullPath), true)));
            items.Add(new ToolStripMenuItem("Uncheck all copies in this folder and its subfolders", null,
                                            (s, e) => CheckFolder(Path.GetDirectoryName(f.FullPath), false)));
            return items.ToArray();
        }

        // Checks the copy that would be kept, after confirmation: then no copy of the
        // file is left (or only copies inside archives, when the file has some)
        void DeleteKeptCopy(FileEntry f)
        {
            if (busy) return;
            var remaining = f.Group.Valid.Where(x => x != f && !x.Checked).ToList();
            string outcome = remaining.Count == 0
                ? "Then NO copy of this file will be left after deleting."
                : "Then only the copies inside archives will be left:\r\n" + string.Join("\r\n", remaining.Select(x => x.FullPath));
            if (MessageBox.Show(this, "This is the copy that would be kept:\r\n" + f.FullPath + "\r\n\r\n" +
                                      "Check it for deletion too?\r\n" + outcome + "\r\n\r\n" +
                                      "(Unchecking any copy of this file later brings the protection back.)",
                                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            f.Group.DeleteAll = true;
            f.Checked = true;
            RefreshStates();
            SetStatus(remaining.Count == 0 ? "Every copy of " + f.Name + " is checked: none will be kept."
                                           : "Every ordinary copy of " + f.Name + " is checked: only copies inside archives will be kept.");
        }

        void JumpTo(FileEntry f)
        {
            if (f.Node == null) return;
            f.Node.EnsureVisible();
            tree.SelectedNode = f.Node;
            tree.Focus();
        }

        void OpenFolder(TreeNode node)
        {
            var folder = node == null ? null : node.Tag as FolderTag;
            if (folder == null) return;
            try { Process.Start("explorer.exe", "\"" + folder.FullPath + "\""); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        }

        List<FileEntry> CheckedFiles()
        {
            return groups.SelectMany(g => g.Files).Where(f => f.Checked && !f.Stale && !f.Deleted).ToList();
        }

        void UpdateTotals()
        {
            var chk = CheckedFiles();
            long bytes = chk.Sum(f => f.Size);
            lblChecked.Text = chk.Count == 0
                ? string.Format("{0:N0} groups of duplicates   \u2022   {1} could be freed   \u2022   nothing checked yet",
                                groups.Count(g => g.Valid.Count > 1), FormatSize(groups.Sum(g => g.Wasted)))
                : string.Format("Checked: {0:N0} file(s), {1} will be freed", chk.Count, FormatSize(bytes));
            btnDelete.Enabled = !busy && chk.Count > 0;
        }

        FileEntry SelectedFile()
        {
            return tree.SelectedNode == null ? null : tree.SelectedNode.Tag as FileEntry;
        }

        // ------------------------------------------------------- selection

        void ApplyKeepRule(KeepRule rule)
        {
            Selection.KeepOne(shownGroups, rule);
            RefreshStates();
            SetStatus("Auto-select applied to the " + shownGroups.Count().ToString("N0") + " group(s) shown.");
        }

        void CheckArchiveCopies()
        {
            int count = Selection.CheckArchiveCopies(shownGroups);
            RefreshStates();
            SetStatus(count == 0 ? "No copies inside archives to check (in the groups shown, every such file needs an ordinary copy elsewhere)."
                                 : string.Format("Checked {0:N0} copies inside archives. They will be removed from their archives; an archive left empty is deleted.", count));
        }

        void ApplyKeepRuleInFolder(TreeNode node, KeepRule rule)
        {
            if (busy || node == null) return;
            int count = Selection.KeepOneAmong(FilesUnder(node), rule);
            RefreshStates();
            SetStatus(count == 0
                ? "No file has more than one copy inside " + NodeName(node) + ", so nothing was changed."
                : string.Format("Auto-select applied inside {0}: {1:N0} file(s) with several copies there now keep one copy each.", NodeName(node), count));
        }

        void PickFolderAndCheck(bool check)
        {
            var picked = FolderPicker.Pick(this, check ? "Check every copy inside this folder (and its subfolders)"
                                                       : "Uncheck every copy inside this folder (and its subfolders)",
                                           ParseFolders().FirstOrDefault(), false);
            if (picked != null && picked.Count > 0) CheckFolder(picked[0], check);
        }

        void CheckFolder(string folder, bool check)
        {
            int kept = Selection.CheckInFolder(shownGroups, folder, check);
            RefreshStates();
            string msg = (check ? "Checked" : "Unchecked") + " all copies inside " + folder + ".";
            if (kept > 0) msg += string.Format(" In {0:N0} group(s) every copy is in that folder, so one copy was left unchecked.", kept);
            SetStatus(msg);
        }

        void KeepOnly(FileEntry f)
        {
            if (busy || f == null || f.Stale) return;
            foreach (var x in f.Group.Valid) x.Checked = x != f;
            RefreshStates();
        }

        void KeepFirstInSelectedGroup()
        {
            var g = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as DupGroup;
            if (g == null) return;
            var keep = Selection.MustKeepOneOf(g)[0];
            foreach (var f in g.Valid) f.Checked = f != keep;
            RefreshStates();
        }

        void UncheckSelectedGroup()
        {
            var g = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as DupGroup;
            if (g == null) return;
            foreach (var f in g.Files) f.Checked = false;
            RefreshStates();
        }

        void UncheckAll()
        {
            foreach (var g in groups) foreach (var f in g.Files) f.Checked = false;
            RefreshStates();
        }

        // --------------------------------------------------------- preview

        void ShowPreview(TreeNode node)
        {
            // Release the previous file first (a handler or the player may hold it open)
            CloseRichPreview();
            ClearPreviewFolder();
            picture.Image = null;
            if (previewImage != null) { previewImage.Dispose(); previewImage = null; }
            if (previewStream != null) { previewStream.Dispose(); previewStream = null; }
            picture.Visible = false;
            txtPreview.Visible = false;
            lblNoPreview.Visible = true;
            txtPreview.Text = "";

            var f = node == null ? null : node.Tag as FileEntry;
            var folder = node == null ? null : node.Tag as FolderTag;
            btnOpen.Enabled = f != null;
            btnShowFolder.Enabled = f != null || folder != null;
            if (f == null)
            {
                var g = node == null ? null : node.Tag as DupGroup;
                if (g != null)
                    txtInfo.Text = string.Format("{0} identical copies of {1:N0} bytes each.\r\nSHA-256: {2}", g.Valid.Count, g.Size, g.Hash);
                else if (folder != null)
                {
                    var below = FilesUnder(node);
                    txtInfo.Text = string.Format("{0}\r\n{1:N0} duplicate file(s) in this folder and its subfolders ({2}), {3:N0} checked.",
                                                 folder.FullPath, below.Count, FormatSize(below.Sum(x => x.Size)), below.Count(x => x.Checked));
                }
                else txtInfo.Text = "";
                lblNoPreview.Text = "Click a file to preview it.";
                return;
            }

            var info = new StringBuilder();
            info.AppendFormat("{0}\r\n{1} ({2:N0} bytes)   \u2022   modified {3:yyyy-MM-dd HH:mm:ss}",
                              f.FullPath, FormatSize(f.Size), f.Size, f.Modified.ToLocalTime());
            if (f.Stale) info.Append("\r\nChanged since the results were saved: " + f.StaleReason);
            var copies = f.Group.Files.Where(x => !x.Deleted).ToList();
            info.AppendFormat("\r\n\r\nAll {0} copies:", copies.Count);
            foreach (var c in copies)
            {
                string mark = c.Stale ? "[changed] " : c.Checked ? "[delete]  " : "[keep]    ";
                info.Append("\r\n" + mark + c.FullPath + (c.InArchive ? "   (inside archive)" : "") + (c == f ? "   <- this one" : ""));
            }
            txtInfo.Text = info.ToString();
            if (f.InArchive)
            {
                PreviewFromArchive(f);
                return;
            }
            ShowFileContent(f.FullPath, f.Size);
        }

        // ------------------------------------------- previewing inside archives

        int previewGeneration;                  // changes with every preview, so late extractions are ignored
        string previewFolder;                   // temp folder of the current archive preview
        string previewExtracted;                // the extracted copy of the selected file inside an archive
        readonly List<string> previewLeftovers = new List<string>();   // folders not deletable yet (file open elsewhere)

        void PreviewFromArchive(FileEntry f)
        {
            if (!File.Exists(f.Archive))
            {
                lblNoPreview.Text = "The archive no longer exists:\r\n" + f.Archive;
                return;
            }
            lblNoPreview.Text = "Extracting from the archive for preview...";
            int generation = previewGeneration;
            string dest = Path.Combine(Path.GetTempPath(), "DF-preview-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            previewFolder = dest;
            string archive = f.Archive, inner = f.FullPath.Substring(f.Archive.Length + 1);
            long size = f.Size;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                string error = null, path = null;
                try { path = ArchiveReader.ExtractOne(archive, inner, dest, out error); }
                catch (Exception ex) { error = ex.Message; }
                // Back to the window's own thread to show the result
                try { BeginInvoke(new Action(() => PreviewExtracted(generation, dest, path, error, size))); }
                catch (InvalidOperationException) { TryDeleteFolder(dest); }   // the window was closed meanwhile
            });
        }

        void PreviewExtracted(int generation, string dest, string path, string error, long size)
        {
            // Another file was selected meanwhile: this extraction is no longer wanted
            if (IsDisposed || generation != previewGeneration)
            {
                if (!TryDeleteFolder(dest)) previewLeftovers.Add(dest);
                return;
            }
            if (path == null)
            {
                lblNoPreview.Text = "Could not extract this file for preview:\r\n" + error;
                return;
            }
            // Read-only: it's a temporary copy, edits to it would be lost
            try { File.SetAttributes(path, FileAttributes.ReadOnly); } catch (Exception) { }
            previewExtracted = path;
            btnOpen.Enabled = true;
            txtInfo.AppendText("\r\n\r\nPreview and \"Open file\" use a temporary read-only copy extracted from the archive " +
                               "(deleted when another file is previewed or the app closes).");
            ShowFileContent(path, size);
        }

        // Called whenever the preview changes, and when the app closes
        void ClearPreviewFolder()
        {
            previewGeneration++;
            previewExtracted = null;
            if (previewFolder != null && !TryDeleteFolder(previewFolder)) previewLeftovers.Add(previewFolder);
            previewFolder = null;
        }

        static bool TryDeleteFolder(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return true;
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(dir, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // At startup: preview folders left by a previous session (e.g. after a crash)
        static void DeleteOldPreviewFolders()
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "DF-preview-*")) TryDeleteFolder(dir);
            }
            catch (Exception) { }
        }

        // Shows a picture or the start of a text file in the preview pane
        void ShowFileContent(string path, long size)
        {
            string ext = Path.GetExtension(path);
            picture.Visible = txtPreview.Visible = false;
            lblNoPreview.Visible = true;
            try
            {
                if (ImageExtensions.Contains(ext) && size < 200L * 1024 * 1024 && ShowPicture(path)) { }
                else if (TextExtensions.Contains(ext))
                {
                    const int max = 64 * 1024;
                    byte[] bytes;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        bytes = new byte[(int)Math.Min(max, fs.Length)];
                        int read = 0, n;
                        while (read < bytes.Length && (n = fs.Read(bytes, read, bytes.Length - read)) > 0) read += n;
                    }
                    string text;
                    using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.Default, true))
                        text = reader.ReadToEnd();
                    // The text box only breaks lines at CR+LF; files from Linux/Mac use LF or CR alone
                    text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
                    txtPreview.Text = text + (size > max ? "\r\n\r\n[... only the first 64 KB are shown ...]" : "");
                    txtPreview.Visible = true;
                    picture.Visible = false;
                    lblNoPreview.Visible = false;
                }
                else if (VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext))
                {
                    ShowMedia(path, VideoExtensions.Contains(ext));
                }
                else if (!ShowWithPreviewHandler(path) && !ShowThumbnail(path))
                {
                    // The icon sits in the middle; the blank lines move the text below it
                    lblNoPreview.Image = FileIcon(path);
                    lblNoPreview.ImageAlign = ContentAlignment.MiddleCenter;
                    lblNoPreview.Text = ImageExtensions.Contains(ext)
                        ? "\r\n\r\n\r\n\r\n\r\nThis picture could not be read: the file may be damaged or incomplete.\r\nDouble-click the file (or press Enter) to try opening it."
                        : "\r\n\r\n\r\n\r\n\r\nNo preview for this file type.\r\nDouble-click the file (or press Enter) to open it.";
                }
            }
            catch (Exception ex)
            {
                CloseRichPreview();
                lblNoPreview.Text = "Cannot preview this file:\r\n" + ex.Message;
                picture.Visible = txtPreview.Visible = false;
                lblNoPreview.Visible = true;
            }
        }

        // A picture GDI+ can read; false (nothing shown) if it can't, e.g. a damaged file
        bool ShowPicture(string path)
        {
            try
            {
                // Loaded from memory, so the file isn't kept open (it may be deleted later)
                previewStream = new MemoryStream(File.ReadAllBytes(path));
                previewImage = Image.FromStream(previewStream);
            }
            catch (Exception)
            {
                if (previewStream != null) { previewStream.Dispose(); previewStream = null; }
                return false;
            }
            picture.Image = previewImage;
            picture.Visible = true;
            txtPreview.Visible = false;
            lblNoPreview.Visible = false;
            return true;
        }

        // ------------------------------------------- previews of other file types

        // The preview handler Windows has for this type (as in Explorer's preview pane)
        bool ShowWithPreviewHandler(string path)
        {
            if (ShellPreview.HandlerFor(path) == null) return false;
            lblNoPreview.Visible = false;
            handlerPanel.Visible = true;
            handlerPanel.BringToFront();
            handlerPanel.Refresh();
            string error;
            previewHandler = ShellPreview.Open(path, handlerPanel, out previewHandlerResource, out error);
            if (previewHandler != null) return true;
            handlerPanel.Visible = false;
            lblNoPreview.Visible = true;
            return false;
        }

        // Explorer's thumbnail (a frame of a video, a picture GDI+ can't read...)
        bool ShowThumbnail(string path)
        {
            var thumbnail = ShellPreview.Thumbnail(path, 1024);
            if (thumbnail == null) return false;
            previewImage = thumbnail;
            picture.Image = previewImage;
            picture.Visible = true;
            txtPreview.Visible = false;
            lblNoPreview.Visible = false;
            return true;
        }

        static Image FileIcon(string path)
        {
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(path)) return icon.ToBitmap();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Videos and songs play in the built-in browser engine. The page asks for its
        // modern mode (X-UA-Compatible), which has the HTML5 video and audio player.
        // Videos start playing muted; songs wait for Play.
        void ShowMedia(string path, bool video)
        {
            if (mediaView == null)
            {
                mediaView = new WebBrowser
                {
                    Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, IsWebBrowserContextMenuEnabled = false,
                    AllowWebBrowserDrop = false, WebBrowserShortcutsEnabled = false
                };
                previewHost.Controls.Add(mediaView);
            }
            if (mediaFolder == null)
            {
                mediaFolder = Path.Combine(Path.GetTempPath(), "DF-preview-media-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(mediaFolder);
            }
            string source = HtmlAttribute(BrowserFileUrl(path));
            string player = video
                ? "<video src=\"" + source + "\" controls autoplay muted style=\"width:100%;height:100%\"></video>"
                : "<div style=\"padding-top:30%;text-align:center\"><audio src=\"" + source + "\" controls style=\"width:90%\"></audio></div>";
            // If the file can't be played, say why in plain words instead of the player's own message
            string onError =
                "<div id=\"err\" style=\"display:none;padding:30px;font:14px Segoe UI,sans-serif;text-align:center;color:" + (video ? "#ddd" : "#444") + "\"></div>" +
                "<script>var m = document.getElementsByTagName('" + (video ? "video" : "audio") + "')[0];" +
                "m.addEventListener('error', function () {" +
                "  var code = m.error ? m.error.code : 0;" +
                "  var why = {1: 'playback was interrupted', 2: 'the file could not be read', " +
                "             3: 'it could not be decoded (Windows may be missing the codec it needs)', " +
                "             4: 'this format, or this file, is not supported by the built-in player'}[code] || 'unknown error';" +
                "  m.style.display = 'none'; var e = document.getElementById('err'); e.style.display = 'block';" +
                "  e.innerHTML = 'This file can\\'t be played in the preview: ' + why + ' (code ' + code + ').<br><br>' +" +
                "                'Double-click the file to open it in your video player.'; });</script>";
            string page = Path.Combine(mediaFolder, "player.html");
            File.WriteAllText(page,
                "<!DOCTYPE html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta charset=\"utf-8\">" +
                "<style>html,body{margin:0;height:100%;overflow:hidden;background:" + (video ? "#000" : "#fff") + "}</style></head><body>" +
                player + onError + "</body></html>", new UTF8Encoding(true));
            mediaView.Navigate(page);
            mediaView.Visible = true;
            mediaView.BringToFront();
            picture.Visible = txtPreview.Visible = lblNoPreview.Visible = false;
        }

        // A file:// address the built-in browser engine understands. Accented letters
        // are left as they are (the page is UTF-8): percent-encoded, the engine would
        // decode them with the Windows code page and look for a folder that doesn't
        // exist (an accented "i" turns into two odd characters). Only characters with a meaning in
        // addresses are encoded.
        static string BrowserFileUrl(string path)
        {
            var url = new StringBuilder(path.StartsWith(@"\\") ? "file:" : "file:///");
            foreach (char c in path.Replace('\\', '/'))
            {
                if (c == '%' || c == '#' || c == '?' || c == ' ') url.Append('%').Append(((int)c).ToString("X2"));
                else url.Append(c);
            }
            return url.ToString();
        }

        static string HtmlAttribute(string value)
        {
            return value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // Stops the player and unloads the preview handler, so neither holds the file
        void CloseRichPreview()
        {
            if (previewHandler != null)
            {
                ShellPreview.Close(previewHandler, previewHandler, previewHandlerResource);
                previewHandler = null;
                previewHandlerResource = null;
            }
            if (handlerPanel != null) handlerPanel.Visible = false;
            if (mediaView != null && mediaView.Visible)
            {
                mediaView.Navigate("about:blank");
                mediaView.Visible = false;
            }
            if (lblNoPreview != null)
            {
                var icon = lblNoPreview.Image;
                lblNoPreview.Image = null;
                if (icon != null) icon.Dispose();
            }
        }

        // A file inside an archive opens its extracted preview copy once it is ready,
        // otherwise the archive itself
        void OpenFile(FileEntry f)
        {
            if (f == null) return;
            string path = !f.InArchive ? f.FullPath
                        : f == SelectedFile() && previewExtracted != null && File.Exists(previewExtracted) ? previewExtracted
                        : f.Archive;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "Cannot open the file:\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void ShowInFolder(FileEntry f)
        {
            if (f == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + (f.InArchive ? f.Archive : f.FullPath) + "\""); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        }

        // ---------------------------------------------------------- delete

        void DeleteChecked()
        {
            var toDelete = CheckedFiles();
            if (toDelete.Count == 0 || busy) return;
            bool verify = chkVerify.Checked;
            bool permanent = chkPermanent.Checked;
            var inArchives = toDelete.Where(f => f.InArchive).ToList();
            int archiveCount = inArchives.Select(f => f.Archive).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            string question = string.Format(
                (permanent ? "PERMANENTLY delete {0:N0} file(s) ({1})?\r\nThey will NOT go to the Recycle Bin and can't be restored.\r\n\r\n"
                           : "Send {0:N0} file(s) ({1}) to the Recycle Bin?\r\n\r\n") +
                "Right before deleting, each file is checked again: it is skipped if it changed or if no unchecked copy of it is found on disk.{2}{3}",
                toDelete.Count, FormatSize(toDelete.Sum(f => f.Size)),
                verify ? "\r\nContents are re-compared too (slower)." : "",
                inArchives.Count == 0 ? "" : string.Format(
                    "\r\n\r\n{0:N0} of them are inside {1:N0} archive(s). They are removed from those archives ({2}), and an archive left with no files is deleted.",
                    inArchives.Count, archiveCount,
                    permanent ? "the previous version of each archive is deleted permanently" : "the previous version of each archive goes to the Recycle Bin"));
            // Files whose kept copy was also checked through "Delete this copy too"
            var noneKept = groups.Where(g => g.DeleteAll && g.Valid.Any(f => f.Checked)).ToList();
            if (noneKept.Count > 0)
                question += string.Format("\r\n\r\nWARNING: for {0:N0} file(s) you chose to delete the copy that would be kept, so no copy of them will be left:\r\n{1}{2}",
                                          noneKept.Count, string.Join("\r\n", noneKept.Take(10).Select(g => "   " + g.Files[0].Name)),
                                          noneKept.Count > 10 ? "\r\n   ..." : "");
            // For permanent deletion, or when nothing is kept of some files, "No" is the
            // default button, so a stray Enter does nothing
            bool risky = permanent || noneKept.Count > 0;
            if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo,
                                risky ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                                risky ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;

            SetBusy(true);
            btnCancel.Enabled = true;
            progress.Style = ProgressBarStyle.Continuous;
            progress.Maximum = toDelete.Count;
            progress.Value = 0;
            var log = new List<string>();
            var skipped = new List<string>();
            int deleted = 0;
            long freed = 0;

            worker = new BackgroundWorker { WorkerReportsProgress = true, WorkerSupportsCancellation = true };
            Func<string, string> deleteFile = path => permanent ? DeletePermanently(path) : RecycleBin.Delete(path);
            worker.DoWork += (s, e) =>
            {
                var w = (BackgroundWorker)s;
                var keepers = new Dictionary<DupGroup, FileEntry>();
                int step = 0;

                // 1. Ordinary files
                foreach (var f in toDelete.Where(x => !x.InArchive))
                {
                    if (w.CancellationPending) return;
                    step++;
                    w.ReportProgress(step - 1, string.Format("Deleting {0:N0} of {1:N0}   {2}", step, toDelete.Count, f.FullPath));
                    FileEntry keeper;
                    string reason = Selection.VerifyBeforeDelete(f, verify, keepers, out keeper);
                    if (reason == null) reason = deleteFile(f.FullPath);
                    if (reason == null)
                    {
                        f.Deleted = true;
                        f.Checked = false;
                        deleted++;
                        freed += f.Size;
                        log.Add((permanent ? "DELETED PERMANENTLY\t" : "DELETED (Recycle Bin)\t") + f.FullPath + "\t" + KeptCopyText(keeper));
                        // A deleted archive takes the files inside it along
                        MarkArchiveGone(f.FullPath);
                    }
                    else
                    {
                        skipped.Add(f.FullPath + "  -  " + reason);
                        log.Add("SKIPPED\t" + f.FullPath + "\t" + reason);
                    }
                }

                // 2. Files inside archives: all are verified before any archive changes,
                //    because changing an archive changes what the checks would see
                var approved = new List<FileEntry>();
                var keeperOf = new Dictionary<FileEntry, FileEntry>();
                var toCheck = toDelete.Where(x => x.InArchive && !x.Deleted).ToList();
                for (int i = 0; i < toCheck.Count; i++)
                {
                    var f = toCheck[i];
                    if (w.CancellationPending) return;
                    w.ReportProgress(step, string.Format("Checking the files inside archives before changing them: {0:N0} of {1:N0}", i + 1, toCheck.Count));
                    FileEntry keeper;
                    string reason = Selection.VerifyBeforeDelete(f, verify, keepers, out keeper);
                    if (reason == null) { approved.Add(f); keeperOf[f] = keeper; }
                    else
                    {
                        skipped.Add(f.FullPath + "  -  " + reason);
                        log.Add("SKIPPED\t" + f.FullPath + "\t" + reason);
                    }
                }
                var byArchive = approved.GroupBy(f => f.Archive, StringComparer.OrdinalIgnoreCase).ToList();
                int archiveNumber = 0, archiveTotal = byArchive.Count;
                foreach (var inArchive in byArchive)
                {
                    if (w.CancellationPending) return;
                    // Each archive moves the progress bar by its number of files, in stages
                    archiveNumber++;
                    int start = step, units = inArchive.Count();
                    string name = Path.GetFileName(inArchive.Key);
                    Action<string, double> stage = (what, fraction) =>
                        w.ReportProgress(start + (int)(fraction * units),
                                         string.Format("Archive {0:N0} of {1:N0}: {2} ({3:N0} file(s) to remove) - {4}",
                                                       archiveNumber, archiveTotal, name, units, what));
                    var remove = inArchive.ToDictionary(f => f.FullPath.Substring(f.Archive.Length + 1), f => f.Group.Hash,
                                                        StringComparer.OrdinalIgnoreCase);
                    bool archiveDeleted;
                    string reason = ArchiveWriter.RemoveEntries(inArchive.Key, remove, Path.GetTempPath(), deleteFile, out archiveDeleted, stage);
                    step = start + units;
                    if (reason != null)
                    {
                        foreach (var f in inArchive)
                        {
                            skipped.Add(f.FullPath + "  -  " + reason);
                            log.Add("SKIPPED\t" + f.FullPath + "\t" + reason);
                        }
                        continue;
                    }
                    foreach (var f in inArchive)
                    {
                        f.Deleted = true;
                        f.Checked = false;
                        deleted++;
                        freed += f.Size;
                        log.Add("REMOVED FROM ARCHIVE\t" + f.FullPath + "\t" + KeptCopyText(keeperOf[f]));
                    }
                    if (archiveDeleted)
                    {
                        log.Add((permanent ? "ARCHIVE DELETED PERMANENTLY (empty)\t" : "ARCHIVE DELETED (empty, Recycle Bin)\t") + inArchive.Key);
                        MarkArchiveGone(inArchive.Key);
                    }
                    else
                    {
                        MarkArchiveChanged(inArchive.Key);
                    }
                }
            };
            // A stage can be one long step (e.g. extracting a big archive): a ticking
            // "still working" keeps the window from looking stuck
            string lastText = "";
            DateTime lastUpdate = DateTime.Now;
            var alive = new Timer { Interval = 1000 };
            alive.Tick += (s, e) =>
            {
                var quiet = DateTime.Now - lastUpdate;
                if (quiet.TotalSeconds >= 3)
                    SetStatus(string.Format("{0}   (still working, {1}:{2:00})", lastText, (int)quiet.TotalMinutes, quiet.Seconds));
            };
            alive.Start();
            worker.ProgressChanged += (s, e) =>
            {
                progress.Value = Math.Max(0, Math.Min(e.ProgressPercentage, progress.Maximum));
                if ((string)e.UserState != lastText) lastUpdate = DateTime.Now;
                lastText = (string)e.UserState;
                SetStatus(lastText);
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                alive.Stop();
                alive.Dispose();
                SetBusy(false);
                string logPath = WriteLog(log);
                foreach (var g in groups) g.Files.RemoveAll(f => f.Deleted);
                groups.RemoveAll(g => g.Valid.Count < 2);
                BuildTree();
                var msg = new StringBuilder();
                msg.AppendFormat(permanent ? "{0:N0} file(s) permanently deleted, {1} freed." : "{0:N0} file(s) sent to the Recycle Bin, {1} freed.",
                                 deleted, FormatSize(freed));
                if (e.Error != null) msg.Append("\r\n\r\nStopped by an error: " + e.Error.Message);
                if (skipped.Count > 0)
                {
                    msg.AppendFormat("\r\n\r\n{0:N0} file(s) were not deleted:\r\n", skipped.Count);
                    msg.Append(string.Join("\r\n", skipped.Take(15)));
                    if (skipped.Count > 15) msg.Append("\r\n...");
                }
                if (logPath != null) msg.Append("\r\n\r\nA list of what was done is in:\r\n" + logPath);
                SetStatus(string.Format("{0:N0} file(s) deleted, {1} freed.", deleted, FormatSize(freed)));
                MessageBox.Show(this, msg.ToString(), Text, MessageBoxButtons.OK,
                                skipped.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            };
            worker.RunWorkerAsync();
        }

        static string KeptCopyText(FileEntry keeper)
        {
            return keeper == null ? "kept copy: none (deleting every copy was chosen)" : "kept copy: " + keeper.FullPath;
        }

        // An archive was deleted: the files inside it are gone too
        void MarkArchiveGone(string archive)
        {
            foreach (var f in groups.SelectMany(g => g.Files))
            {
                if (f.InArchive && string.Equals(f.Archive, archive, StringComparison.OrdinalIgnoreCase))
                {
                    f.Deleted = true;
                    f.Checked = false;
                }
                else if (!f.InArchive && string.Equals(f.FullPath, archive, StringComparison.OrdinalIgnoreCase))
                {
                    f.Deleted = true;
                    f.Checked = false;
                }
            }
        }

        // Files were removed from an archive: the files still in it stay valid (their
        // archive's new date is recorded), but the archive file itself no longer
        // matches its scan, so as a file it is set aside
        void MarkArchiveChanged(string archive)
        {
            DateTime modified = File.GetLastWriteTimeUtc(archive);
            foreach (var f in groups.SelectMany(g => g.Files))
            {
                if (f.InArchive && string.Equals(f.Archive, archive, StringComparison.OrdinalIgnoreCase))
                    f.ArchiveModified = modified;
                else if (!f.InArchive && string.Equals(f.FullPath, archive, StringComparison.OrdinalIgnoreCase))
                {
                    f.Stale = true;
                    f.Checked = false;
                    f.StaleReason = "files were removed from it";
                }
            }
        }

        // Deletes without the Recycle Bin (read-only files too). Returns null on
        // success, or why the file was not deleted.
        static string DeletePermanently(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes &= ~FileAttributes.ReadOnly;
                fi.Delete();
                return File.Exists(path) ? "the file is still there" : null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        static string WriteLog(List<string> lines)
        {
            if (lines.Count == 0) return null;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Duplicate File Finder");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "deleted-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".log");
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
                return path;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------- save / load / export

        void SaveResults()
        {
            if (busy) return;
            if (groups.Count == 0) { MessageBox.Show(this, "There are no results to save yet.", Text); return; }
            using (var dlg = new SaveFileDialog { Filter = "Duplicate File Finder results (*.dupes)|*.dupes", FileName = "duplicates-" + DateTime.Now.ToString("yyyy-MM-dd") + ".dupes" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    ResultsFile.Save(dlg.FileName, groups, txtFolders.Text);
                    SetStatus("Results saved to " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save:\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        void LoadResults()
        {
            if (busy) return;
            using (var dlg = new OpenFileDialog { Filter = "Duplicate File Finder results (*.dupes)|*.dupes|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) LoadResults(dlg.FileName);
            }
        }

        void LoadResults(string path)
        {
            if (busy) return;
            if (CheckedFiles().Count > 0 &&
                MessageBox.Show(this, "Loading discards the current results and checkmarks. Continue?", Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                string folders;
                int changed;
                groups = ResultsFile.Load(path, out folders, out changed);
                txtFolders.Text = folders;
                errors.Clear();
                ShowErrorsLink();
                // Files may have changed since the results were saved: compare contents before deleting
                chkVerify.Checked = true;
                BuildTree();
                string msg = string.Format("Loaded {0:N0} groups of duplicates from {1}.", groups.Count, Path.GetFileName(path));
                if (changed > 0) msg += string.Format(" {0:N0} file(s) changed or missing since then are shown in gray and ignored.", changed);
                SetStatus(msg);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not load the results:\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        void ExportCsv()
        {
            if (groups.Count == 0) { MessageBox.Show(this, "There are no results to export yet.", Text); return; }
            using (var dlg = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "duplicates.csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var lines = new List<string> { "Group,Checked,SizeBytes,Modified,InArchive,Path,Hash" };
                    int n = 0;
                    foreach (var g in groups.Where(x => x.Valid.Count > 1))
                    {
                        n++;
                        foreach (var f in g.Files.Where(x => !x.Deleted))
                            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:yyyy-MM-dd HH:mm:ss},{4},\"{5}\",{6}",
                                      n, f.Checked ? "yes" : "no", f.Size, f.Modified.ToLocalTime(), f.InArchive ? "yes" : "no",
                                      f.FullPath.Replace("\"", "\"\""), g.Hash));
                    }
                    File.WriteAllLines(dlg.FileName, lines, new UTF8Encoding(true));
                    SetStatus("Exported to " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not export:\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ---------------------------------------------------------- helpers

        void SetStatus(string text)
        {
            lblStatus.Text = text;
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int i = 0;
            while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
            return i == 0 ? bytes.ToString("N0") + " bytes" : value.ToString(value < 10 ? "0.0" : "0") + " " + units[i];
        }

        void ShowTextDialog(string title, string text)
        {
            using (var dlg = new Form { Text = title, Size = new Size(900, 500), StartPosition = FormStartPosition.CenterParent, Font = Font })
            {
                dlg.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = text });
                dlg.ShowDialog(this);
            }
        }

        void ShowHelp()
        {
            ShowTextDialog("How to use Duplicate File Finder", string.Join(Environment.NewLine, new[]
            {
                "1. Type or add one or more folders (separate them with ;) and click Scan (F5).",
                "   Files are compared by content, so copies are found even if renamed. All folders are compared with each other.",
                "",
                "2. The list shows the folders that contain duplicates (View: Folders), or each set of identical",
                "   files together (View: Duplicate groups). Check the copies you want to delete:",
                "   - click a file's checkbox, or a folder's checkbox to check everything below it",
                "     (a filled box means part of the folder is checked; click again to uncheck it all),",
                "   - use Auto-select (keep newest / oldest / shortest path ...) on everything shown, or",
                "     right-click a folder > \"Auto-select inside this folder\" to apply it to that folder and",
                "     its subfolders only (copies elsewhere are not changed), or",
                "   - right-click a file: \"Keep only this copy\" or \"Check all copies in this folder\".",
                "   At least one copy of each file always stays unchecked: if a folder holds every copy",
                "   of a file, checking the folder leaves one of them unchecked.",
                "   Red = will be deleted.  Green = all other copies are checked, so this one is kept.",
                "",
                "   \"Include archives\" also compares the files inside zip, rar, 7z, cab ... files. They appear in blue",
                "   under their archive and can be checked: they are then removed from the archive (zip, rar, 7z,",
                "   tar-based and cab archives can be changed), and an archive left with no files is deleted.",
                "   One copy of every file always stays, and it may be the copy inside an archive. When the",
                "   program chooses which copy to keep, it prefers an ordinary file. Auto-select's keep-one rules",
                "   only check ordinary files; use",
                "   \"Also check the copies inside archives\" or the folder checkboxes for archive contents.",
                "   Selecting a file lists all its copies on the right, marked [delete] or [keep].",
                "",
                "3. Click a file to preview it on the right (pictures and text files). Double-click or press Enter to open it.",
                "   Right-click for \"Show in folder\" and \"Copy path\".",
                "",
                "4. Click \"Delete checked files...\". Files go to the Recycle Bin, or are deleted for good if",
                "   \"Delete permanently\" is ticked. Each file is checked again just before deleting, and a log",
                "   of what was deleted is saved in Documents\\Duplicate File Finder.",
                "",
                "Saving your work: \"Save results\" (Ctrl+S) stores the list and your checkmarks in a .dupes file.",
                "\"Load results\" (Ctrl+O) continues later. Files that changed since then are shown in gray and ignored,",
                "and contents are re-compared before deleting. You can also drag a .dupes file onto the window.",
                "",
                "Other: Filter (Ctrl+F) shows only groups whose paths contain some text; Auto-select only affects the",
                "groups shown. Sort changes the order of the groups. Export CSV saves the list for Excel."
            }));
        }
    }
}
