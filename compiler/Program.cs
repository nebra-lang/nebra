using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.LPS;
using Nebra.PackageManager;
using Nebra.Runtime;

namespace Nebra;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
#if DEBUG
        if (args.Length > 0 && args[0] == "--test")
        {
            var testFile = Path.Combine(Environment.CurrentDirectory, "..", "..", "test.neb");
            return await RunBuildFilesAsync([testFile]);
        }
#endif

        if (Trampoline.TryForward(args) is { } forwarded)
            return forwarded;

        Upgrader.CleanupStaleBackup();

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        var exit = command switch
        {
            "build" => await RunBuildAsync(args.Skip(1).ToArray()),
            "watch" => await RunWatchAsync(args.Skip(1).ToArray()),
            "run" => await RunExecuteAsync(args.Skip(1).ToArray()),
            "lps" => await RunLpsAsync(),
            "init" => RunInit(),
            "migrate" => Migrator.Run(args.Skip(1).ToArray()),
            "create" => await RunCreateAsync(args.Skip(1).ToArray()),
            "install" => await RunInstallAsync(args.Skip(1).ToArray()),
            "add" => await RunAddAsync(args.Skip(1).ToArray()),
            "remove" or "rm" => await RunRemoveAsync(args.Skip(1).ToArray()),
            "registry" => await RunRegistryAsync(args.Skip(1).ToArray()),
            "pm" => await RunPmAsync(args.Skip(1).ToArray()),
            "docs" => await RunDocsAsync(args.Skip(1).ToArray()),
            "test" => await RunTestAsync(args.Skip(1).ToArray()),
            "compile" => await RunCompileAsync(args.Skip(1).ToArray()),
            "repl" => await RunReplAsync(args.Skip(1).ToArray()),
            "upgrade" => await Upgrader.RunUpgradeAsync(args.Skip(1).ToArray()),
            "check" => await Upgrader.RunCheckAsync(),
            "_upgrade_apply" => await Upgrader.RunDeferredApplyAsync(args.Skip(1).ToArray()),
            "login" => await GitHubLogin.LoginAsync(),
            "logout" => GitHubLogin.Logout(),
            "whoami" => await GitHubLogin.WhoAmIAsync(),
            "publish" => await RunPublishAsync(args.Skip(1).ToArray()),
            "unpublish" => await RunUnpublishAsync(args.Skip(1).ToArray()),
            "scope" => await RunScopeAsync(args.Skip(1).ToArray()),
            "use" => Trampoline.RunUse(args.Skip(1).ToArray()),
            "unuse" => Trampoline.RunUnuse(),
            "version" => RunVersion(),
            "help" or "--help" or "-h" => RunHelp(),
            _ => RunUnknown(command)
        };

        if (command is "version" or "help" or "--help" or "-h")
            await Upgrader.MaybeShowUpdateNoticeAsync();

        return exit;
    }

    private static async Task<int> RunBuildAsync(string[] args)
    {
        var fileArgs = args.Where(argument => argument != "--no-clean").ToArray();
        if (fileArgs.Length > 0)
            return await RunBuildFilesAsync(fileArgs);

        return await RunBuildProjectAsync(cleanOutput: fileArgs.Length == args.Length);
    }

    /// <summary>
    /// Loads the project's <c>nebra.toml</c>, or the defaults when there is none. A file that
    /// cannot be read, such as one naming an unknown target, has already been reported and stops
    /// the command rather than letting it run on defaults the project never asked for.
    /// </summary>
    private static bool TryLoadProjectConfig(string configPath, out Config config)
    {
        if (!File.Exists(configPath))
        {
            config = new Config();
            return true;
        }

        config = Config.LoadFromFile(configPath)!;
        return config != null;
    }

    private static async Task<int> RunBuildProjectAsync(bool cleanOutput)
    {
        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        if (!TryLoadProjectConfig(configPath, out var config)) return 1;
        return await CompileProjectAsync(config, runScripts: true, cleanOutput);
    }

    /// <summary>
    /// Compiles the current project (every <c>*.neb</c> file under <c>config.Source</c>)
    /// and writes the generated Lua to <c>config.Output</c>. Shared by <c>nebra build</c>
    /// (with pre/post-build scripts) and <c>nebra watch</c> (scripts skipped — a watch loop
    /// should only recompile, not re-run side-effecting build hooks on every save).
    /// </summary>
    private static async Task CleanOutputAsync(Config config, DateTime buildStart)
    {
        var refusal = OutputCleaner.Clean(config, Environment.CurrentDirectory, buildStart);
        if (refusal != null)
        {
            await Console.Error.WriteLineAsync($"Warning: not clearing the output directory '{config.Output}': {refusal}.");
        }
    }

    private static async Task<int> CompileProjectAsync(Config config, bool runScripts, bool cleanOutput = false)
    {
        var buildStart = DateTime.UtcNow;

        if (config.TypesOnly)
        {
            return await CheckTypesOnlyProjectAsync(config);
        }

        var srcDir = Path.Combine(Environment.CurrentDirectory, config.Source);
        if (!Directory.Exists(srcDir))
        {
            await Console.Error.WriteLineAsync($"Source directory '{config.Source}' not found.");
            return 1;
        }

        var outDir = Path.Combine(Environment.CurrentDirectory, config.Output);

        if (runScripts && !RunScripts(config.Scripts.PreBuild, "pre-build"))
            return 1;

        var sourceFiles = ProjectFiles.Files(srcDir, "*.neb")
            .Where(f => !f.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            var hasDecls = ProjectFiles.Files(srcDir, "*.d.neb").Any();
            if (hasDecls)
            {
                await Console.Error.WriteLineAsync(
                    $"No .neb files found in '{config.Source}', only declarations. " +
                    "Set `types_only = true` in nebra.toml if this is a types-only package.");
            }
            else
            {
                await Console.Error.WriteLineAsync($"No .neb files found in '{config.Source}'.");
            }
            return 1;
        }

        var compiler = new NebraCompiler { Config = config };
        foreach (var file in sourceFiles)
            compiler.AddSource(file);

        var success = compiler.Compile();
        await PrintDiagnosticsAsync(compiler);
        if (!success)
        {
            await Console.Error.WriteLineAsync("Build FAILED.");
            return 1;
        }

        if (cleanOutput) await CleanOutputAsync(config, buildStart);

        Directory.CreateDirectory(outDir);
        await WriteOutputFilesAsync(compiler, srcDir, outDir, config);

        Console.WriteLine($"Build SUCCESS — {sourceFiles.Length} file(s) compiled to '{config.Output}/'.");

        if (runScripts && !RunScripts(config.Scripts.PostBuild, "post-build"))
            return 1;

        return 0;
    }

    /// <summary>
    /// Type-checks a types-only package. Such a package emits no Lua, but its declarations still
    /// have to resolve: without this the errors stayed invisible until a consumer hit them, and a
    /// package could publish declarations naming types that do not exist.
    /// </summary>
    private static async Task<int> CheckTypesOnlyProjectAsync(Config config)
    {
        var srcDir = Path.Combine(Environment.CurrentDirectory, config.Source);
        if (!Directory.Exists(srcDir))
        {
            await Console.Error.WriteLineAsync($"Source directory '{config.Source}' not found.");
            return 1;
        }

        var declarationFiles = ProjectFiles.Files(srcDir, "*.d.neb").ToArray();

        if (declarationFiles.Length == 0)
        {
            await Console.Error.WriteLineAsync($"No .d.neb files found in '{config.Source}'.");
            return 1;
        }

        var compiler = new NebraCompiler { Config = config };
        foreach (var file in declarationFiles)
            compiler.AddSource(file);

        var checkedTypes = compiler.Compile();
        await PrintDiagnosticsAsync(compiler);
        if (!checkedTypes)
        {
            await Console.Error.WriteLineAsync("Check FAILED.");
            return 1;
        }

        Console.WriteLine($"Types-only project — {declarationFiles.Length} declaration file(s) checked.");
        return 0;
    }

    /// <summary>
    /// Watches <c>config.Source</c> recursively and recompiles the project whenever a
    /// <c>*.neb</c> file changes (debounced). Runs until the user presses Ctrl+C.
    /// </summary>
    private static async Task<int> RunWatchAsync(string[] args)
    {
        var debounceMs = 300;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--debounce" && i + 1 < args.Length && int.TryParse(args[i + 1], out var ms) && ms >= 0)
            {
                debounceMs = ms;
                i++;
            }
            else
            {
                await Console.Error.WriteLineAsync($"error: unexpected argument '{args[i]}'. Usage: nebra watch [--debounce <ms>]");
                return 1;
            }
        }

        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        if (!File.Exists(configPath))
        {
            await Console.Error.WriteLineAsync("error: nebra.toml not found in current directory. Run 'nebra init' first.");
            return 1;
        }

        if (!TryLoadProjectConfig(configPath, out var initialConfig)) return 1;
        if (initialConfig.TypesOnly)
        {
            Console.WriteLine("Types-only project — nothing to watch.");
            return 0;
        }

        var srcDir = Path.Combine(Environment.CurrentDirectory, initialConfig.Source);
        if (!Directory.Exists(srcDir))
        {
            await Console.Error.WriteLineAsync($"Source directory '{initialConfig.Source}' not found.");
            return 1;
        }

        // A rebuild reloads nebra.toml so config edits (e.g. output path) are picked up,
        // and uses a fresh compiler each time since the compiler accumulates state.
        async Task RebuildAsync(string reason)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            Console.WriteLine($"\n[{stamp}] {reason} — rebuilding…");
            try
            {
                if (TryLoadProjectConfig(configPath, out var config))
                    await CompileProjectAsync(config, runScripts: false);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Build FAILED: {ex.Message}");
            }
            Console.WriteLine("Watching for changes… (Ctrl+C to stop)");
        }

        Console.WriteLine($"nebra watch — watching '{initialConfig.Source}/' for *.neb changes.");
        await RebuildAsync("initial build");

        using var debouncer = new Debouncer(TimeSpan.FromMilliseconds(debounceMs), RebuildAsync);
        using var watcher = new FileSystemWatcher(srcDir)
        {
            IncludeSubdirectories = true,
            Filter = "*.neb",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                           NotifyFilters.DirectoryName | NotifyFilters.Size,
        };

        void OnChanged(object? _, FileSystemEventArgs e) =>
            debouncer.Trigger($"{Path.GetFileName(e.Name ?? e.FullPath)} {e.ChangeType.ToString().ToLowerInvariant()}");
        void OnRenamed(object? _, RenamedEventArgs e) =>
            debouncer.Trigger($"{Path.GetFileName(e.Name ?? e.FullPath)} renamed");

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += (_, e) =>
            Console.Error.WriteLine($"watch error: {e.GetException().Message}");
        watcher.EnableRaisingEvents = true;

        // RunContinuationsAsynchronously: TrySetResult is invoked from a POSIX signal
        // handler thread, and we must not run the (disposing) continuation inline there.
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        Console.CancelKeyPress += onCancel;

        // Console.CancelKeyPress alone is unreliable when stdout is redirected or the
        // process has no controlling terminal (CI, `nebra watch > log &`). Register the
        // POSIX signals directly so Ctrl+C / SIGTERM always stop the loop cleanly.
        var signals = new List<PosixSignalRegistration>();
        if (!OperatingSystem.IsWindows())
        {
            foreach (var sig in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
            {
                signals.Add(PosixSignalRegistration.Create(sig, ctx =>
                {
                    ctx.Cancel = true;
                    stop.TrySetResult();
                }));
            }
        }

        try
        {
            await stop.Task;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            foreach (var reg in signals) reg.Dispose();
        }

        Console.WriteLine("\nnebra watch stopped.");
        return 0;
    }

    /// <summary>
    /// Collapses a burst of file-system events into a single action that fires once the
    /// stream goes quiet for <c>delay</c>. Re-entrancy safe: events that arrive while the
    /// action is running schedule exactly one more run afterwards.
    /// </summary>
    private sealed class Debouncer(TimeSpan delay, Func<string, Task> action) : IDisposable
    {
        private readonly Lock _gate = new();
        private Timer? _timer;
        private bool _running;
        private string? _pendingReason;
        private string? _lastReason;

        public void Trigger(string reason)
        {
            lock (_gate)
            {
                _lastReason = reason;
                _timer?.Dispose();
                _timer = new Timer(_ => Fire(), null, delay, Timeout.InfiniteTimeSpan);
            }
        }

        private async void Fire()
        {
            string reason;
            lock (_gate)
            {
                if (_running)
                {
                    _pendingReason = _lastReason;
                    return;
                }
                _running = true;
                reason = _lastReason ?? "change detected";
            }

            try
            {
                await action(reason);
            }
            catch
            {
                // action is expected to handle its own errors; swallow to keep the
                // timer thread alive across a failed rebuild.
            }
            finally
            {
                string? again;
                lock (_gate)
                {
                    _running = false;
                    again = _pendingReason;
                    _pendingReason = null;
                }
                if (again != null) Trigger(again);
            }
        }

        public void Dispose()
        {
            lock (_gate) _timer?.Dispose();
        }
    }

    /// <summary>
    /// Prints every diagnostic of a compilation, warnings of a successful one included.
    /// </summary>
    private static async Task PrintDiagnosticsAsync(NebraCompiler compiler)
    {
        foreach (var diag in compiler.Diagnostics.Diagnostics)
            await Console.Error.WriteLineAsync(Nebra.Diagnostics.DiagnosticRenderer.Render(diag) + "\n");
    }

    private static async Task<int> RunBuildFilesAsync(string[] fileArgs)
    {
        Config? config = null;
        var nebraFiles = new List<string>();

        foreach (var arg in fileArgs)
        {
            var fullPath = Path.GetFullPath(arg);
            if (fullPath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ||
                fullPath.EndsWith("nebra.toml", StringComparison.OrdinalIgnoreCase))
            {
                config = Config.LoadFromFile(fullPath);
            }
            else if (fullPath.EndsWith(".neb", StringComparison.OrdinalIgnoreCase) &&
                     !fullPath.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(fullPath))
                    nebraFiles.Add(fullPath);
                else
                    await Console.Error.WriteLineAsync($"File not found: {arg}");
            }
            else
            {
                await Console.Error.WriteLineAsync($"Unknown file type: {arg}");
            }
        }

        config ??= new Config();

        if (nebraFiles.Count == 0)
        {
            await Console.Error.WriteLineAsync("No .neb files specified.");
            return 1;
        }

        var compiler = new NebraCompiler { Config = config };
        foreach (var file in nebraFiles)
            compiler.AddSource(file);

        var success = compiler.Compile();
        await PrintDiagnosticsAsync(compiler);
        if (!success)
        {
            await Console.Error.WriteLineAsync("Build FAILED.");
            return 1;
        }

        var outDir = Path.Combine(Environment.CurrentDirectory, config.Output);
        Directory.CreateDirectory(outDir);

        var baseDir = nebraFiles.Count == 1
            ? Path.GetDirectoryName(nebraFiles[0])!
            : FindCommonParent(nebraFiles);

        await WriteOutputFilesAsync(compiler, baseDir, outDir, config);

        Console.WriteLine($"Build SUCCESS — {nebraFiles.Count} file(s) compiled to '{config.Output}/'.");
        return 0;
    }

    /// <summary>
    /// Writes the generated Lua for every file this project owns. Files pulled in from outside
    /// <paramref name="baseDir"/> - a dependency's sources under <c>nebra_modules/</c>, a path
    /// from <c>[code] libs</c> - are compiled into the type universe but never written: their
    /// relative path would escape <paramref name="outDir"/> and land back in the dependency,
    /// overwriting the output that dependency built under its own configuration.
    /// </summary>
    private static async Task WriteOutputFilesAsync(NebraCompiler compiler, string baseDir, string outDir, Config config)
    {
        var fullBaseDir = Path.GetFullPath(baseDir);

        foreach (var pkg in compiler.Packages.Values)
        {
            foreach (var file in pkg.Files)
            {
                // A file whose generated chunk is empty is still written: a module holding only
                // types compiles to nothing, and importers emit a `require` for it regardless, so
                // omitting the file turns the import into a load-time failure.
                if (file.GeneratedLua == null || file.IsDeclarationFile) continue;
                if (file.Filename != null && !IsUnderDirectory(file.Filename, fullBaseDir)) continue;

                var relativePath = Path.GetRelativePath(baseDir, file.Filename ?? "output.lua");
                var outputPath = Path.Combine(outDir, Path.ChangeExtension(relativePath, ".lua"));

                var outputFileDir = Path.GetDirectoryName(outputPath);
                if (outputFileDir != null) Directory.CreateDirectory(outputFileDir);

                await File.WriteAllTextAsync(outputPath, file.GeneratedLua);
                if (file.GeneratedLineSources != null && file.Filename != null)
                {
                    await File.WriteAllTextAsync(outputPath + ".map", SourceMapFile.Render(outputPath, file.Filename, file.GeneratedLineSources));
                }
            }
        }

        if (compiler.Cache.TryGetValue("GeneratedDeclarations", out var declObj) && declObj is string declContent)
        {
            var declName = config.Name ?? "index";
            var declPath = Path.Combine(outDir, declName + ".d.neb");
            await File.WriteAllTextAsync(declPath, declContent);
        }
        
        await CopyAssetsAsync(outDir, config);
    }

    /// <summary>
    /// Reports whether <paramref name="path"/> sits inside <paramref name="fullRoot"/>, which is
    /// expected to be an absolute path already.
    /// </summary>
    private static bool IsUnderDirectory(string path, string fullRoot)
    {
        var full = Path.GetFullPath(path);
        if (full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return true;

        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyAssetsAsync(string outDir, Config config)
    {
        if (config.Assets.Count == 0) return;
        Console.WriteLine($"Copying assets from '{config.Name}' to '{outDir}'.");

        // Asset paths are resolved relative to the PROJECT ROOT (where
        // `nebra.toml` lives, i.e. the current working directory at build time),
        // not the common parent of source files. This lets manifests like
        // `Package.toml` sit next to `nebra.toml` outside `src/` and still be
        // copied into the output.
        var projectRoot = Environment.CurrentDirectory;

        foreach (var (srcRelPath, destRelPath) in config.Assets)
        {
            var srcPath = Path.Combine(projectRoot, srcRelPath);
            var destPath = Path.Combine(outDir, destRelPath);

            if (Directory.Exists(srcPath))
            {
                CopyDirectoryRecursive(srcPath, destPath);
                continue;
            }

            if (!File.Exists(srcPath))
            {
                await Console.Error.WriteLineAsync($"File not found: {srcRelPath}! Skipping.");
                continue;
            }

            var parentDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            File.Copy(srcPath, destPath, true);
        }
    }

    /// <summary>
    /// Mirrors <paramref name="src"/> into <paramref name="dest"/>, creating
    /// destination directories as needed and overwriting existing files. Used
    /// by <see cref="CopyAssetsAsync"/> when an asset entry points at a
    /// directory (e.g. a UI bundle) rather than a single file.
    /// </summary>
    private static void CopyDirectoryRecursive(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(dest, name), true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            CopyDirectoryRecursive(dir, Path.Combine(dest, name));
        }
    }

    private static string FindCommonParent(List<string> paths)
    {
        if (paths.Count == 0) return Environment.CurrentDirectory;
        var dirs = paths.Select(p => Path.GetDirectoryName(p) ?? "").ToList();
        var common = dirs[0];
        foreach (var dir in dirs.Skip(1))
        {
            while (!dir.StartsWith(common, StringComparison.OrdinalIgnoreCase) && common.Length > 0)
            {
                common = Path.GetDirectoryName(common) ?? "";
            }
        }
        return string.IsNullOrEmpty(common) ? Environment.CurrentDirectory : common;
    }

    private static async Task<int> RunExecuteAsync(string[] fileArgs)
    {
        Config? config = null;
        var nebraFiles = new List<string>();
        var passThroughArgs = new List<string>();
        var parsingNebraArgs = true;

        foreach (var arg in fileArgs)
        {
            if (!parsingNebraArgs)
            {
                passThroughArgs.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                parsingNebraArgs = false;
                continue;
            }

            var fullPath = Path.GetFullPath(arg);
            if (fullPath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ||
                fullPath.EndsWith("nebra.toml", StringComparison.OrdinalIgnoreCase))
            {
                config = Config.LoadFromFile(fullPath);
            }
            else if (fullPath.EndsWith(".neb", StringComparison.OrdinalIgnoreCase) &&
                     !fullPath.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(fullPath)) nebraFiles.Add(fullPath);
                else await Console.Error.WriteLineAsync($"File not found: {arg}");
            }
            else
            {
                passThroughArgs.Add(arg);
            }
        }

        var projectMode = nebraFiles.Count == 0;
        if (projectMode)
        {
            var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
            if (config == null && !TryLoadProjectConfig(configPath, out config)) return 1;

            if (config.TypesOnly)
            {
                await Console.Error.WriteLineAsync("error: cannot run a types-only project (no executable code).");
                return 1;
            }

            var srcDir = Path.Combine(Environment.CurrentDirectory, config.Source);
            if (!Directory.Exists(srcDir))
            {
                await Console.Error.WriteLineAsync($"Source directory '{config.Source}' not found.");
                return 1;
            }

            nebraFiles.AddRange(ProjectFiles.Files(srcDir, "*.neb")
                .Where(f => !f.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase)));

            if (nebraFiles.Count == 0)
            {
                await Console.Error.WriteLineAsync($"No .neb files found in '{config.Source}'.");
                return 1;
            }
        }
        else
        {
            config ??= new Config();
        }

        var compiler = new NebraCompiler { Config = config };
        foreach (var file in nebraFiles)
            compiler.AddSource(file);

        var success = compiler.Compile();
        await PrintDiagnosticsAsync(compiler);
        if (!success)
        {
            await Console.Error.WriteLineAsync("Build FAILED.");
            return 1;
        }

        var baseDir = projectMode
            ? Path.Combine(Environment.CurrentDirectory, config.Source)
            : (nebraFiles.Count == 1 ? Path.GetDirectoryName(nebraFiles[0])! : FindCommonParent(nebraFiles));

        var outDir = projectMode
            ? Path.Combine(Environment.CurrentDirectory, config.Output)
            : Path.Combine(Path.GetTempPath(), "nebra-run-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(outDir);
        await WriteOutputFilesAsync(compiler, baseDir, outDir, config);

        string? entryPath;
        if (!string.IsNullOrEmpty(config.Entry))
        {
            string rel;
            if (config.Entry.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                rel = config.Entry;
            else if (config.Entry.EndsWith(".neb", StringComparison.OrdinalIgnoreCase))
                rel = config.Entry[..^4] + ".lua";
            else
                rel = config.Entry + ".lua";
            var candidate = Path.Combine(outDir, rel);
            if (File.Exists(candidate)) entryPath = candidate;
            else
            {
                await Console.Error.WriteLineAsync($"nebra run: entry '{config.Entry}' not found in '{outDir}'.");
                return 1;
            }
        }
        else
        {
            entryPath = ResolveDefaultEntry(outDir, baseDir, nebraFiles);
            if (entryPath == null)
            {
                await Console.Error.WriteLineAsync(
                    "nebra run: could not determine entry file. Set [entry] in nebra.toml, " +
                    "or add a 'main.neb' / 'index.neb' to your source root.");
                return 1;
            }
        }

        using var runtime = new NebraRuntime();
        runtime.AddPackagePath(outDir);

        var modulesDir = Path.Combine(Environment.CurrentDirectory, "nebra_modules");
        if (Directory.Exists(modulesDir))
            runtime.AddPackagePath(modulesDir);

        PushCommandLineArgs(runtime, passThroughArgs);

        var ok = runtime.RunFile(entryPath);

        if (!projectMode)
        {
            try { Directory.Delete(outDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        return ok ? 0 : 1;
    }

    private static string? ResolveDefaultEntry(string outDir, string baseDir, List<string> nebraFiles)
    {
        foreach (var candidate in new[] { "main.lua", "index.lua", "init.lua" })
        {
            var path = Path.Combine(outDir, candidate);
            if (File.Exists(path)) return path;
        }

        if (nebraFiles.Count == 1)
        {
            var rel = Path.GetRelativePath(baseDir, nebraFiles[0]);
            var lua = Path.Combine(outDir, Path.ChangeExtension(rel, ".lua"));
            if (File.Exists(lua)) return lua;
        }

        return null;
    }

    private static void PushCommandLineArgs(NebraRuntime runtime, List<string> args)
    {
        if (args.Count == 0) return;
        var escaped = string.Join(", ", args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
        runtime.RunChunk($"arg = {{ {escaped} }}", "<nebra-run-args>");
    }

    private static async Task<int> RunLpsAsync()
    {
        await NebraLanguageServer.RunAsync();
        return 0;
    }

    private static int RunInit()
    {
        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        if (File.Exists(configPath))
        {
            Console.Error.WriteLine("nebra.toml already exists in the current directory.");
            return 1;
        }

        var defaults = new Config();
        var projectName = Path.GetFileName(Environment.CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(projectName)) projectName = "myproject";
        projectName = projectName.Replace("\\", "\\\\").Replace("\"", "\\\"");

        var toml = $"""
            name = "{projectName}"
            version = "0.1.0"
            target = "5.4"

            """;
        File.WriteAllText(configPath, toml);

        Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, defaults.Source));
        Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, defaults.Output));

        var gitignorePath = Path.Combine(Environment.CurrentDirectory, ".gitignore");
        var entries = new[] { $"{defaults.Output}/", "nebra_modules/" };
        if (!File.Exists(gitignorePath))
        {
            File.WriteAllText(gitignorePath, string.Join('\n', entries) + "\n");
        }
        else
        {
            var content = File.ReadAllText(gitignorePath);
            var toAppend = entries.Where(e => !content.Contains(e)).ToArray();
            if (toAppend.Length > 0)
            {
                if (!content.EndsWith('\n')) content += "\n";
                content += string.Join('\n', toAppend) + "\n";
                File.WriteAllText(gitignorePath, content);
            }
        }

        Console.WriteLine("Initialized Nebra project:");
        Console.WriteLine($"  nebra.toml");
        Console.WriteLine($"  {defaults.Source}/");
        Console.WriteLine($"  {defaults.Output}/");
        Console.WriteLine($"  .gitignore");
        return 0;
    }

    private static int RunVersion()
    {
        Console.WriteLine($"nebra {GetNebraVersion()}");
        return 0;
    }

    internal static string GetNebraVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static int RunHelp()
    {
        Console.WriteLine("Usage: nebra <command> [args]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  build              Compile the project (reads nebra.toml)");
        Console.WriteLine("                     Flags: --no-clean (keep files from earlier builds)");
        Console.WriteLine("  build <files...>   Compile specific .neb files (optional nebra.toml)");
        Console.WriteLine("  watch              Recompile the project whenever a *.neb file changes");
        Console.WriteLine("                     Flags: --debounce <ms> (default 300)");
        Console.WriteLine("  run                Compile and execute the project via embedded Lua");
        Console.WriteLine("  run <files...>     Compile and execute specific .neb files");
        Console.WriteLine("  init               Create a new Nebra project in the current directory");
        Console.WriteLine("  migrate [dir]      Convert a project from the old 'lux' toolchain to Nebra");
        Console.WriteLine("                     Flags: --dry-run, --no-backup");
        Console.WriteLine("  create <spec>      Scaffold a new project from a template (git spec, setup URL, local setup script or directory)");
        Console.WriteLine("                     Flags: [dir], --skip-setup, --offline, --no-cache");
        Console.WriteLine("  install            Install declared dependencies into nebra_modules/");
        Console.WriteLine("  add <spec>         Add a dependency (e.g. github:owner/repo@v1)");
        Console.WriteLine("                     Monorepo: github:owner/repo/path/to/pkg@v1 picks one package");
        Console.WriteLine("                     Flags: --dev, --peer");
        Console.WriteLine("  remove <name>      Remove a declared dependency");
        Console.WriteLine("  pm prune [spec]    Wipe cached bare clones / snapshots so next install re-fetches");
        Console.WriteLine("  pm update [name]   Re-resolve every (or one named) dependency against origin");
        Console.WriteLine("  pm refresh-registry  Refresh the cached alias registry index");
        Console.WriteLine("  docs               Generate documentation site (Markdown + HTML)");
        Console.WriteLine("                     Flags: --out <dir>, --no-html, --no-md");
        Console.WriteLine("  test [filter]      Discover and run unit/integration tests");
        Console.WriteLine("                     Flags: --quiet (suppress per-test ticks)");
        Console.WriteLine("  compile            Bundle the project into a standalone native binary");
        Console.WriteLine("                     Flags: --out <path>, --name <appname>, --target <rid>,");
        Console.WriteLine("                            --aot (experimental), --keep-build");
        Console.WriteLine("  repl               Start an interactive Nebra session (Ctrl+D or :quit to exit)");
        Console.WriteLine("  lps                Start the language server (LSP via stdio)");
        Console.WriteLine("  check              Check whether a newer Nebra release is available");
        Console.WriteLine("  upgrade            Download and install the latest Nebra release");
        Console.WriteLine("                     Flags: --force (reinstall even if already up to date)");
        Console.WriteLine("  login              Log in to GitHub to publish packages (device flow in the browser)");
        Console.WriteLine("  logout             Forget the GitHub login");
        Console.WriteLine("  whoami             Show the GitHub account you are logged in as");
        Console.WriteLine("  publish            Publish the project to the package registry as @scope/name");
        Console.WriteLine("                     Flags: --scope <scope> (default: your GitHub account), --description <text>");
        Console.WriteLine("  unpublish [pkg]    Remove a package from the registry (default: the current project)");
        Console.WriteLine("  scope add-owner <scope> <user>     Let another GitHub account publish to a scope");
        Console.WriteLine("  scope remove-owner <scope> <user>  Take that right away again");
        Console.WriteLine("  use [path]         Forward every nebra call to another build, such as a local");
        Console.WriteLine("                     nebra executable, nebra.dll or its directory; shows the current one");
        Console.WriteLine("  unuse              Go back to the installed version");
        Console.WriteLine("  version            Print the Nebra version");
        Console.WriteLine("  help               Show this help message");
        return 0;
    }

    private static async Task<int> RunDocsAsync(string[] args)
    {
        var outDir = "docs";
        var emitHtml = true;
        var emitMd = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--no-html":
                    emitHtml = false;
                    break;
                case "--no-md":
                    emitMd = false;
                    break;
            }
        }

        if (!emitHtml && !emitMd)
        {
            await Console.Error.WriteLineAsync("error: nothing to emit (both --no-md and --no-html given).");
            return 1;
        }

        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        if (!TryLoadProjectConfig(configPath, out var config)) return 1;

        var site = Doc.DocSiteBuilder.Build(config, Environment.CurrentDirectory, out var diagnostics);
        foreach (var d in diagnostics) await Console.Error.WriteLineAsync(d);

        var fullOut = Path.IsPathRooted(outDir) ? outDir : Path.Combine(Environment.CurrentDirectory, outDir);
        Directory.CreateDirectory(fullOut);

        if (emitMd)
        {
            var mdRenderer = new Doc.MarkdownDocRenderer();
            mdRenderer.WriteTo(site, fullOut);
        }

        if (emitHtml)
        {
            var htmlRenderer = new Doc.HtmlDocRenderer();
            htmlRenderer.WriteTo(site, fullOut);
        }

        Console.WriteLine($"Generated docs for {site.Modules.Count} module(s) in '{outDir}'.");
        return 0;
    }

    private static async Task<int> RunTestAsync(string[] args)
    {
        string? filter = null;
        var quiet = false;
        var explicitFiles = new List<string>();

        foreach (var a in args)
        {
            switch (a)
            {
                case "--quiet":
                case "-q":
                    quiet = true;
                    break;
                default:
                    if (a.StartsWith("--"))
                    {
                        await Console.Error.WriteLineAsync($"error: unknown flag '{a}'");
                        return 1;
                    }
                    var path = Path.GetFullPath(a);
                    if (File.Exists(path) && path.EndsWith(".neb", StringComparison.OrdinalIgnoreCase)
                        && !path.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
                        explicitFiles.Add(path);
                    else if (filter == null)
                        filter = a;
                    else
                    {
                        await Console.Error.WriteLineAsync($"error: unexpected argument '{a}'");
                        return 1;
                    }
                    break;
            }
        }

        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        if (!TryLoadProjectConfig(configPath, out var config)) return 1;
        quiet = quiet || config.Test.Quiet;

        if (config.TypesOnly && explicitFiles.Count == 0)
        {
            Console.WriteLine("Types-only project — no tests to run.");
            return 0;
        }

        var srcDir = Path.Combine(Environment.CurrentDirectory, config.Source);
        var sourceFiles = Directory.Exists(srcDir)
            ? ProjectFiles.Files(srcDir, "*.neb")
                .Where(f => !f.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        var testFiles = explicitFiles.Count > 0
            ? explicitFiles
            : DiscoverTestFiles(config, sourceFiles);

        if (testFiles.Count == 0)
        {
            await Console.Error.WriteLineAsync(
                $"nebra test: no test files found. Searched dirs: {string.Join(", ", config.Test.Dirs)}; " +
                $"patterns: {string.Join(", ", config.Test.Patterns)}.");
            return 1;
        }

        var allNebraFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in sourceFiles) allNebraFiles.Add(f);
        foreach (var f in testFiles) allNebraFiles.Add(f);

        var compiler = new NebraCompiler { Config = config };
        foreach (var file in allNebraFiles) compiler.AddSource(file);

        var success = compiler.Compile();
        await PrintDiagnosticsAsync(compiler);
        if (!success)
        {
            await Console.Error.WriteLineAsync("nebra test: compilation FAILED.");
            return 1;
        }

        var outDir = Path.Combine(Path.GetTempPath(), "nebra-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        var baseDir = FindCommonParent([.. allNebraFiles]);
        await WriteOutputFilesAsync(compiler, baseDir, outDir, config);

        var compiledPaths = new List<string>();
        foreach (var nebraPath in testFiles)
        {
            var rel = Path.GetRelativePath(baseDir, nebraPath);
            var luaPath = Path.Combine(outDir, Path.ChangeExtension(rel, ".lua"));
            if (File.Exists(luaPath)) compiledPaths.Add(luaPath);
            else await Console.Error.WriteLineAsync($"nebra test: compiled output missing for {nebraPath}");
        }

        if (compiledPaths.Count == 0)
        {
            await Console.Error.WriteLineAsync("nebra test: no compiled test files to execute.");
            try { Directory.Delete(outDir, recursive: true); } catch { }
            return 1;
        }

        using var runtime = new NebraRuntime();
        runtime.AddPackagePath(outDir);
        var modulesDir = Path.Combine(Environment.CurrentDirectory, "nebra_modules");
        if (Directory.Exists(modulesDir)) runtime.AddPackagePath(modulesDir);

        if (!runtime.RegisterEmbeddedModule("nebra:test", typeof(Program).Assembly, "test_runtime.lua"))
        {
            await Console.Error.WriteLineAsync("nebra test: failed to register nebra:test runtime module.");
            try { Directory.Delete(outDir, recursive: true); } catch { }
            return 1;
        }

        var runnerScript = BuildTestRunnerScript(compiledPaths, filter, quiet);
        var ok = runtime.RunChunk(runnerScript, "<nebra-test-runner>");

        try { Directory.Delete(outDir, recursive: true); } catch { }
        return ok ? 0 : 1;
    }

    internal static List<string> DiscoverTestFiles(Config config, IEnumerable<string> sourceFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in sourceFiles)
        {
            var name = Path.GetFileName(f);
            foreach (var pattern in config.Test.Patterns)
            {
                if (name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(f);
                    break;
                }
            }
        }

        foreach (var dir in config.Test.Dirs)
        {
            var fullDir = Path.IsPathRooted(dir)
                ? dir
                : Path.Combine(Environment.CurrentDirectory, dir);
            if (!Directory.Exists(fullDir)) continue;

            foreach (var file in TestDirectoryFiles(fullDir))
            {
                result.Add(Path.GetFullPath(file));
            }
        }

        return result.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The test sources in <paramref name="testDirectory"/>. A directory beneath it holding its own
    /// <c>nebra.toml</c> is a separate project, such as a fixture built on its own, so its files
    /// are not tests of this one.
    /// </summary>
    private static IEnumerable<string> TestDirectoryFiles(string testDirectory)
    {
        var nestedProjects = ProjectFiles.Directories(testDirectory)
            .Where(directory => directory != testDirectory && File.Exists(Path.Combine(directory, "nebra.toml")))
            .Select(directory => directory + Path.DirectorySeparatorChar)
            .ToList();

        return ProjectFiles.Files(testDirectory, "*.neb")
            .Where(file => !file.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
            .Where(file => !nestedProjects.Any(project => file.StartsWith(project, StringComparison.Ordinal)));
    }

    private static string BuildTestRunnerScript(List<string> compiledLuaPaths, string? filter, bool quiet)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("local _T = require(\"nebra:test\")");
        if (quiet) sb.AppendLine("_T.__set_quiet(true)");
        if (!string.IsNullOrEmpty(filter))
            sb.AppendLine($"_T.__set_filter({EscapeLuaString(filter)})");

        foreach (var path in compiledLuaPaths)
        {
            sb.AppendLine($"_T.__begin_file({EscapeLuaString(path)})");
            sb.AppendLine($"dofile({EscapeLuaString(path)})");
        }

        sb.AppendLine("_T.__summary()");
        sb.AppendLine("if _T.__results().failed > 0 then os.exit(1) end");
        return sb.ToString();
    }

    private static string EscapeLuaString(string s)
    {
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    }

    #region nebra repl

    private static async Task<int> RunReplAsync(string[] args)
    {
        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        if (!TryLoadProjectConfig(configPath, out var config)) return 1;

        NebraRuntime CreateRuntime()
        {
            var rt = new NebraRuntime();
            var modulesDir = Path.Combine(Environment.CurrentDirectory, "nebra_modules");
            if (Directory.Exists(modulesDir)) rt.AddPackagePath(modulesDir);
            return rt;
        }

        var runtime = CreateRuntime();
        try
        {
            PrintReplHeader(config);

            var buffer = new StringBuilder();
            while (true)
            {
                Console.Write(buffer.Length == 0 ? "[36mnebra>[0m " : "[2m...>[0m ");
                var line = Console.ReadLine();
                if (line == null)
                {
                    Console.WriteLine();
                    break;
                }

                if (buffer.Length == 0 && line.TrimStart().StartsWith(':'))
                {
                    var (action, replacement) = HandleReplCommand(line.Trim(), runtime, config);
                    if (action == ReplCommandResult.Quit) break;
                    if (action == ReplCommandResult.Reset)
                    {
                        runtime.Dispose();
                        runtime = CreateRuntime();
                        Console.WriteLine("REPL state cleared.");
                    }
                    if (replacement != null)
                    {
                        EvaluateRepl(runtime, config, replacement);
                    }
                    continue;
                }

                if (buffer.Length > 0) buffer.Append('\n');
                buffer.Append(line);

                if (IsReplInputIncomplete(buffer.ToString())) continue;

                var input = buffer.ToString();
                buffer.Clear();

                if (string.IsNullOrWhiteSpace(input)) continue;

                EvaluateRepl(runtime, config, input);
            }
        }
        finally
        {
            runtime.Dispose();
        }

        return 0;
    }

    private static void PrintReplHeader(Config config)
    {
        Console.WriteLine($"[1mNebra REPL[0m {GetNebraVersion()} (target {config.Target}). Type [36m:help[0m for commands, [36m:quit[0m to exit.");
        Console.WriteLine("[2mNote: top-level `local` declarations don't persist between inputs — use `name = ...` for globals.[0m");
    }

    private enum ReplCommandResult { Continue, Quit, Reset }

    private static (ReplCommandResult Action, string? Replacement) HandleReplCommand(
        string cmd, NebraRuntime runtime, Config config)
    {
        var parts = cmd.Split(' ', 2);
        switch (parts[0])
        {
            case ":quit":
            case ":exit":
            case ":q":
                return (ReplCommandResult.Quit, null);
            case ":help":
            case ":h":
            case ":?":
                PrintReplHelp();
                return (ReplCommandResult.Continue, null);
            case ":clear":
                Console.Clear();
                return (ReplCommandResult.Continue, null);
            case ":reset":
                return (ReplCommandResult.Reset, null);
            case ":load":
            case ":l":
                if (parts.Length < 2)
                {
                    Console.Error.WriteLine("Usage: :load <path-to-.neb-file>");
                    return (ReplCommandResult.Continue, null);
                }
                var path = parts[1].Trim().Trim('"');
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($":load: file not found: {path}");
                    return (ReplCommandResult.Continue, null);
                }
                try
                {
                    return (ReplCommandResult.Continue, File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($":load: {ex.Message}");
                    return (ReplCommandResult.Continue, null);
                }
            default:
                Console.Error.WriteLine($"Unknown REPL command: {parts[0]}. Type :help for available commands.");
                return (ReplCommandResult.Continue, null);
        }
    }

    private static void PrintReplHelp()
    {
        Console.WriteLine("REPL commands:");
        Console.WriteLine("  :help, :h        Show this help");
        Console.WriteLine("  :quit, :q        Exit the REPL");
        Console.WriteLine("  :clear           Clear the screen");
        Console.WriteLine("  :reset           Drop all defined globals (fresh runtime)");
        Console.WriteLine("  :load <path>     Read and evaluate a .neb file in this session");
        Console.WriteLine();
        Console.WriteLine("Tips:");
        Console.WriteLine("  • A bare expression like `2 + 2` prints its value automatically.");
        Console.WriteLine("  • Use `name = ...` (no `local`) to define a global that survives.");
        Console.WriteLine("  • Multi-line input continues until brackets/blocks balance.");
    }

    private static void EvaluateRepl(NebraRuntime runtime, Config config, string input)
    {
        config.ReplMode = true;
        if (TryCompileNebraToLua("return " + input, config, out var luaExpr, out _))
        {
            var wrapped = "do local __r = (function() " + luaExpr + " end)(); if __r ~= nil then print(__r) end end";
            runtime.RunChunk(wrapped, "<repl>");
            return;
        }

        if (TryCompileNebraToLua(input, config, out var luaStmt, out var stmtErrors))
        {
            runtime.RunChunk(luaStmt, "<repl>");
            return;
        }

        foreach (var err in stmtErrors)
            Console.Error.WriteLine(err);
    }

    private static bool TryCompileNebraToLua(string source, Config config,
        out string luaSource, out List<string> errors)
    {
        luaSource = "";
        errors = [];
        var compiler = new NebraCompiler { Config = config };
        compiler.AddRawSource(source);
        if (!compiler.Compile())
        {
            errors = compiler.Diagnostics.Diagnostics.Select(d => d.ToString()).ToList();
            return false;
        }

        foreach (var pkg in compiler.Packages.Values)
        {
            foreach (var file in pkg.Files)
            {
                if (!string.IsNullOrEmpty(file.GeneratedLua))
                {
                    luaSource = file.GeneratedLua;
                    return true;
                }
            }
        }

        errors = ["compiler produced no Lua output"];
        return false;
    }

    /// <summary>
    /// Cheap heuristic that decides whether a REPL input chunk is still open
    /// (more lines need to come) by walking the lex tokens and counting how many
    /// block-openers are unmatched. Counts <c>function/do/if/for/while/match/class/
    /// interface/enum/module/repeat</c> against <c>end/until</c>, plus
    /// <c>(</c>/<c>)</c>, <c>[</c>/<c>]</c>, <c>{</c>/<c>}</c>.
    /// </summary>
    private static bool IsReplInputIncomplete(string source)
    {
        try
        {
            var input = new Antlr4.Runtime.AntlrInputStream(source);
            var lexer = new NebraLexer(input);
            lexer.RemoveErrorListeners();
            var stream = new Antlr4.Runtime.CommonTokenStream(lexer);
            stream.Fill();

            var blockDepth = 0;
            var parens = 0;
            var brackets = 0;
            var braces = 0;
            foreach (var tok in stream.GetTokens())
            {
                switch (tok.Type)
                {
                    case NebraLexer.FUNCTION:
                    case NebraLexer.DO:
                    case NebraLexer.IF:
                    case NebraLexer.FOR:
                    case NebraLexer.WHILE:
                    case NebraLexer.MATCH:
                    case NebraLexer.CLASS:
                    case NebraLexer.INTERFACE:
                    case NebraLexer.ENUM:
                    case NebraLexer.MODULE:
                    case NebraLexer.REPEAT:
                        blockDepth++; break;
                    case NebraLexer.END:
                    case NebraLexer.UNTIL:
                        blockDepth--; break;
                    case NebraLexer.LPAREN: parens++; break;
                    case NebraLexer.RPAREN: parens--; break;
                    case NebraLexer.LBRACK: brackets++; break;
                    case NebraLexer.RBRACK: brackets--; break;
                    case NebraLexer.LBRACE: braces++; break;
                    case NebraLexer.RBRACE: braces--; break;
                }
            }
            return blockDepth > 0 || parens > 0 || brackets > 0 || braces > 0;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region nebra compile

    private static async Task<int> RunCompileAsync(string[] args)
    {
        string? outPath = null;
        string? appName = null;
        string? targetRid = null;
        var aot = false;
        var keepBuild = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length) { await Console.Error.WriteLineAsync("error: --out needs a path"); return 1; }
                    outPath = args[++i]; break;
                case "--name":
                    if (i + 1 >= args.Length) { await Console.Error.WriteLineAsync("error: --name needs a value"); return 1; }
                    appName = args[++i]; break;
                case "--target":
                    if (i + 1 >= args.Length) { await Console.Error.WriteLineAsync("error: --target needs a RID"); return 1; }
                    targetRid = args[++i]; break;
                case "--aot": aot = true; break;
                case "--keep-build": keepBuild = true; break;
                default:
                    await Console.Error.WriteLineAsync($"error: unknown arg '{args[i]}'");
                    return 1;
            }
        }

        if (!await DetectDotnetSdkAsync())
        {
            await Console.Error.WriteLineAsync("error: dotnet SDK not found in PATH. Install .NET 10+ to compile Nebra binaries.");
            return 1;
        }

        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        if (!TryLoadProjectConfig(configPath, out var config)) return 1;

        if (config.TypesOnly)
        {
            await Console.Error.WriteLineAsync("error: cannot bundle a types-only project (no executable code).");
            return 1;
        }

        var srcDir = Path.Combine(Environment.CurrentDirectory, config.Source);
        if (!Directory.Exists(srcDir))
        {
            await Console.Error.WriteLineAsync($"error: source directory '{config.Source}' not found.");
            return 1;
        }

        var sourceFiles = ProjectFiles.Files(srcDir, "*.neb")
            .Where(f => !f.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            await Console.Error.WriteLineAsync($"error: no .neb files in '{config.Source}'.");
            return 1;
        }

        Console.WriteLine($"[1/5] Compiling {sourceFiles.Length} Nebra file(s)…");
        var compiler = new NebraCompiler { Config = config };
        foreach (var f in sourceFiles) compiler.AddSource(f);
        var compiled = compiler.Compile();
        await PrintDiagnosticsAsync(compiler);
        if (!compiled)
        {
            return 1;
        }

        var nativeIssues = DetectNativeDeps();
        if (nativeIssues.Count > 0)
        {
            await Console.Error.WriteLineAsync("error: standalone binary cannot bundle native modules:");
            foreach (var p in nativeIssues) await Console.Error.WriteLineAsync($"  • {p}");
            await Console.Error.WriteLineAsync("Remove the offending package or use 'nebra run' instead.");
            return 1;
        }

        var entryModule = ResolveEntryModule(config, srcDir);
        if (entryModule == null)
        {
            await Console.Error.WriteLineAsync(
                "error: cannot determine entry. Set [entry] in nebra.toml or add src/main.neb or src/index.neb.");
            return 1;
        }

        appName ??= !string.IsNullOrEmpty(config.Name) ? config.Name : Path.GetFileNameWithoutExtension(entryModule);
        appName = SanitizeAppName(appName);
        targetRid ??= RuntimeInformation.RuntimeIdentifier;

        if (targetRid != RuntimeInformation.RuntimeIdentifier)
        {
            await Console.Error.WriteLineAsync(
                $"warning: --target {targetRid} is requested but cross-compile from " +
                $"{RuntimeInformation.RuntimeIdentifier} may need additional .NET workloads.");
        }

        var outExt = targetRid.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? ".exe" : "";
        outPath ??= Path.Combine(Environment.CurrentDirectory, appName + outExt);
        outPath = Path.GetFullPath(outPath);

        Console.WriteLine($"[2/5] Bundle target: {appName}{outExt} for {targetRid}{(aot ? " (AOT — experimental)" : "")}");

        var modules = CollectBundleModules(compiler, srcDir);
        if (modules.Count == 0)
        {
            await Console.Error.WriteLineAsync("error: compilation produced no bundle-able modules.");
            return 1;
        }
        if (!modules.Any(m => m.ModuleName == entryModule))
        {
            await Console.Error.WriteLineAsync(
                $"error: entry module '{entryModule}' was not produced by the compiler. " +
                "Bundle aborted.");
            return 1;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nebra-compile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tempDir, "resources"));

        foreach (var m in modules)
        {
            var path = Path.Combine(tempDir, m.PhysicalName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, m.LuaSource);
        }

        var nebraRuntimePath = ResolveNebraRuntimePath();
        if (nebraRuntimePath == null)
        {
            await Console.Error.WriteLineAsync(
                "error: cannot locate Nebra.Runtime.dll next to the running nebra binary. " +
                "If running from source, build the solution first ('dotnet build Nebra.sln'). " +
                "If running a published binary, make sure the archive was fully extracted.");
            try { Directory.Delete(tempDir, true); } catch { }
            return 1;
        }

        WriteLauncherCsproj(tempDir, appName, targetRid, aot, nebraRuntimePath, modules);
        WriteLauncherProgram(tempDir, modules, entryModule);

        Console.WriteLine($"[3/5] Bundling {modules.Count} module(s) and publishing (this may take a while)…");
        var publishOk = await RunDotnetPublishAsync(tempDir, targetRid);
        if (!publishOk)
        {
            await Console.Error.WriteLineAsync("error: dotnet publish failed.");
            if (keepBuild) await Console.Error.WriteLineAsync($"keep-build: temp dir at {tempDir}");
            else try { Directory.Delete(tempDir, true); } catch { }
            return 1;
        }

        Console.WriteLine("[4/5] Copying binary to output path…");
        var publishedFile = Path.Combine(tempDir, "bin", "Release", "net10.0", targetRid, "publish", appName + outExt);
        if (!File.Exists(publishedFile))
        {
            await Console.Error.WriteLineAsync($"error: published file not found at {publishedFile}");
            if (keepBuild) await Console.Error.WriteLineAsync($"keep-build: temp dir at {tempDir}");
            else try { Directory.Delete(tempDir, true); } catch { }
            return 1;
        }
        File.Copy(publishedFile, outPath, overwrite: true);
        if (!targetRid.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.SetUnixFileMode(outPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { /* best-effort */ }
        }

        if (!keepBuild)
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
        else
        {
            Console.WriteLine($"keep-build: temp dir retained at {tempDir}");
        }

        var size = new FileInfo(outPath).Length / 1024.0 / 1024.0;
        Console.WriteLine($"[5/5] Done — {appName}{outExt} ({size:0.0} MB) → {outPath}");
        return 0;
    }

    private readonly record struct BundleModule(string ModuleName, string LogicalName, string PhysicalName, string LuaSource);

    /// <summary>
    /// Locates the Nebra.Runtime.dll that ships alongside the running <c>nebra</c>
    /// binary. <see cref="System.Reflection.Assembly.Location"/> returns an empty
    /// string when the assembly is embedded in a SingleFile bundle (warning
    /// IL3000), so we fall back to <see cref="AppContext.BaseDirectory"/> — which
    /// in self-extracting SingleFile mode is the directory where all assemblies
    /// have been extracted to disk on first launch.
    /// </summary>
    private static string? ResolveNebraRuntimePath()
    {
        var direct = typeof(NebraRuntime).Assembly.Location;
        if (!string.IsNullOrEmpty(direct) && File.Exists(direct)) return direct;

        var beside = Path.Combine(AppContext.BaseDirectory, "Nebra.Runtime.dll");
        if (File.Exists(beside)) return beside;

        return null;
    }

    private static List<BundleModule> CollectBundleModules(NebraCompiler compiler, string srcDir)
    {
        var result = new List<BundleModule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var modulesDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "nebra_modules"));
        var normalizedSrc = Path.GetFullPath(srcDir);

        foreach (var pkg in compiler.Packages.Values)
        {
            foreach (var file in pkg.Files)
            {
                if (string.IsNullOrEmpty(file.GeneratedLua)) continue;
                if (file.Filename == null) continue;
                if (!file.Filename.EndsWith(".neb", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Filename.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsUnderDirectory(file.Filename, normalizedSrc)) continue;

                var moduleName = ModuleNameFor(file.Filename, normalizedSrc, modulesDir);
                if (moduleName == null) continue;
                if (!seen.Add(moduleName)) continue;

                var logicalName = moduleName + ".lua";
                var physicalName = "resources/" + moduleName.Replace('/', '_') + ".lua";
                result.Add(new BundleModule(moduleName, logicalName, physicalName, file.GeneratedLua));
            }
        }

        // Pre-built .lua files inside nebra_modules/ (not produced by NebraCompiler).
        // These are how pure-Lua packages or pre-compiled Nebra packages ship. A dependency's
        // sources are compiled into the type universe but never bundled from there: it built its
        // own output under its own configuration, and that is the code its callers expect.
        if (Directory.Exists(modulesDir))
        {
            foreach (var luaPath in Directory.GetDirectories(modulesDir).SelectMany(pkgDir => InstalledPackages.EnumerateFilesSafely(pkgDir, "*.lua")))
            {
                var moduleName = ModuleNameFor(luaPath, normalizedSrc, modulesDir);
                if (moduleName == null) continue;
                if (!seen.Add(moduleName)) continue;

                string luaSource;
                try { luaSource = File.ReadAllText(luaPath); }
                catch { continue; }

                var logicalName = moduleName + ".lua";
                var physicalName = "resources/" + moduleName.Replace('/', '_') + ".lua";
                result.Add(new BundleModule(moduleName, logicalName, physicalName, luaSource));
            }
        }

        return result;
    }

    /// <summary>
    /// Maps a source file path to the Lua module name that <c>require(...)</c> would use.
    /// Files under <paramref name="srcDir"/> use their relative path; files inside
    /// <c>nebra_modules/&lt;pkg&gt;/</c> become <c>&lt;pkg&gt;</c> (for init.lua / init.neb) or
    /// <c>&lt;pkg&gt;/&lt;rel&gt;</c>. Returns null for files outside both roots.
    /// </summary>
    private static string? ModuleNameFor(string filePath, string srcDir, string modulesDir)
    {
        var full = Path.GetFullPath(filePath);
        if (full.StartsWith(srcDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.Equals(srcDir, StringComparison.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(srcDir, full).Replace('\\', '/');
            return StripNebraOrLuaExt(rel);
        }

        if (full.StartsWith(modulesDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(modulesDir, full).Replace('\\', '/');
            // rel = "<pkg>/<sub-path>.lua" → split off pkg, then strip init.
            var slash = rel.IndexOf('/');
            if (slash < 0) return null;
            var pkg = rel[..slash];
            var inner = StripNebraOrLuaExt(rel[(slash + 1)..]);
            if (inner == "init" || string.IsNullOrEmpty(inner)) return pkg;
            return pkg + "/" + inner;
        }

        return null;
    }

    private static string StripNebraOrLuaExt(string rel)
    {
        if (rel.EndsWith(".neb", StringComparison.OrdinalIgnoreCase)) return rel[..^4];
        if (rel.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return rel[..^4];
        return rel;
    }

    private static async Task<bool> DetectDotnetSdkAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> DetectNativeDeps()
    {
        var hits = new List<string>();
        var dir = Path.Combine(Environment.CurrentDirectory, "nebra_modules");
        if (!Directory.Exists(dir)) return hits;
        foreach (var p in Directory.GetDirectories(dir).SelectMany(pkgDir => InstalledPackages.EnumerateFilesSafely(pkgDir, "*.*")))
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext is ".so" or ".dll" or ".dylib") hits.Add(p);
        }
        return hits;
    }

    private static string? ResolveEntryModule(Config config, string srcDir)
    {
        string? entryFile = null;
        if (!string.IsNullOrEmpty(config.Entry))
        {
            var candidate = Path.Combine(srcDir, config.Entry);
            if (!Path.HasExtension(candidate)) candidate += ".neb";
            if (File.Exists(candidate)) entryFile = candidate;
        }
        else
        {
            foreach (var c in new[] { "main.neb", "index.neb" })
            {
                var p = Path.Combine(srcDir, c);
                if (File.Exists(p)) { entryFile = p; break; }
            }
        }
        if (entryFile == null) return null;
        var rel = Path.GetRelativePath(srcDir, entryFile).Replace('\\', '/');
        return rel.EndsWith(".neb", StringComparison.OrdinalIgnoreCase) ? rel[..^4] : rel;
    }

    private static string SanitizeAppName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        var r = sb.ToString();
        if (r.Length == 0 || char.IsDigit(r[0])) r = "app_" + r;
        return r;
    }

    private static void WriteLauncherCsproj(
        string tempDir, string appName, string rid, bool aot,
        string nebraRuntimeDll, List<BundleModule> modules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <OutputType>Exe</OutputType>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine($"    <AssemblyName>{appName}</AssemblyName>");
        sb.AppendLine($"    <RuntimeIdentifier>{rid}</RuntimeIdentifier>");
        sb.AppendLine("    <SelfContained>true</SelfContained>");
        if (aot)
        {
            sb.AppendLine("    <PublishAot>true</PublishAot>");
        }
        else
        {
            sb.AppendLine("    <PublishSingleFile>true</PublishSingleFile>");
            sb.AppendLine("    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>");
            sb.AppendLine("    <InvariantGlobalization>true</InvariantGlobalization>");
        }
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <Reference Include=\"Nebra.Runtime\"><HintPath>{nebraRuntimeDll}</HintPath></Reference>");
        sb.AppendLine("    <PackageReference Include=\"KeraLua\" Version=\"1.4.9\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("  <ItemGroup>");
        foreach (var m in modules)
        {
            sb.AppendLine($"    <EmbeddedResource Include=\"{m.PhysicalName}\" LogicalName=\"{m.LogicalName}\" />");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        File.WriteAllText(Path.Combine(tempDir, "Launcher.csproj"), sb.ToString());
    }

    private static void WriteLauncherProgram(string tempDir, List<BundleModule> modules, string entryModule)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using Nebra.Runtime;");
        sb.AppendLine();
        sb.AppendLine("internal static class Program");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly (string Module, string Resource)[] _modules = new[]");
        sb.AppendLine("    {");
        foreach (var m in modules)
        {
            sb.AppendLine($"        (\"{EscapeCSharp(m.ModuleName)}\", \"{EscapeCSharp(m.LogicalName)}\"),");
        }
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine($"    private const string EntryModule = \"{EscapeCSharp(entryModule)}\";");
        sb.AppendLine();
        sb.AppendLine("    public static int Main(string[] args)");
        sb.AppendLine("    {");
        sb.AppendLine("        using var rt = new NebraRuntime();");
        sb.AppendLine("        var asm = typeof(Program).Assembly;");
        sb.AppendLine("        foreach (var (mod, res) in _modules)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (!rt.RegisterEmbeddedModule(mod, asm, res))");
        sb.AppendLine("            {");
        sb.AppendLine("                Console.Error.WriteLine($\"failed to register module {mod}\");");
        sb.AppendLine("                return 1;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (args.Length > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            var quoted = string.Join(\", \", args.Select(a => \"\\\"\" + a.Replace(\"\\\\\", \"\\\\\\\\\").Replace(\"\\\"\", \"\\\\\\\"\") + \"\\\"\"));");
        sb.AppendLine("            rt.RunChunk(\"arg = { \" + quoted + \" }\", \"<args>\");");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return rt.RequireAndRun(EntryModule) ? 0 : 1;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), sb.ToString());
    }

    private static string EscapeCSharp(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task<bool> RunDotnetPublishAsync(string projDir, string rid)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish Launcher.csproj -c Release -r {rid} --nologo -v quiet",
            WorkingDirectory = projDir,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }

    #endregion

    private static async Task<int> RunInstallAsync(string[] args)
    {
        var frozen = args.Contains("--frozen");
        var offline = args.Contains("--offline");
        var noDev = args.Contains("--no-dev") || args.Contains("--production");
        var noCache = args.Contains("--no-cache");
        var (allowAll, allowList) = ParseAllowScripts(args);

        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        var config = Config.LoadFromFile(configPath);
        if (config == null)
        {
            await Console.Error.WriteLineAsync("error: nebra.toml not found in current directory. Run 'nebra init' first.");
            return 1;
        }

        var installer = new Installer();
        return await installer.InstallAsync(config, Environment.CurrentDirectory, new InstallOptions
        {
            Frozen = frozen,
            Offline = offline,
            IncludeDev = !noDev,
            NoRegistryCache = noCache,
            AllowAllScripts = allowAll || config.Install.AllowScripts,
            AllowedScriptPackages = allowList,
        });
    }

    private static async Task<int> RunCreateAsync(string[] args)
    {
        string? spec = null;
        string? dir = null;
        var skipSetup = false;
        var offline = false;
        var noCache = false;

        foreach (var a in args)
        {
            switch (a)
            {
                case "--skip-setup": skipSetup = true; break;
                case "--offline": offline = true; break;
                case "--no-cache": noCache = true; break;
                default:
                    if (a.StartsWith("--"))
                    {
                        await Console.Error.WriteLineAsync($"error: unknown flag '{a}'");
                        return 1;
                    }
                    if (spec == null) spec = a;
                    else if (dir == null) dir = a;
                    else
                    {
                        await Console.Error.WriteLineAsync($"error: unexpected argument '{a}'");
                        return 1;
                    }
                    break;
            }
        }

        if (spec == null)
        {
            await Console.Error.WriteLineAsync("error: missing template specifier. Usage: nebra create <spec_or_url> [dir]");
            return 1;
        }

        var creator = new ProjectCreator();
        return await creator.CreateAsync(spec, new CreateOptions
        {
            TargetDirectory = dir,
            SkipSetup = skipSetup,
            NoRegistryCache = noCache,
            Offline = offline,
        });
    }

    private static async Task<int> RunPublishAsync(string[] args)
    {
        var options = ReadOptions(args, out var positional);
        if (options == null || positional.Count > 0)
        {
            await Console.Error.WriteLineAsync("Usage: nebra publish [--scope <scope>] [--description <text>]");
            return 1;
        }

        return await RegistryPublisher.PublishAsync(options.GetValueOrDefault("--scope"), options.GetValueOrDefault("--description"));
    }

    private static async Task<int> RunUnpublishAsync(string[] args)
    {
        var options = ReadOptions(args, out var positional);
        if (options == null || positional.Count > 1)
        {
            await Console.Error.WriteLineAsync("Usage: nebra unpublish [@scope/name] [--scope <scope>]");
            return 1;
        }

        return await RegistryPublisher.UnpublishAsync(positional.FirstOrDefault(), options.GetValueOrDefault("--scope"));
    }

    private static async Task<int> RunScopeAsync(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("add-owner" or "remove-owner"))
        {
            await Console.Error.WriteLineAsync("Usage: nebra scope add-owner|remove-owner <scope> <github-user>");
            return 1;
        }

        return await RegistryPublisher.ChangeOwnerAsync(args[0] == "add-owner", args[1], args[2]);
    }

    /// <summary>
    /// Splits <c>--flag value</c> pairs from positional arguments; <c>null</c> when a flag lacks its value.
    /// </summary>
    private static Dictionary<string, string>? ReadOptions(string[] args, out List<string> positional)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        positional = [];
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
            {
                positional.Add(args[i]);
                continue;
            }

            if (i + 1 >= args.Length) return null;
            options[args[i]] = args[++i];
        }

        return options;
    }

    private static async Task<int> RunRegistryAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "refresh":
                await Console.Error.WriteLineAsync("warning: `nebra registry refresh` has moved to `nebra pm refresh-registry`.");
                return await RunPmRefreshRegistry();
            default:
                await Console.Error.WriteLineAsync("Usage: nebra pm refresh-registry");
                return 1;
        }
    }

    /// <summary>
    /// Package-manager utility subcommands. Currently only <c>prune</c>, which
    /// wipes the on-disk caches so the next <c>install</c> / <c>create</c>
    /// re-fetches from origin. Use <c>nebra pm prune</c> when a package was
    /// republished under the same ref (force-pushed tag, mutable branch) and
    /// the cached snapshot is now stale.
    /// </summary>
    private static async Task<int> RunPmAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        return sub switch
        {
            "prune" => RunPmPrune(args.Skip(1).ToArray()),
            "update" => await RunPmUpdate(args.Skip(1).ToArray()),
            "refresh-registry" => await RunPmRefreshRegistry(),
            _ => RunPmHelp(sub),
        };
    }

    /// <summary>
    /// Prints the <c>pm</c> usage. Bare <c>nebra pm</c> asks for it and succeeds; a subcommand that
    /// does not exist is an error and has to be reported as one, or a typo in a script passes for a
    /// successful run.
    /// </summary>
    private static int RunPmHelp(string subcommand)
    {
        if (!string.IsNullOrEmpty(subcommand))
        {
            Console.Error.WriteLine($"Unknown pm subcommand: {subcommand}");
            Console.Error.WriteLine("Run 'nebra pm' for available subcommands.");
            return 1;
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  nebra pm prune                  Wipe ALL cached bare clones, snapshots and tmp staging.");
        Console.WriteLine("  nebra pm prune <git-spec>       Wipe the bare clone + every cached snapshot for one repo");
        Console.WriteLine("                                (e.g. `nebra pm prune github:nebra-lang/nanos-world-types`).");
        Console.WriteLine("  nebra pm update                 Re-resolve every dependency against origin and rewrite the lockfile.");
        Console.WriteLine("  nebra pm update <name>          Re-resolve only the named dependency.");
        Console.WriteLine("  nebra pm refresh-registry       Refresh the cached alias registry index.");
        return 0;
    }

    /// <summary>
    /// Re-resolves dependencies against origin and rewrites the lockfile. With
    /// no args it drops the entire lockfile so every dep gets re-resolved; with
    /// a name it removes just that one entry before re-installing. Either way
    /// the bare clone is refreshed (<see cref="PackageManager.GitFetcher.EnsureBareCloneAsync"/>
    /// already runs <c>git fetch</c>) so floating refs (branch / wildcard tag)
    /// pick up the latest commits.
    /// </summary>
    private static async Task<int> RunPmUpdate(string[] args)
    {
        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        var config = Config.LoadFromFile(configPath);
        if (config == null)
        {
            await Console.Error.WriteLineAsync("error: nebra.toml not found in current directory. Run 'nebra init' first.");
            return 1;
        }

        var lockPath = Path.Combine(Environment.CurrentDirectory, "nebra.lock");
        var existing = Lockfile.Load(lockPath);

        if (args.Length == 0)
        {
            if (existing != null && existing.Packages.Count > 0)
            {
                Console.WriteLine($"Dropping {existing.Packages.Count} lockfile entries — every dep will be re-resolved against origin.");
                new Lockfile { Version = 1 }.Save(lockPath);
            }
            else
            {
                Console.WriteLine("No lockfile entries to drop — proceeding with fresh resolve.");
            }
        }
        else
        {
            if (existing == null || existing.Packages.Count == 0)
            {
                Console.WriteLine("No lockfile to update — running install instead.");
            }
            else
            {
                var dropped = 0;
                foreach (var name in args)
                {
                    var idx = existing.Packages.FindIndex(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                    if (idx >= 0)
                    {
                        existing.Packages.RemoveAt(idx);
                        Console.WriteLine($"  unlocked '{name}'");
                        dropped++;
                    }
                    else
                    {
                        Console.WriteLine($"  '{name}' not in lockfile — will resolve fresh");
                    }
                }
                existing.Save(lockPath);
                if (dropped == 0)
                {
                    Console.WriteLine("Nothing dropped; install will be a no-op unless a new dep is declared in nebra.toml.");
                }
            }
        }

        var installer = new Installer();
        return await installer.InstallAsync(config, Environment.CurrentDirectory, new InstallOptions
        {
            Frozen = false,
            Offline = false,
            IncludeDev = true,
            NoRegistryCache = false,
            AllowAllScripts = config.Install.AllowScripts,
            AllowedScriptPackages = new HashSet<string>(StringComparer.Ordinal),
        });
    }

    private static async Task<int> RunPmRefreshRegistry()
    {
        try
        {
            await Registry.RefreshAsync();
            Console.WriteLine($"Registry index refreshed from {Registry.EffectiveUrl()}.");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunPmPrune(string[] args)
    {
        if (args.Length == 0)
        {
            return PrunePaths(
                ("git cache",     NebraHome.GitCacheRoot),
                ("package store", NebraHome.StoreRoot),
                ("tmp staging",   NebraHome.TmpRoot));
        }

        PackageSpec spec;
        try { spec = PackageSpec.Parse(args[0]); }
        catch (PackageManagerException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        if (spec.Kind != SpecKind.Git || spec.Host == null || spec.Owner == null || spec.Repo == null)
        {
            Console.Error.WriteLine("error: `nebra pm prune <spec>` requires a git specifier (github:owner/repo, gitlab:..., git+url).");
            return 1;
        }

        var barePath = NebraHome.BareClonePath(spec.Host, spec.Owner, spec.Repo);
        var storeRepoDir = Path.GetDirectoryName(NebraHome.PackagePath(spec.Host, spec.Owner, spec.Repo, "x"))!;
        return PrunePaths(
            ($"bare clone {spec.Host}/{spec.Owner}/{spec.Repo}", barePath),
            ($"snapshots {spec.Host}/{spec.Owner}/{spec.Repo}", storeRepoDir));
    }

    private static int PrunePaths(params (string Label, string Path)[] targets)
    {
        var anyExisted = false;
        foreach (var (label, path) in targets)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                Console.WriteLine($"  {label}: nothing to prune ({path})");
                continue;
            }
            try
            {
                Directory.Delete(path, recursive: true);
                Console.WriteLine($"  {label}: pruned ({path})");
                anyExisted = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  {label}: failed to prune {path}: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine(anyExisted
            ? "Cache pruned. Next install/create will re-fetch from origin."
            : "Nothing to prune.");
        return 0;
    }

    /// <summary>
    /// Parses <c>--allow-scripts</c> (allow all) and <c>--allow-scripts=pkg1,pkg2</c> (allow-list).
    /// Returns <c>(allowAll, allowedSet)</c>; <c>allowedSet</c> is empty when not specified.
    /// </summary>
    private static (bool AllowAll, HashSet<string> Allowed) ParseAllowScripts(string[] args)
    {
        var allowAll = false;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in args)
        {
            if (a == "--allow-scripts") allowAll = true;
            else if (a.StartsWith("--allow-scripts=", StringComparison.Ordinal))
            {
                var rhs = a["--allow-scripts=".Length..];
                foreach (var name in rhs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    allowed.Add(name);
            }
        }
        return (allowAll, allowed);
    }

    private static async Task<int> RunAddAsync(string[] args)
    {
        var group = DependencyGroup.Runtime;
        string? spec = null;
        var (allowAll, allowList) = ParseAllowScripts(args);
        var noCache = args.Contains("--no-cache");
        foreach (var a in args)
        {
            switch (a)
            {
                case "--dev": group = DependencyGroup.Dev; break;
                case "--peer": group = DependencyGroup.Peer; break;
                case "--allow-scripts": break;
                case "--no-cache": break;
                default:
                    if (a.StartsWith("--allow-scripts=", StringComparison.Ordinal)) break;
                    if (spec == null) spec = a;
                    else
                    {
                        await Console.Error.WriteLineAsync($"error: unexpected argument '{a}'");
                        return 1;
                    }
                    break;
            }
        }

        if (spec == null)
        {
            await Console.Error.WriteLineAsync("error: missing package specifier. Usage: nebra add <spec> [--dev|--peer]");
            return 1;
        }

        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        var config = Config.LoadFromFile(configPath);
        if (config == null)
        {
            await Console.Error.WriteLineAsync("error: nebra.toml not found in current directory. Run 'nebra init' first.");
            return 1;
        }

        var installer = new Installer();
        return await installer.AddAsync(spec, config, Environment.CurrentDirectory, configPath, group, new InstallOptions
        {
            NoRegistryCache = noCache,
            AllowAllScripts = allowAll || config.Install.AllowScripts,
            AllowedScriptPackages = allowList,
        });
    }

    private static async Task<int> RunRemoveAsync(string[] args)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync("error: missing package name. Usage: nebra remove <name>");
            return 1;
        }
        var name = args[0];

        var configPath = Path.Combine(Environment.CurrentDirectory, "nebra.toml");
        var config = Config.LoadFromFile(configPath);
        if (config == null)
        {
            await Console.Error.WriteLineAsync("error: nebra.toml not found in current directory.");
            return 1;
        }

        var installer = new Installer();
        return await installer.RemoveAsync(name, config, Environment.CurrentDirectory, configPath, new InstallOptions());
    }

    private static int RunUnknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run 'nebra help' for available commands.");
        return 1;
    }

    private static bool RunScripts(List<string> scripts, string phase)
    {
        foreach (var script in scripts)
        {
            Console.WriteLine($"[{phase}] {script}");
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd" : "sh",
                Arguments = OperatingSystem.IsWindows() ? $"/c {script}" : $"-c \"{script}\"",
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.Error.WriteLine($"[{phase}] Failed to start: {script}");
                return false;
            }

            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"[{phase}] Script exited with code {proc.ExitCode}: {script}");
                return false;
            }
        }
        return true;
    }
}
