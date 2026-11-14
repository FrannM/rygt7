using System.Security.Cryptography;
// adcpatch: rewrites GT Sport (v1.69) AdHoc scripts for ryGT's offline features.
//   adcpatch roundtrip <in.adc> <out.adc>
//   adcpatch uikit     <UI_KIT.ADC> <out.adc> <openGrim.ad>
//   adcpatch livery2   <LIVERY2.ADC> <out.adc>
//   adcpatch thumbnail <THUMBNAIL.ADC> <out.adc>
//   adcpatch gallery   <GALLERY.ADC> <out.adc>
//   adcpatch grim      <GRIM.ADC> <out.adc> <grim.ad>
// The output is never larger than the input (the game reads scripts by their original size);
// it is padded with zeros up to the original size.
// Shader tooling (FEAT-GTS-266, Shaders.cs; GT7's volumes and GT Sport's):
//   adcpatch shaderindex <game> <update or ""> <out.tsv[.br]>
//   adcpatch shadercodes <game> <update or ""> <index.tsv[.br]> <manifest dir or hashes.txt> <out dir>
//   adcpatch manifestpack [--serial <title id>] <out shader-manifest.bin> <source>...   (FEAT-GTS-267)
//   adcpatch manifestprep <game> <update or ""> <shader-manifest.bin> <cache dir> [serial]
using GTAdhocToolchain.CodeGen;
using GTAdhocToolchain.Compiler;
using GTAdhocToolchain.Core;
using GTAdhocToolchain.Core.Instructions;
using GTAdhocToolchain.Disasm;
using GTAdhocToolchain.Preprocessor;
using Esprima;

static AdhocFile Load(string path, out AdhocSymbolMap map)
{
    var file = AdhocFile.ReadFromFile(path)[0];
    map = new AdhocSymbolMap();
    for (int i = 0; i < file.SymbolTable.Count; i++)
    {
        file.SymbolTable[i].Id = i;
        map.Symbols.TryAdd(file.SymbolTable[i].Name, file.SymbolTable[i]);
    }
    return file;
}

static void Save(AdhocFile file, AdhocSymbolMap map, string original, string output)
{
    AdhocStream.SymbolIdsByName = map.Symbols.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
    var gen = new AdhocCodeGen(file.TopLevelFrame, map);
    gen.Generate();
    string tmp = output + ".tmp";
    gen.SaveTo(tmp);
    var data = File.ReadAllBytes(tmp);
    File.Delete(tmp);
    long size = new FileInfo(original).Length;
    if (data.Length > size)
        throw new Exception($"patched script is {data.Length} bytes, more than the original {size}");
    var padded = new byte[size];
    data.CopyTo(padded, 0);
    File.WriteAllBytes(output, padded);
    Console.WriteLine($"{output}: {data.Length} of {size} bytes");
}

// Every symbol a frame uses gets an id in the target's table (new names are appended).
static void RegisterSymbols(object node, AdhocSymbolMap map, HashSet<object> seen)
{
    if (node == null || !seen.Add(node)) return;
    if (node is AdhocSymbol s)
    {
        if (!map.Symbols.TryGetValue(s.Name, out var existing))
        {
            existing = new AdhocSymbol(map.Symbols.Count, s.Name, s.HasHexEscape);
            map.Symbols.Add(s.Name, existing);
        }
        return;
    }
    if (node is string || node.GetType().IsPrimitive || node is Enum) return;
    if (node is System.Collections.IEnumerable list)
    {
        foreach (var item in list) RegisterSymbols(item, map, seen);
        return;
    }
    foreach (var prop in node.GetType().GetProperties())
    {
        if (prop.GetIndexParameters().Length != 0) continue;
        var t = prop.PropertyType;
        if (t.IsPrimitive || t == typeof(string) || t.IsEnum) continue;
        if (!(t.Namespace ?? "").StartsWith("GTAdhocToolchain") && !typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) continue;
        object v;
        try { v = prop.GetValue(node); } catch { continue; }
        RegisterSymbols(v, map, seen);
    }
}

static AdhocCodeFrame Compile(string sourcePath)
{
    var source = File.ReadAllText(sourcePath);
    var pre = new AdhocScriptPreprocessor();
    pre.SetCurrentFileName(Path.GetFileName(sourcePath));
    string preprocessed = pre.Preprocess(source);
    var errors = new CollectingErrorHandler();
    var parser = new AdhocAbstractSyntaxTree(new ParserOptions { ErrorHandler = errors });
    var program = parser.ParseScript(preprocessed, sourcePath);
    if (errors.Errors.Any())
        throw new Exception(string.Join("\n", errors.Errors.Select(e => $"{e.Description} line {e.LineNumber}")));
    var compiler = new AdhocScriptCompiler(12);
    return compiler.CompileScript(program, sourcePath);
}

static IEnumerable<(List<InstructionBase> list, int index, SubroutineBase sub)> Subroutines(List<InstructionBase> list)
{
    for (int i = 0; i < list.Count; i++)
    {
        if (list[i] is SubroutineBase sub)
        {
            yield return (list, i, sub);
            foreach (var inner in Subroutines(sub.CodeFrame.Instructions)) yield return inner;
        }
    }
}


// Removes ins[i..i+n) from a code frame and moves the frame's jump targets past it back by n.
static void RemoveRange(List<InstructionBase> ins, int i, int n)
{
    int end = i + n;
    ins.RemoveRange(i, n);
    foreach (var x in ins)
    {
        switch (x)
        {
            case InsJump j when j.JumpInstructionIndex >= end: j.JumpInstructionIndex -= n; break;
            case InsJumpIfFalse f when f.JumpIndex >= end: f.JumpIndex -= n; break;
            case InsJumpIfTrue t when t.JumpIndex >= end: t.JumpIndex -= n; break;
            case InsLogicalBase l when l.InstructionJumpIndex >= end: l.InstructionJumpIndex -= n; break;
            case InsTryCatch tc when tc.InstructionIndex >= end: tc.InstructionIndex -= n; break;
        }
    }
}

// Removes debug-build asserts (`if (GetVersionBuild() == "debug") DebugTool::Assert(...)`) and
// DebugTool::putHere logging from one code frame, fixing up the frame's jump targets. Makes room for
// patched code; both only do anything in debug builds or write logs.
static int StripDebug(List<InstructionBase> ins)
{
    static bool IsDebugTool(InstructionBase x, string fn) =>
        x is InsVariableEvaluation v && v.Path.Count == 4 && v.Path[0].Name == "main" && v.Path[1].Name == "DebugTool" && v.Path[2].Name == fn;
    int removed = 0;
    for (int i = 0; i < ins.Count; i++)
    {
        int end = -1;
        if (i + 4 < ins.Count && ins[i] is InsVariableEvaluation g && g.Path.Count == 1 && g.Path[0].Name == "GetVersionBuild" &&
            ins[i + 1] is InsCall c && c.ArgumentCount == 0 && ins[i + 2] is InsStringConst sc && sc.String.Name == "debug" &&
            ins[i + 3] is InsBinaryOperator op && op.Operator.Name == "__eq__" && ins[i + 4] is InsJumpIfFalse jf &&
            jf.JumpIndex > i + 5 && jf.JumpIndex <= ins.Count && ins[jf.JumpIndex - 1] is InsLeaveScope &&
            IsDebugTool(ins[i + 5], "Assert") && ins[jf.JumpIndex - 2] is InsPop && ins[jf.JumpIndex - 3] is InsCall)
        {
            bool plain = true;
            for (int k = i + 5; k < jf.JumpIndex - 3; k++)
                if (ins[k] is InsCall || ins[k] is InsJump || ins[k] is InsJumpIfFalse || ins[k] is InsJumpIfTrue || ins[k] is InsLogicalBase || ins[k] is SubroutineBase) plain = false;
            if (plain) end = jf.JumpIndex;
        }
        else if (IsDebugTool(ins[i], "putHere"))
        {
            int k = i + 1;
            while (k < ins.Count && ins[k] is not InsCall && ins[k] is not InsJump && ins[k] is not InsJumpIfFalse &&
                   ins[k] is not InsJumpIfTrue && ins[k] is not InsLogicalBase && ins[k] is not SubroutineBase) k++;
            if (k + 1 < ins.Count && ins[k] is InsCall && ins[k + 1] is InsPop) end = k + 2;
        }
        if (end < 0) continue;
        int n = end - i;
        // nothing may jump into the removed range (only to its start or past it)
        bool inside = ins.Any(x => (x is InsJump j && j.JumpInstructionIndex > i && j.JumpInstructionIndex < end) ||
                                   (x is InsJumpIfFalse f && f.JumpIndex > i && f.JumpIndex < end && !ReferenceEquals(x, ins[Math.Min(i + 4, ins.Count - 1)])) ||
                                   (x is InsJumpIfTrue t && t.JumpIndex > i && t.JumpIndex < end) ||
                                   (x is InsLogicalBase l && l.InstructionJumpIndex > i && l.InstructionJumpIndex < end) ||
                                   (x is InsTryCatch tc && tc.InstructionIndex > i && tc.InstructionIndex < end));
        if (inside) continue;
        RemoveRange(ins, i, n);
        removed++;
        i--;
    }
    return removed;
}

static int StripDebugAll(AdhocFile file)
{
    int n = StripDebug(file.TopLevelFrame.Instructions);
    foreach (var (_, _, sub) in Subroutines(file.TopLevelFrame.Instructions).ToList())
        n += StripDebug(sub.CodeFrame.Instructions);
    return n;
}


// Drops symbols nothing references any more (after code was replaced or removed); ids are renumbered.
static void PruneSymbols(AdhocFile file, AdhocSymbolMap map)
{
    var used = new AdhocSymbolMap();
    RegisterSymbols(file.TopLevelFrame, used, new HashSet<object>(ReferenceEqualityComparer.Instance));
    var kept = map.Symbols.Where(kv => used.Symbols.ContainsKey(kv.Key)).ToList();
    Console.WriteLine($"symbols: {map.Symbols.Count} -> {kept.Count}");
    map.Symbols.Clear();
    for (int i = 0; i < kept.Count; i++)
    {
        kept[i].Value.Id = i;
        map.Symbols.Add(kept[i].Key, kept[i].Value);
    }
}

string mode = args[0];
if (mode == "shaderindex")
{
    // FEAT-GTS-266 (from RyGTS, with its FIX-GTS-281 and FIX-GTS-283): shaderindex <game> <update
    // or ""> <out.tsv or out.tsv.br>: hash, volume file, footer offset, code length of every
    // shader program in the game's files (contents\gt.idx for GT7; a GT7 file is named by its
    // hash, "#xxxxxxxx"). Each program is written as it is found (<out>.partial, replaced at the
    // end), so a stop or a crash keeps what was found. The smallest file holding each program is
    // kept, so extraction (shadercodes) unpacks small files rather than a track's pack where the
    // same program is in a car's file too. A name ending in .br is written Brotli-compressed.
    var idx = GameShaders.IndexFiles(args[1], args[2]);
    var found = new Dictionary<ulong, string>();
    var foundSize = new Dictionary<ulong, long>();
    var byExtension = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    int files = 0;
    var started = DateTime.UtcNow;
    string partial = args[3] + ".partial";
    int failed;
    using (var writer = new StreamWriter(partial, false))
    {
        failed = GameShaders.ForEachFile(idx, Path.Combine(Path.GetTempPath(), "rygt-shaderscan-" + Environment.ProcessId), (path, data) =>
        {
            if (++files % 2000 == 0)
            {
                writer.Flush();
                Console.WriteLine($"{files} files, {found.Count} programs, {(DateTime.UtcNow - started).TotalMinutes:F1} min");
            }
            bool any = false;
            foreach (var (hash, footer, length) in GameShaders.Find(data))
            {
                string line = $"{hash:x16}\t{path}\t{footer}\t{length}";
                if (found.ContainsKey(hash))
                {
                    if (data.Length < foundSize[hash]) { found[hash] = line; foundSize[hash] = data.Length; }
                    continue;
                }
                found[hash] = line;
                foundSize[hash] = data.Length;
                writer.WriteLine(line);
                any = true;
            }
            if (any)
            {
                string ext = Path.GetExtension(path);
                byExtension[ext] = byExtension.GetValueOrDefault(ext) + 1;
            }
        });
    }
    GameShaders.WriteIndex(args[3], found.Values);
    File.Delete(partial);
    Console.WriteLine($"{found.Count} programs in {files} files ({failed} skipped) in {(DateTime.UtcNow - started).TotalMinutes:F1} min");
    Console.WriteLine("files with new programs by type: " +
        string.Join(", ", byExtension.OrderByDescending(e => e.Value).Select(e => $"{(e.Key.Length > 0 ? e.Key : "(none)")} {e.Value}")));
    return 0;
}

if (mode == "shadercodes")
{
    // FEAT-GTS-266 (from RyGTS, with its FIX-GTS-281 and FIX-GTS-282): shadercodes <game> <update
    // or ""> <index.tsv[.br]> <manifest dir or hashes.txt> <out dir>: writes <hash>.bin for each
    // wanted program, from its first instruction through its footer, from the player's own game
    // files (nothing leaves the PC). The wanted programs are a manifest folder's (a record file is
    // <program>-<SPIR-V hash>.rec, in it or its per-GPU folders) or a list of hashes; programs
    // already in <out dir> are kept, so after a manifest update only the new ones are unpacked.
    var idx = GameShaders.IndexFiles(args[1], args[2]);
    HashSet<ulong> want;
    if (Directory.Exists(args[4]))
        want = GameShaders.RecordPrograms(Directory.EnumerateFiles(args[4], "*.rec", SearchOption.AllDirectories));
    else
        want = File.ReadAllLines(args[4]).Where(l => l.Trim().Length > 0).Select(l => Convert.ToUInt64(l.Trim(), 16)).ToHashSet();
    var (present, written, files, seconds) = GameShaders.ExtractPrograms(idx, GameShaders.ReadIndex(args[3]), want, args[5]);
    Console.WriteLine($"{want.Count} programs wanted, {present} extracted already; {written} of {want.Count - present} written from {files} files in {seconds:F0} s");
    return 0;
}

if (mode == "manifestpack")
{
    // FEAT-GTS-267 (from RyGTS FEAT-GTS-284 and its "manifestpack merges sources"):
    // manifestpack [--serial <title id>] <out shader-manifest.bin> <source>...: what a release
    // ships for the shader pre-build: manifest records and pipeline recipes (no code: a record
    // holds a translation's inputs and reads, a recipe a pipeline's state) and the index of where
    // each program is in the game's files. Sources, in order: a shader-manifest.bin (its records,
    // recipes and index), a manifest folder (<serial>.manifest: its .rec files and those of its
    // per-GPU folders, where GT7's renderer keeps them, and its pipelines-*.rcp files; not the
    // capture) or an index (.tsv or .tsv.br). Records add up (a record is named by its program
    // and SPIR-V hash, so one already there is the same); a later recipe file of the same GPU
    // replaces an earlier one; a later index replaces an earlier one. So a shipped file grows
    // with: manifestpack shader-manifest.bin shader-manifest.bin <newer manifest folder>.
    // A recipe file is renamed pipelines-shared-..., so a player with the recording GPU imports it
    // too (the renderer skips its own GPU's file).
    // The game's serial (GT7: CUSA24769) comes from --serial, a <serial>.manifest folder's name or
    // a shader-manifest.bin's entries; sources of two games are refused.
    // File: "RYGTMAN1", then Brotli: int count, (string name, int length, bytes) per entry; names
    // are "<serial>.manifest/<file>" and "index.tsv".
    var rest = args.Skip(1).ToList();
    string serial = null;
    Action<string, string> useSerial = (found, source) =>
    {
        if (string.IsNullOrEmpty(found)) return;
        if (serial != null && !serial.Equals(found, StringComparison.OrdinalIgnoreCase))
            throw new Exception($"{source} is {found}'s, the others {serial}'s");
        serial ??= found;
    };
    if (rest.Count > 1 && rest[0] == "--serial") { useSerial(rest[1], "--serial"); rest.RemoveRange(0, 2); }
    if (rest.Count < 2) throw new Exception("manifestpack [--serial <title id>] <out shader-manifest.bin> <source>...");
    string outPath = rest[0];
    var records = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    var recipes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    byte[] indexBytes = null;
    var gpuFolder = new System.Text.RegularExpressions.Regex("^[0-9a-fA-F]{4}-[0-9a-fA-F]{4}$");
    foreach (var source in rest.Skip(1))
    {
        int before = records.Count, recipesBefore = recipes.Count;
        bool index = false;
        if (Directory.Exists(source))
        {
            string folder = Path.GetFileName(Path.GetFullPath(source).TrimEnd('\\', '/'));
            if (folder.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) useSerial(folder[..^".manifest".Length], source);
            var recordDirs = new List<string> { source };
            recordDirs.AddRange(Directory.EnumerateDirectories(source).Where(d => gpuFolder.IsMatch(Path.GetFileName(d))).OrderBy(d => d, StringComparer.Ordinal));
            foreach (var dir in recordDirs)
                foreach (var file in Directory.EnumerateFiles(dir, "*.rec"))
                    records.TryAdd(Path.GetFileName(file), File.ReadAllBytes(file));
            foreach (var file in Directory.EnumerateFiles(source, "pipelines-*.rcp"))
            {
                string name = Path.GetFileName(file);
                recipes[name.StartsWith("pipelines-shared-") ? name : "pipelines-shared-" + name["pipelines-".Length..]] = File.ReadAllBytes(file);
            }
        }
        else if (source.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (name, bytes) in GameShaders.ReadManifestBundle(source))
            {
                if (name == "index.tsv") { indexBytes = bytes; index = true; continue; }
                useSerial(GameShaders.ManifestSerial(name), source);
                string file = Path.GetFileName(name);
                if (file.EndsWith(".rec")) records.TryAdd(file, bytes);
                else if (file.EndsWith(".rcp")) recipes[file] = bytes;
            }
        }
        else
        {
            indexBytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", GameShaders.ReadIndex(source)) + "\n");
            index = true;
        }
        Console.WriteLine($"{source}: {records.Count - before} new records, {recipes.Count - recipesBefore} new recipe files{(index ? ", the index" : "")}");
    }
    if (indexBytes == null) throw new Exception("no index: give a shader-manifest.bin or an index.tsv(.br) as a source");
    if (serial == null) throw new Exception("which game: give --serial <title id> or a <serial>.manifest folder");
    string temp = outPath + ".tmp";
    using (var output = File.Create(temp))
    {
        output.Write("RYGTMAN1"u8);
        using var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.SmallestSize);
        using var w = new BinaryWriter(brotli);
        w.Write(records.Count + recipes.Count + 1);
        foreach (var (name, bytes) in records.Concat(recipes))
        {
            w.Write(serial + ".manifest/" + name);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
        w.Write("index.tsv");
        w.Write(indexBytes.Length);
        w.Write(indexBytes);
    }
    File.Move(temp, outPath, true);
    Console.WriteLine($"{serial}: {records.Count} records, {recipes.Count} recipe files ({string.Join(", ", recipes.Keys)}) and the index: {new FileInfo(outPath).Length / 1024} KB");
    return 0;
}

if (mode == "manifestprep")
{
    // FEAT-GTS-267 (from RyGTS FEAT-GTS-284): manifestprep <game> <update or "">
    // <shader-manifest.bin> <cache dir> [serial]: what the launcher's Set up does with the shipped
    // manifest (manifestpack). Writes its records and recipes into <cache dir>\<serial>.manifest
    // (records already there are kept; the shipped recipe files are replaced) and copies each
    // recorded program out of the player's own game files into <cache dir>\<serial>-programs
    // (programs already there are kept). The serial is the manifest's; given, a manifest made for
    // another game is refused (exit 2) and nothing is written.
    string game = args[1], update = args[2], cache = args[4];
    string wantedSerial = args.Length > 5 && args[5].Length > 0 ? args[5] : null;
    var bundle = GameShaders.ReadManifestBundle(args[3]).ToList();
    var serials = bundle.Select(e => GameShaders.ManifestSerial(e.name)).Where(s => s != null)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    string serial = wantedSerial ?? serials.FirstOrDefault();
    if (serial == null || (serials.Count > 0 && !serials.Contains(serial, StringComparer.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"{args[3]} is a shader manifest for {(serials.Count > 0 ? string.Join(", ", serials) : "no game")}, not {serial ?? "this game"}: nothing prepared");
        return 2;
    }
    string manifest = Path.Combine(cache, serial + ".manifest");
    Directory.CreateDirectory(manifest);
    var records = new List<string>();
    string[] index = Array.Empty<string>();
    int written = 0, recipes = 0;
    string prefix = serial + ".manifest/";
    foreach (var (name, bytes) in bundle)
    {
        if (name == "index.tsv")
        {
            index = System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            continue;
        }
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
        string file = Path.GetFileName(name);
        string target = Path.Combine(manifest, file);
        if (file.EndsWith(".rec"))
        {
            records.Add(file);
            if (File.Exists(target)) continue;
            File.WriteAllBytes(target + ".tmp", bytes);
            File.Move(target + ".tmp", target, true);
            written++;
        }
        else if (file.EndsWith(".rcp"))
        {
            File.WriteAllBytes(target + ".tmp", bytes);
            File.Move(target + ".tmp", target, true);
            recipes++;
        }
    }
    var idx = GameShaders.IndexFiles(game, update);
    var want = GameShaders.RecordPrograms(records);
    var (present, copied, files, seconds) = GameShaders.ExtractPrograms(idx, index, want, Path.Combine(cache, serial + "-programs"));
    Console.WriteLine($"{serial}: {written} of {records.Count} records and {recipes} recipe files written; " +
                      $"{copied} programs copied from the game files ({present} of {want.Count} there already, " +
                      $"{want.Count - present - copied} not found) in {seconds:F0} s");
    return 0;
}

if (mode == "extract")
{
    // extract <game folder> <update folder or ""> <out dir> <path>...: research helper.
    var idx = new List<string>();
    if (!string.IsNullOrEmpty(args[2])) idx.Add(Path.Combine(args[2], "gt.idx"));
    idx.Add(Path.Combine(args[1], "gt.idx"));
    var got = GameFiles.Extract(idx, args.Skip(4), args[3]);
    Console.WriteLine($"{got.Count} of {args.Length - 4} extracted");
    return 0;
}

if (mode == "all")
{
    // all <game folder> <update folder or ""> <output scripts folder>: extracts the five scripts from
    // the player's own game volumes, patches each (this program again, one mode each), and writes
    // them where the renderer (GTS-005) serves them from. The .ad sources sit next to this program.
    string game = args[1], update = args[2], output = args[3];
    string here = AppContext.BaseDirectory;
    var jobs = new (string mode, string path, string source)[]
    {
        ("uikit", "PROJECTS/GT7SP/UI_KIT/UI_KIT.ADC", "openGrim.ad"),
        ("livery2", "PROJECTS/GT7SP/LIVERY2/LIVERY2.ADC", null),
        ("thumbnail", "SCRIPTS/GT7SP/THUMBNAIL/THUMBNAIL.ADC", null),
        ("gallery", "PROJECTS/GT7SP/GALLERY/GALLERY.ADC", null),
        ("grim", "SCRIPTS/GT7SP/GRIM/GRIM.ADC", "grim.ad"),
    };
    string work = Path.Combine(Path.GetTempPath(), "rygt-scripts-" + Environment.ProcessId);
    Directory.CreateDirectory(work);
    var indices = new List<string>();
    if (!string.IsNullOrEmpty(update)) indices.Add(Path.Combine(update, "gt.idx"));
    indices.Add(Path.Combine(game, "gt.idx"));
    var found = GameFiles.Extract(indices, jobs.Select(j => j.path), work);
    Directory.CreateDirectory(output);
    int done = 0;
    foreach (var job in jobs)
    {
        string input = Path.Combine(work, job.path.Replace('/', Path.DirectorySeparatorChar));
        if (!found.Contains(job.path))
        {
            Console.WriteLine($"[X] {job.path} not found in the game files");
            continue;
        }
        string target = Path.Combine(output, Path.GetFileName(job.path));
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false };
        start.ArgumentList.Add(job.mode);
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(target);
        // The source's path goes into the compiled script: a bare name, as the tested scripts had.
        start.WorkingDirectory = here;
        if (job.source != null) start.ArgumentList.Add(job.source);
        using var process = System.Diagnostics.Process.Start(start);
        process.WaitForExit();
        if (process.ExitCode == 0 && File.Exists(target))
        {
            done++;
            Console.WriteLine($"[OK] {Path.GetFileName(job.path)}");
        }
        else
        {
            Console.WriteLine($"[X] {job.mode} failed (exit {process.ExitCode})");
        }
    }
    if (Environment.GetEnvironmentVariable("ADCPATCH_KEEP") != "1") { try { Directory.Delete(work, true); } catch { } } else Console.WriteLine($"originals kept in {work}");
    Console.WriteLine($"{done} of {jobs.Length} scripts patched into {output}");
    return done == jobs.Length ? 0 : 1;
}
if (mode == "roundtrip")
{
    var file = Load(args[1], out var map);
    Save(file, map, args[1], args[2]);
}
else if (mode == "uikit")
{
    // ProgressRoot.openGrim: offline, "Grim::requestLiveryUpload" is answered with a local id.
    var file = Load(args[1], out var map);
    var compiled = Compile(args[3]);
    var targets = Subroutines(file.TopLevelFrame.Instructions)
        .Where(x => x.sub.Name?.Name == "openGrim" && x.sub is InsMethodDefine).ToList();
    if (targets.Count != 1) throw new Exception($"found {targets.Count} openGrim methods");
    var module = targets[0].list; // ProgressRoot's body: the replacements are its methods
    foreach (var replacement in compiled.Instructions.OfType<InsModuleDefine>().Any()
                 ? Subroutines(compiled.Instructions).Where(x => x.sub is InsMethodDefine).Select(x => x.sub).ToList()
                 : new List<SubroutineBase>())
    {
        int index = module.FindIndex(x => x is InsMethodDefine m && m.Name?.Name == replacement.Name.Name);
        if (index < 0) throw new Exception($"ProgressRoot has no method {replacement.Name.Name}");
        RegisterSymbols(replacement, map, new HashSet<object>(ReferenceEqualityComparer.Instance));
        replacement.LineNumber = module[index].LineNumber;
        module[index] = replacement;
        Console.WriteLine($"replaced ProgressRoot.{replacement.Name.Name}");
    }
    Save(file, map, args[1], args[2]);
}
else if (mode == "livery2")
{
    // save(context): `if (CarEditTopRoot.is_online)` / `if (HelmetEditTopRoot.is_online)` before the
    // upload becomes `if (!false)`, so Save always takes the upload (and apply) path.
    var file = Load(args[1], out var map);
    var notSymbol = map.Symbols["__not__"];
    int patched = 0;
    foreach (var (_, _, sub) in Subroutines(file.TopLevelFrame.Instructions))
    {
        if (sub.Name?.Name != "save" || sub is not InsMethodDefine) continue;
        var ins = sub.CodeFrame.Instructions;
        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (ins[i] is InsVariableEvaluation v && v.Path.Count > 0 && v.Path[^1].Name.EndsWith("EditTopRoot") &&
                ins[i + 1] is InsAttributeEvaluation a && a.Path.Count == 1 && a.Path[0].Name == "is_online" &&
                ins[i + 2].InstructionType == AdhocInstructionType.JUMP_IF_FALSE)
            {
                var b = new InsBoolConst(false) { LineNumber = v.LineNumber };
                var n = new InsUnaryOperator(notSymbol) { LineNumber = a.LineNumber };
                ins[i] = b;
                ins[i + 1] = n;
                patched++;
            }
        }
    }
    Console.WriteLine($"save methods patched: {patched}");

    // alias_AddDecal.onActivate: `if (type == USER && !DecalEditorRoot.getParent().is_online) return 2;`
    // turns user decals away offline. `.is_online` + `!` become `POP` + `false`.
    int gates = 0;
    foreach (var (_, _, sub) in Subroutines(file.TopLevelFrame.Instructions))
    {
        if (sub.Name?.Name != "onActivate" || sub is not InsMethodDefine) continue;
        var ins = sub.CodeFrame.Instructions;
        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (ins[i] is InsAttributeEvaluation g && g.Path.Count == 1 && g.Path[0].Name == "getParent" &&
                ins[i + 2] is InsAttributeEvaluation a && a.Path.Count == 1 && a.Path[0].Name == "is_online" &&
                i + 3 < ins.Count && ins[i + 3] is InsUnaryOperator u && u.Operator.Name == "__not__" &&
                i > 0 && ins[i - 1] is InsVariableEvaluation v && v.Path[^1].Name == "DecalEditorRoot" &&
                i > 4 && ins[i - 2].InstructionType == AdhocInstructionType.LOGICAL_AND)
            {
                ins[i + 2] = new InsPop() { LineNumber = a.LineNumber };
                ins[i + 3] = new InsBoolConst(false) { LineNumber = u.LineNumber };
                gates++;
            }
        }
    }
    Console.WriteLine($"user decal gates patched: {gates}");
    Save(file, map, args[1], args[2]);
}
else if (mode == "thumbnail")
{
    // captureThumbnail: a composite thumbnail under 10 KB (32 KB at 2x) counts as a failed capture.
    // The livery report picture that the upload path captures comes out smaller here, so the size
    // floor is dropped: `file_size < 10 * 1024` becomes `file_size < 0 * 1024`.
    var file = Load(args[1], out var map);
    int patched = 0;
    foreach (var (_, _, sub) in Subroutines(file.TopLevelFrame.Instructions))
    {
        if (sub.Name?.Name != "captureThumbnail") continue;
        var ins = sub.CodeFrame.Instructions;
        for (int i = 1; i + 3 < ins.Count; i++)
        {
            if (ins[i] is InsIntConst k && (k.Value == 10 || k.Value == 32) &&
                ins[i - 1] is InsVariableEvaluation v && v.Path.Count == 1 && v.Path[0].Name == "file_size" &&
                ins[i + 1] is InsIntConst m && m.Value == 1024 &&
                ins[i + 3] is InsBinaryOperator op && op.Operator.Name == "__lt__")
            {
                k.Value = 0;
                patched++;
            }
        }
    }
    Console.WriteLine($"size checks patched: {patched}");
    Save(file, map, args[1], args[2]);
}
else if (mode == "gallery")
{
    // DecalStrategy.requestMyLibrary: the "My Decals" folder is listed only
    // `if (Player.login_util.isOnline() && is_online)`; it becomes `if (true)`.
    var file = Load(args[1], out var map);
    var top = file.TopLevelFrame.Instructions;
    int cls = top.FindIndex(x => x is InsClassDefine c && c.Name.Name == "DecalStrategy");
    int next = top.FindIndex(cls + 1, x => x is InsClassDefine);
    var method = (InsMethodDefine)top.Skip(cls).Take(next - cls).Single(x => x is InsMethodDefine m && m.Name?.Name == "requestMyLibrary");
    var ins = method.CodeFrame.Instructions;
    int at = ins.FindIndex(x => x is InsVariableEvaluation v && v.Path[^1].Name == "Player");
    if (at < 0 || ins[at + 4] is not InsLogicalAnd || ins[at + 5] is not InsVariableEvaluation iv || iv.Path[0].Name != "is_online" ||
        ins[at + 6] is not InsJumpIfFalse)
        throw new Exception("requestMyLibrary is not as expected");
    ins[at] = new InsBoolConst(true) { LineNumber = ins[at].LineNumber };
    RemoveRange(ins, at + 1, 5);
    Console.WriteLine("requestMyLibrary: user decal folder always listed");
    Save(file, map, args[1], args[2]);
}
else if (mode == "grim")
{
    // From grim.ad: GrimDecal.requestDecalList (the decal list comes from local SVG files) and
    // GrimLivery's ban checks (no server to ask; the garage loads liveries without them).
    var file = Load(args[1], out var map);
    var compiled = Compile(args[3]);
    var top = file.TopLevelFrame.Instructions;
    var jobs = new[] { ("GrimDecal", "requestDecalList"), ("GrimLivery", "liveryTest"), ("GrimLivery", "requestLiveryTest") };
    var modules = new HashSet<string>();
    foreach (var (module, name) in jobs)
    {
        var replacement = Subroutines(compiled.Instructions).Select(x => x.sub).Single(x => x is InsFunctionDefine f && f.Name.Name == name);
        int mod = top.FindIndex(x => x is InsModuleDefine m && m.Path[^1].Name == module);
        int index = mod < 0 ? -1 : top.FindIndex(mod, x => x is InsFunctionDefine f && f.Name.Name == name);
        if (index < 0) throw new Exception($"{module}.{name} not found");
        RegisterSymbols(replacement, map, new HashSet<object>(ReferenceEqualityComparer.Instance));
        replacement.LineNumber = top[index].LineNumber;
        top[index] = replacement;
        modules.Add(module);
        Console.WriteLine($"replaced {module}.{name}");
    }
    // room for them: GrimDecal's own debug asserts and logs (GrimLivery's replacements are smaller
    // than the originals)
    int stripped = 0;
    foreach (var module in new[] { "GrimDecal" })
    {
        int mod = top.FindIndex(x => x is InsModuleDefine m && m.Path[^1].Name == module);
        int nextModule = top.FindIndex(mod + 1, x => x is InsModuleDefine);
        foreach (var (_, _, sub) in Subroutines(top.GetRange(mod, (nextModule < 0 ? top.Count : nextModule) - mod)).ToList())
            stripped += StripDebug(sub.CodeFrame.Instructions);
    }
    Console.WriteLine($"debug asserts/logs removed: {stripped}");
    PruneSymbols(file, map);
    Save(file, map, args[1], args[2]);
}

return 0;
