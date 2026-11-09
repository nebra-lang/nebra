using System.Text.Json;
using System.Text.Json.Serialization;
using Tomlyn;

namespace Nebra.Configuration;

/// <summary>
/// Represents the configuration of the Nebra compiler, which can be loaded from a TOML file. The configuration file
/// (nebra.toml) is used to configure the transpilers behavior, such as the target language, the output directory, and
/// other options.
/// </summary>
public sealed class Config
{
    #region Sections

    /// <summary>
    /// The optional name of the project.
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// The optional version of the project.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// The target version of Lua to transpile to. This is used to determine which language features are available and
    /// how to generate the code. The default value is Lua 5.4.
    /// </summary>
    public LuaVersion Target { get; set; } = LuaVersion.Lua54;

    /// <summary>
    /// The entry Nebra file.
    /// </summary>
    public string? Entry { get; set; }

    /// <summary>
    /// Relative paths of other configuration files to extend. The paths are relative to the current configuration file.
    /// This allows for modular and reusable configurations, where common settings can be defined in a base configuration
    /// file and then extended by other configuration files. When loading a configuration file, the compiler will first
    /// load the base configuration files specified in the "extends" section, and then override any settings with the
    /// values specified in the current configuration file.
    /// </summary>
    public List<string> Extends { get; set; } = [];
    
    /// <summary>
    /// The rule preset to load. Load presets work the same way as extends. First the preset is being loaded. Then
    /// the configuration file is being loaded and overrides the preset.
    /// </summary>
    /// <remarks>
    /// Predefined presets include <b>strict</b> and <b>relaxed</b>.<br/>
    /// <b>strict</b> enables all rules which ensure good code quality like strict nil, disallowing any and default immutability.<br/>
    /// <b>relaxed</b> is normal Lua just added with types. Thats the default loaded preset.
    /// </remarks>
    public string? Preset { get; set; }
    
    /// <summary>
    /// The output directory for the generated Lua code. This is the directory where the transpiled Lua files will be saved. The default value is "out".
    /// </summary>
    public string Output { get; set; } = "out";
    
    /// <summary>
    /// Whether to minify the generated Lua code. If true, the generated code will be minified by removing unnecessary whitespace and comments. The default value is false.
    /// </summary>
    public bool Minify { get; set; } = false;
    
    /// <summary>
    /// Whether to generate documentation for the transpiled code. If true, the transpiler will generate documentation documents based on the Lua comments given.
    /// </summary>
    public bool GenerateDocs { get; set; } = false;
    
    /// <summary>
    /// Whether to generate type declarations for the transpiled code. If true, the transpiler will generate type declaration files based on the types used in the source code and the type annotations given.
    /// </summary>
    public bool GenerateDeclarations { get; set; } = true;

    /// <summary>Writes a <c>.lua.map</c> next to every generated file, mapping its lines to the source.</summary>
    public bool SourceMaps { get; set; }
    
    /// <summary>
    /// A list of relative directories or type declaration files to include in the type universe.
    /// </summary>
    public List<string> Globals { get; set; } = [];
    
    /// <summary>
    /// A list of relative directories or Nebra files to load annotation plugins from.
    /// </summary>
    public List<string> Annotations { get; set; } = [];

    public string Source { get; set; } = "src";

    /// <summary>
    /// The directory the project lives in, which every relative path in the configuration is
    /// resolved against. Set to the directory of <c>nebra.toml</c> when the configuration is loaded
    /// from a file, and to the current directory otherwise. Not part of the file format.
    /// </summary>
    [Tomlyn.Serialization.TomlIgnore]
    public string ProjectRoot { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// File contents that take precedence over what is on disk, keyed by full path, so a host such
    /// as the language server can compile text the editor has not saved yet. Not part of the file
    /// format.
    /// </summary>
    [Tomlyn.Serialization.TomlIgnore]
    public IReadOnlyDictionary<string, string> SourceOverrides { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// Set while an annotation definition file is compiled: its environment (the <c>ir</c>
    /// builder and the shapes it declares itself with) is loaded, annotations are not applied to
    /// it, and it is checked the way the annotation loader reads it.
    /// </summary>
    [Tomlyn.Serialization.TomlIgnore]
    public bool AnnotationFile { get; set; }

    /// <summary>
    /// Reads the source at <paramref name="path"/>, preferring <see cref="SourceOverrides"/> over
    /// the file on disk.
    /// </summary>
    public string ReadSource(string path)
    {
        return SourceOverrides.TryGetValue(Path.GetFullPath(path), out var text) ? text : File.ReadAllText(path);
    }

    /// <summary>
    /// When true, the project ships only <c>.d.neb</c> declaration files (a "types-only"
    /// package — like a TypeScript <c>@types/*</c> shim). <c>nebra build</c>, <c>compile</c>,
    /// <c>run</c> and <c>test</c> become graceful no-ops; <c>nebra docs</c> still generates.
    /// </summary>
    public bool TypesOnly { get; set; } = false;

    /// <summary>
    /// The global the package's root module is reachable as at runtime, for runtimes where another
    /// package cannot load its files. The package sets it from the exports of its root module, and
    /// a consumer's imports from the package read it instead of loading files.
    /// </summary>
    public string? RuntimeGlobal { get; set; }

    /// <summary>
    /// How the package sets <see cref="RuntimeGlobal"/>: a statement where <c>%name</c> stands for
    /// the quoted global name and <c>%value</c> for the exports, such as
    /// <c>Package.Export(%name, %value)</c>. Assigns <c>_G</c> when not set.
    /// </summary>
    public string? RuntimeExport { get; set; }

    public ScriptsSection Scripts { get; set; } = new();

    public CodeSection Code { get; set; } = new();

    public MangleSection Mangle { get; set; } = new();

    public RulesSection Rules { get; set; } = new();

    public InstallSection Install { get; set; } = new();

    public StdlibSection Stdlib { get; set; } = new();

    public TestSection Test { get; set; } = new();

    public ReflectionSection Reflection { get; set; } = new();

    /// <summary>
    /// Glob → side-name-list mapping that restricts which symbols (annotated
    /// with <c>@side(...)</c>) each source file may reach. Files outside any
    /// glob inherit <see cref="Side.All"/> so the feature is opt-in per
    /// project. Keys are glob patterns like <c>"src/Client/**"</c>; values are
    /// lists of side names (<c>client</c>, <c>server</c>, <c>shared</c>).
    /// </summary>
    public Dictionary<string, List<string>> Sides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, undeclared identifiers are silently treated as <c>any</c>-typed
    /// globals so a fresh per-input compilation in <c>nebra repl</c> can still reach
    /// state from earlier inputs (whose locals/declarations did not survive). Never
    /// loaded from <c>nebra.toml</c> — set programmatically by the REPL host only.
    /// </summary>
    [JsonIgnore] public bool ReplMode { get; set; } = false;

    /// <summary>
    /// Runtime dependencies. Values are either a string specifier (e.g. <c>"github:owner/repo@v1"</c>)
    /// or an inline table (e.g. <c>{ git = "...", tag = "v1" }</c>). Consumers should feed each
    /// value through <see cref="PackageManager.PackageSpec.FromValue"/> to normalize.
    /// </summary>
    public Dictionary<string, object> Dependencies { get; set; } = new();

    public Dictionary<string, object> DevDependencies { get; set; } = new();

    public Dictionary<string, object> PeerDependencies { get; set; } = new();

    /// <summary>
    /// Asset files to copy to the output directory. Keys are relative paths to the source files, values are relative
    /// paths in the output directory. This is used to include non-Nebra files in the output, such as images, sounds, or
    /// other resources.
    /// </summary>
    public Dictionary<string, string> Assets { get; set; } = new();

    #endregion

    #region Initialization

    private Config Initialize(string directory, int depth)
    {
        ProjectRoot = Path.GetFullPath(directory);

        if (depth >= 10)
        {
            Console.Error.WriteLine($"Maximum configuration depth exceeded while loading configuration from '{directory}'. Possible circular dependency in extends.");
            return this;
        }
        
        foreach (var extend in Extends)
        {
            var extensionConfig = LoadFromFile(Path.Combine(directory, extend), depth + 1);
            if (extensionConfig != null)
            {
                Merge(extensionConfig);
            }
        }

        if (string.Equals(Preset, "strict", StringComparison.OrdinalIgnoreCase))
        {
            Rules.ApplyStrictPreset();
        }
         
        return this;
    }

    private void Merge(Config config)
    {
        Name = MergeVal(Name, config.Name, null);
        Target = MergeVal(Target, config.Target, LuaVersion.Lua54);
        Entry = MergeVal(Entry, config.Entry, null);
        Output = MergeVal(Output, config.Output, "out");
        Minify = MergeVal(Minify, config.Minify, false);
        GenerateDocs = MergeVal(GenerateDocs, config.GenerateDocs, false);
        GenerateDeclarations = MergeVal(GenerateDeclarations, config.GenerateDeclarations, true);
        SourceMaps = MergeVal(SourceMaps, config.SourceMaps, false);
        Source = MergeVal(Source, config.Source, "src");
        TypesOnly = MergeVal(TypesOnly, config.TypesOnly, false);
        RuntimeGlobal = MergeVal(RuntimeGlobal, config.RuntimeGlobal, null);
        RuntimeExport = MergeVal(RuntimeExport, config.RuntimeExport, null);
        Globals.AddRange(config.Globals);
        Annotations.AddRange(config.Annotations);

        Scripts.Merge(config.Scripts);
        Code.Merge(config.Code);
        Mangle.Merge(config.Mangle);
        Rules.Merge(config.Rules);
        Install.Merge(config.Install);
        Stdlib.Merge(config.Stdlib);
        Test.Merge(config.Test);
        Reflection.Merge(config.Reflection);
        foreach (var kv in config.Sides)
            Sides.TryAdd(kv.Key, kv.Value);
        foreach (var kv in config.Dependencies)
            Dependencies.TryAdd(kv.Key, kv.Value);
        foreach (var kv in config.DevDependencies)
            DevDependencies.TryAdd(kv.Key, kv.Value);
        foreach (var kv in config.PeerDependencies)
            PeerDependencies.TryAdd(kv.Key, kv.Value);
        foreach (var kv in config.Assets)
            Assets.TryAdd(kv.Key, kv.Value);
    }
    
    #endregion
    
    #region IO

    private static readonly TomlSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Replace,
        WriteIndented = true,
        IndentSize = 4,
        DefaultIgnoreCondition = TomlIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Loads the configuration from a TOML file. If the file does not exist or is invalid, null will be returned.
    /// </summary>
    /// <param name="path">The path to the TOML configuration file. This can be an absolute path or a relative path from the current working directory.</param>
    /// <returns>The loaded configuration object if the file exists and is valid; otherwise, null.</returns>
    public static Config? LoadFromFile(string path) => LoadFromFile(path, 0);

    private static Config? LoadFromFile(string path, int depth)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            
            return TomlSerializer.Deserialize<Config>(File.ReadAllText(path), DefaultOptions)?.Initialize(Path.GetDirectoryName(path) ?? ".", depth);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error loading configuration from file '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Saves the configuration to a TOML file. If the file already exists, it will be overwritten. If an error occurs
    /// during the saving process, false will be returned and the error message will be printed to the standard error
    /// stream.
    /// </summary>
    /// <param name="config">The configuration object to save. This object will be serialized to TOML format and written to the specified file.</param>
    /// <param name="path">The path to the TOML configuration file. This can be an absolute path or a relative path from the current working directory. If the file already exists, it will be overwritten.</param>
    /// <returns>true if the configuration was successfully saved to the file; otherwise, false.</returns>
    public static bool SaveToFile(Config config, string path)
    {
        try
        {
            var toml = TomlSerializer.Serialize(config, DefaultOptions);
            File.WriteAllText(path, toml);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error saving configuration to file '{path}': {ex.Message}");
            return false;
        }
    }

    public Config Clone()
    {
        return new Config
        {
            Name = Name,
            Target = Target,
            Entry = Entry,
            Extends = [..Extends],
            Preset = Preset,
            Output = Output,
            Minify = Minify,
            GenerateDocs = GenerateDocs,
            GenerateDeclarations = GenerateDeclarations,
            SourceMaps = SourceMaps,
            Source = Source,
            ProjectRoot = ProjectRoot,
            SourceOverrides = SourceOverrides,
            AnnotationFile = AnnotationFile,
            TypesOnly = TypesOnly,
            RuntimeGlobal = RuntimeGlobal,
            RuntimeExport = RuntimeExport,
            Globals = [..Globals],
            Annotations = [..Annotations],
            Scripts = Scripts,
            Code = Code,
            Mangle = Mangle,
            Rules = Rules,
            Install = Install,
            Stdlib = Stdlib,
            Test = Test,
            Reflection = Reflection,
            Sides = new Dictionary<string, List<string>>(Sides, StringComparer.OrdinalIgnoreCase),
            Dependencies = new Dictionary<string, object>(Dependencies),
            DevDependencies = new Dictionary<string, object>(DevDependencies),
            PeerDependencies = new Dictionary<string, object>(PeerDependencies)
        };
    }

    internal static T MergeVal<T>(T current, T @new, T @default)
    {
        if (Equals(current, @default))
            return @new;
        return current;
    }

    #endregion
}