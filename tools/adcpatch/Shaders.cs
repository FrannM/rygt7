using System.Collections.Concurrent;
using GTToolsSharp;
using GTToolsSharp.Encryption;
using GTToolsSharp.Entities;
using GTToolsSharp.Volumes;

/// FEAT-GTS-266 (from RyGTS's shader tooling and FIX-GTS-281): one of the game's volume indexes
/// (gt.idx) opened for reading. GT7's index (CUSA24769: contents\gt.idx) is GTToolsSharp's MPH
/// volume: it keeps only the hash of each file's name, so a file is named "#<FNV-1a hash>" here
/// (the same in every copy of a game version). GT Sport's PFS index (gt.idx) names its files.
abstract class GameVolume : IDisposable
{
    /// The file names, in the volume's order.
    public abstract IReadOnlyList<string> Files { get; }

    /// Unpacks one file to target. False when it could not be unpacked.
    public abstract bool Unpack(string name, string target);

    public abstract void Dispose();

    static readonly ConcurrentDictionary<string, Keyset> PfsKeysets = new(StringComparer.OrdinalIgnoreCase);

    static GameVolume()
    {
        // GTToolsSharp logs every volume and file it opens to the console (Program.PrintToConsole,
        // internal): off, so adcpatch's own progress lines are what the launcher sees.
        try
        {
            typeof(GTVolumeMPH).Assembly.GetType("GTToolsSharp.Program")
                ?.GetField("PrintToConsole", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.SetValue(null, false);
        }
        catch { }
    }

    /// Opens index (GT7's MPH form first, then GT Sport's PFS one); volumeDirs are where else a
    /// GT7 index's data volumes may be (an update's index names the base game's volumes too).
    /// Null when it is neither.
    public static GameVolume Open(string index, IEnumerable<string> volumeDirs)
    {
        try
        {
            var mph = GTVolumeMPH.Load(index);
            if (mph != null) return new MphVolume(mph, volumeDirs);
        }
        catch { }
        try
        {
            if (!PfsKeysets.TryGetValue(index, out var keyset))
            {
                keyset = KeysetStore.Default_Keysets.FirstOrDefault(k =>
                {
                    try
                    {
                        using var probe = GTVolumePFS.Load(k, index, false, Syroot.BinaryData.Core.Endian.Little);
                        return probe != null;
                    }
                    catch { return false; }
                });
                if (keyset == null) return null;
                PfsKeysets[index] = keyset;
            }
            var pfs = GTVolumePFS.Load(keyset, index, false, Syroot.BinaryData.Core.Endian.Little);
            return pfs == null ? null : new PfsVolume(pfs);
        }
        catch { return null; }
    }

    sealed class PfsVolume : GameVolume
    {
        readonly GTVolumePFS vol;
        readonly PFSVolumeUnpacker unpacker;
        readonly Dictionary<uint, GTToolsSharp.BTree.FileInfoKey> infos = new();
        readonly Dictionary<string, (string key, uint entry)> files = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> names = new();

        public PfsVolume(GTVolumePFS vol)
        {
            this.vol = vol;
            unpacker = new PFSVolumeUnpacker(vol);
            foreach (var info in vol.BTree.FileInfos.Entries) infos[info.FileIndex] = info;
            foreach (var file in vol.BTree.GetAllRegisteredFiles())
            {
                string name = file.Key.Replace('\\', '/');
                if (files.TryAdd(name, (file.Key, file.Value.EntryIndex))) names.Add(name);
            }
        }

        public override IReadOnlyList<string> Files => names;

        public override bool Unpack(string name, string target)
        {
            if (!files.TryGetValue(name, out var f)) return false;
            return unpacker.UnpackFile(infos[f.entry], name, target) && File.Exists(target);
        }

        public override void Dispose() => vol.Dispose();
    }

    sealed class MphVolume : GameVolume
    {
        readonly GTVolumeMPH vol;
        readonly Dictionary<string, MPHNodeInfo> nodes = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> names = new();

        public MphVolume(GTVolumeMPH vol, IEnumerable<string> volumeDirs)
        {
            this.vol = vol;
            // The data volumes the index names that are not next to it (GTToolsSharp looks only
            // there): from the other game folder, as the game's app0 has them.
            var infos = vol.VolumeHeader.VolumeInfo;
            for (int i = 0; i < infos.Length && i < vol.SplitVolumes.Length; i++)
            {
                if (vol.SplitVolumes[i] != null) continue;
                foreach (var dir in volumeDirs)
                {
                    string path = Path.Combine(dir, infos[i].FileName);
                    if (!File.Exists(path)) continue;
                    var cluster = ClusterVolume.Read(path);
                    if (cluster == null) continue;
                    cluster.Name = infos[i].FileName;
                    vol.SplitVolumes[i] = cluster;
                    break;
                }
            }
            foreach (var cluster in vol.SplitVolumes)
                if (cluster != null) cluster.ParentMasterVolume = vol;
            // FileDeviceMPHSuperintendentHeader (internal in GTToolsSharp) holds the nodes.
            var header = vol.VolumeHeader;
            var list = header.GetType().GetProperty("Nodes")?.GetValue(header) as MPHNodeInfo[] ?? Array.Empty<MPHNodeInfo>();
            foreach (var node in list)
            {
                if (!node.IsFromClusterVolume()) continue; // PS5 (PFS-style) nodes: not unpacked by GTToolsSharp
                int v = node.GetVolumeIndex();
                if (v >= vol.SplitVolumes.Length || vol.SplitVolumes[v] == null) continue;
                string name = $"#{node.EntryHash:x8}";
                if (nodes.TryAdd(name, node)) names.Add(name);
            }
        }

        public override IReadOnlyList<string> Files => names;

        public override bool Unpack(string name, string target)
        {
            if (!nodes.TryGetValue(name, out var node)) return false;
            return vol.SplitVolumes[node.GetVolumeIndex()].UnpackFile(node, target, "", -1) && File.Exists(target);
        }

        public override void Dispose() => vol.Dispose();
    }
}

/// Finds the game's PS4 shader programs in its volumes. Each program ends with a 32-byte footer
/// that starts with "OrbShdr"; the renderer identifies a program by the hash in that footer
/// (offset 16) and the footer gives the code length (bits 8-31 of offset 8).
/// FEAT-GTS-266 (from RyGTS): GT7's volumes as well as GT Sport's; nothing names a game.
static class GameShaders
{
    static readonly byte[] Signature = "OrbShdr"u8.ToArray();

    /// The volume indexes of the game folders, the update's first: <folder>\contents\gt.idx
    /// (GT7's app0 layout) or <folder>\gt.idx.
    public static List<string> IndexFiles(string game, string update)
    {
        var found = new List<string>();
        foreach (var dir in new[] { update, game })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string contents = Path.Combine(dir, "contents", "gt.idx");
            found.Add(File.Exists(contents) ? contents : Path.Combine(dir, "gt.idx"));
        }
        return found;
    }

    static List<string> VolumeDirs(IEnumerable<string> indexFiles) =>
        indexFiles.Select(Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// FEAT-GTS-266 (from RyGTS FIX-GTS-282): the programs of manifest records, from their file
    /// names (<program>-<SPIR-V hash>.rec).
    public static HashSet<ulong> RecordPrograms(IEnumerable<string> recordFiles)
    {
        var want = new HashSet<ulong>();
        foreach (var rec in recordFiles)
        {
            string name = Path.GetFileNameWithoutExtension(rec);
            if (name.Length > 16 && name[16] == '-' &&
                ulong.TryParse(name[..16], System.Globalization.NumberStyles.HexNumber, null, out var h))
                want.Add(h);
        }
        return want;
    }

    /// Copies each wanted program the index places (from its first instruction through its footer)
    /// out of the game's volumes into outDir as <hash>.bin, skipping programs already there.
    /// From RyGTS FIX-GTS-281/282: one pass over the volumes, unpacking only the files that hold
    /// wanted programs, on ADCPATCH_THREADS threads (default 6). Each file is written whole or not
    /// at all (.tmp, then renamed), so a kept file is complete.
    public static (int present, int written, int files, double seconds) ExtractPrograms(
        List<string> indexFiles, IEnumerable<string> indexLines, HashSet<ulong> wanted, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var want = new HashSet<ulong>(wanted);
        int present = want.RemoveWhere(h => File.Exists(Path.Combine(outDir, $"{h:x16}.bin")));
        var index = new Dictionary<ulong, (string path, long footer, int length)>();
        foreach (var line in indexLines)
        {
            var f = line.Split('\t');
            if (f.Length < 4) continue;
            ulong h = Convert.ToUInt64(f[0], 16);
            if (want.Contains(h)) index[h] = (f[1], long.Parse(f[2]), int.Parse(f[3]));
        }
        var groups = index.GroupBy(kv => kv.Value.path, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        string work = Path.Combine(Path.GetTempPath(), "rygt-programs-" + Environment.ProcessId);
        int written = 0;
        var started = DateTime.UtcNow;
        try
        {
            int threads = int.TryParse(Environment.GetEnvironmentVariable("ADCPATCH_THREADS"), out var t) && t > 0 ? t : 6;
            if (groups.Count > 0)
            {
                ForEachWantedFile(indexFiles, work, path => groups.ContainsKey(path), (path, data) =>
                {
                    foreach (var (hash, e) in groups[path])
                    {
                        if (e.footer + 32 > data.Length) continue;
                        int start = CodeStart(data, e.footer);
                        if (start < 0) continue;
                        string target = Path.Combine(outDir, $"{hash:x16}.bin");
                        File.WriteAllBytes(target + ".tmp", data[start..(int)(e.footer + 32)]);
                        File.Move(target + ".tmp", target, true);
                        Interlocked.Increment(ref written);
                    }
                }, threads);
            }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
        return (present, written, groups.Count, (DateTime.UtcNow - started).TotalSeconds);
    }

    /// FEAT-GTS-266 (from RyGTS FIX-GTS-283): the shader index's lines; a name ending in .br is
    /// Brotli-compressed (the form to ship: mostly the programs' IDs, which compress well).
    public static IEnumerable<string> ReadIndex(string path)
    {
        if (!path.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(path)) yield return line;
            yield break;
        }
        using var file = File.OpenRead(path);
        using var brotli = new System.IO.Compression.BrotliStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// Writes the index ordered by file and footer offset (it compresses better: a file's
    /// offsets are near each other), compressed when the name ends in .br.
    public static void WriteIndex(string path, IEnumerable<string> lines)
    {
        var ordered = lines.Select(l => (line: l, f: l.Split('\t')))
                           .OrderBy(x => x.f[1], StringComparer.Ordinal)
                           .ThenBy(x => long.Parse(x.f[2]))
                           .Select(x => x.line);
        using var file = File.Create(path);
        Stream output = path.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
            ? new System.IO.Compression.BrotliStream(file, System.IO.Compression.CompressionLevel.SmallestSize)
            : file;
        using (var writer = new StreamWriter(output) { NewLine = "\n" })
        {
            foreach (var line in ordered) writer.WriteLine(line);
        }
    }

    /// (hash, footer offset, code length) of every program in the data.
    public static List<(ulong hash, long footer, int length)> Find(byte[] data)
    {
        var found = new List<(ulong, long, int)>();
        int i = 0;
        while (true)
        {
            int at = data.AsSpan(i).IndexOf(Signature);
            if (at < 0) return found;
            i += at;
            if (i + 32 <= data.Length)
            {
                uint bits = BitConverter.ToUInt32(data, i + 8);
                ulong hash = BitConverter.ToUInt64(data, i + 16);
                found.Add((hash, i, (int)(bits >> 8)));
            }
            i += Signature.Length;
        }
    }

    /// Where the program whose footer is at `footer` starts: its first instruction is
    /// s_mov_b32 vcc_hi, imm (0xBEEB03FF) with the footer (imm + 1) * 2 dwords after it, as the
    /// renderer finds the footer. -1 when there is no such instruction.
    public static int CodeStart(byte[] data, long footer)
    {
        for (long p = footer - 8; p >= 0 && footer - p <= (1 << 20); p -= 4)
        {
            if (BitConverter.ToUInt32(data, (int)p) == 0xBEEB03FF &&
                p + ((long)BitConverter.ToUInt32(data, (int)p + 4) + 1) * 8 == footer)
                return (int)p;
        }
        return -1;
    }

    /// File types that hold no shader programs (GT Sport's names; GT7's files have no names).
    static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".JXR", ".JPG", ".PNG", ".MP4", ".SXD", ".SXD2", ".SXD3", ".AST", ".GTESD",
        ".OTF", ".TTF", ".SVG", ".JSON", ".TXT", ".ADC", ".MPROJECT", ".ECP", ".CSS", ".JS",
    };

    /// From RyGTS FIX-GTS-281: the given files of the volumes (the update's first; a file is read
    /// once), unpacked on several threads, each with its own volume handle and temp file; visit
    /// runs on those threads. Unpacking (decryption, decompression) is single-threaded per file.
    /// threads is kept low: each holds a whole file (track packs are hundreds of MB). Returns how
    /// many failed.
    public static int ForEachWantedFile(List<string> indexFiles, string tempDir,
                                        Func<string, bool> wanted, Action<string, byte[]> visit,
                                        int threads)
    {
        int failed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = VolumeDirs(indexFiles);
        Directory.CreateDirectory(tempDir);
        int slot = 0;
        foreach (var index in indexFiles)
        {
            if (!File.Exists(index)) continue;
            var work = new List<string>();
            using (var vol = GameVolume.Open(index, dirs))
            {
                if (vol == null) { Console.Error.WriteLine($"could not open {index}"); continue; }
                foreach (var name in vol.Files)
                {
                    if (!seen.Add(name) || !wanted(name)) continue;
                    work.Add(name);
                }
            }
            if (work.Count == 0) continue;
            int next = -1;
            var workers = new List<Thread>();
            for (int t = 0; t < Math.Max(1, Math.Min(threads, work.Count)); t++)
            {
                string temp = Path.Combine(tempDir, $"part{Interlocked.Increment(ref slot)}.bin");
                var thread = new Thread(() =>
                {
                    using var vol = GameVolume.Open(index, dirs);
                    if (vol == null) return;
                    for (int i = Interlocked.Increment(ref next); i < work.Count; i = Interlocked.Increment(ref next))
                    {
                        string name = work[i];
                        try
                        {
                            if (File.Exists(temp)) File.Delete(temp);
                            if (!vol.Unpack(name, temp)) continue;
                            visit(name, File.ReadAllBytes(temp));
                        }
                        catch (Exception e)
                        {
                            Interlocked.Increment(ref failed);
                            Console.Error.WriteLine($"skipped {name}: {e.GetType().Name}: {e.Message}");
                        }
                    }
                    if (File.Exists(temp)) File.Delete(temp);
                });
                thread.Start();
                workers.Add(thread);
            }
            foreach (var thread in workers) thread.Join();
        }
        return failed;
    }

    /// Walks every file of the volumes (the update's first; a file is read once) and calls
    /// visit(name, bytes) for each file that may hold shaders. From RyGTS FIX-GTS-281: a file that
    /// fails to unpack or to scan is reported and skipped (one bad file ended a 95 GB scan with
    /// nothing written); returns how many failed.
    public static int ForEachFile(List<string> indexFiles, string tempDir, Action<string, byte[]> visit)
    {
        int failed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = VolumeDirs(indexFiles);
        Directory.CreateDirectory(tempDir);
        string temp = Path.Combine(tempDir, "scan.bin");
        foreach (var index in indexFiles)
        {
            if (!File.Exists(index)) continue;
            using var vol = GameVolume.Open(index, dirs);
            if (vol == null) { Console.Error.WriteLine($"could not open {index}"); continue; }
            foreach (var name in vol.Files)
            {
                if (!seen.Add(name) || SkipExtensions.Contains(Path.GetExtension(name))) continue;
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                    if (!vol.Unpack(name, temp)) continue;
                    visit(name, File.ReadAllBytes(temp));
                }
                catch (Exception e)
                {
                    failed++;
                    Console.Error.WriteLine($"skipped {name}: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        if (File.Exists(temp)) File.Delete(temp);
        return failed;
    }
}
