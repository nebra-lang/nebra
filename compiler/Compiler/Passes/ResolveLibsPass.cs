using System.Reflection;
using Antlr4.Runtime;
using Nebra.IR;
using Nebra.PackageManager;

namespace Nebra.Compiler.Passes;

public sealed class ResolveLibsPass() : Pass(PassName, PassScope.PerBuild)
{
    public const string PassName = "ResolveLibs";

    /// <summary>
    /// Logical filename used by the embedded stdlib so disable lookups can
    /// distinguish <c>std.d.neb</c> entries (subject to user toggles) from
    /// runtime-only declaration files such as <c>http.d.neb</c>.
    /// </summary>
    private const string StdLogicalName = "std.d.neb";

    public override bool Run(PassContext context)
    {
        LoadDeclarationFiles(context);
        return true;
    }

    private void LoadDeclarationFiles(PassContext context)
    {
        LoadEmbeddedStdlibDeclarations(context);

        var baseDir = context.Config.ProjectRoot;

        foreach (var globPath in context.Config.Globals)
        {
            var fullPath = Path.IsPathRooted(globPath)
                ? globPath
                : Path.Combine(baseDir, globPath);

            if (Directory.Exists(fullPath))
            {
                foreach (var file in Directory.GetFiles(fullPath, "*.d.neb", SearchOption.AllDirectories))
                    LoadDeclFile(context, file);
            }
            else if (File.Exists(fullPath) && fullPath.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase))
            {
                LoadDeclFile(context, fullPath);
            }
        }

        foreach (var pkg in GetInstalledPackages(context))
        {
            if (!Directory.Exists(pkg.RootPath)) continue;

            var outputRoot = pkg.OutputRoot;
            foreach (var file in InstalledPackages.EnumerateFilesSafely(pkg.RootPath, "*.d.neb"))
            {
                if (IsUnder(file, outputRoot)) continue;
                LoadDeclFile(context, file);
            }
        }
    }

    /// <summary>
    /// Reports whether <paramref name="path"/> sits inside <paramref name="root"/>. Used to leave
    /// a dependency's own build output out of the declaration scan: the declarations it generates
    /// there restate what its sources already declare, and loading both makes every exported
    /// symbol collide with itself.
    /// </summary>
    private static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;

        var full = Path.GetFullPath(path);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads the runtime's built-in .d.neb declarations that are embedded in the
    /// compiler assembly (see Nebra.csproj). Each resource is materialised with a
    /// pseudo path like "&lt;stdlib&gt;/http.d.neb" so diagnostics remain readable.
    /// The base Lua type definitions in <c>std.d.neb</c> can be partially or
    /// fully suppressed via the <c>[stdlib]</c> section of <c>nebra.toml</c>.
    /// </summary>
    private static void LoadEmbeddedStdlibDeclarations(PassContext context)
    {
        var stdlib = context.Config.Stdlib;
        var disabled = stdlib.Disabled.Count > 0
            ? new HashSet<string>(stdlib.Disabled, StringComparer.Ordinal)
            : null;

        var asm = typeof(ResolveLibsPass).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase)) continue;

            var logicalName = ExtractLogicalName(name);
            var isStd = string.Equals(logicalName, StdLogicalName, StringComparison.OrdinalIgnoreCase);
            if (isStd && !stdlib.Enabled) continue;

            string? source;
            try
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                source = reader.ReadToEnd();
            }
            catch
            {
                continue;
            }

            LoadDeclSource(context, $"<stdlib>/{logicalName}", source, isStd ? disabled : null);
        }
    }

    private static string ExtractLogicalName(string resourceName)
    {
        var lastDot = resourceName.LastIndexOf(".d.neb", StringComparison.OrdinalIgnoreCase);
        if (lastDot < 0) return resourceName;
        var prefix = resourceName[..lastDot];
        var slash = prefix.LastIndexOf('.');
        var baseName = slash >= 0 ? prefix[(slash + 1)..] : prefix;
        return baseName + ".d.neb";
    }

    private static IReadOnlyList<InstalledPackage> GetInstalledPackages(PassContext context)
    {
        if (context.Cache.TryGetValue(InstalledPackages.CacheKey, out var cached)
            && cached is IReadOnlyList<InstalledPackage> list)
            return list;
        return InstalledPackages.Discover(context.Config.ProjectRoot);
    }

    private static void LoadDeclFile(PassContext context, string filePath)
    {
        string source;
        try
        {
            source = File.ReadAllText(filePath);
        }
        catch
        {
            return;
        }

        LoadDeclSource(context, filePath, source, disabled: null);
    }

    /// <summary>
    /// Lowers the declaration source once per target package so each gets
    /// its own private IR; the parse itself comes from <see cref="DeclarationParseCache"/>. NameRef/TypeRef nodes are mutated in-place during
    /// later passes (assigning <c>Sym</c>, <c>ResolvedType</c> etc.); sharing a
    /// single IR across packages would race those writes between contexts.
    /// </summary>
    private static void LoadDeclSource(PassContext context, string filePath, string source, HashSet<string>? disabled)
    {
        var pkgsMissingFile = context.Pkgs
            .Where(p => p.Files.All(f => f.Filename != filePath))
            .ToList();
        if (pkgsMissingFile.Count == 0) return;

        var diag = context.Diag;
        var tree = DeclarationParseCache.Parse(source);
        foreach (var pkg in pkgsMissingFile)
        {
            var visitor = new IRVisitor(filePath, context.NodeAlloc, diag, context.Config);
            var ir = visitor.Visit(tree);
            if (ir is not IRScript script) continue;

            if (disabled is { Count: > 0 })
                ApplyStdlibDisable(script, disabled);

            pkg.Files.Add(new PreparsedFile(filePath, source) { Hir = script });
        }
    }

    /// <summary>
    /// Drops top-level declarations and package members named in
    /// <see cref="StdlibSection.Disabled"/>. Bare names match a top-level
    /// function, variable, module, interface, class or enum; a dotted name
    /// (<c>"pkg.member"</c>) drills into the package's underlying interface,
    /// class, module or struct type and removes the named member.
    /// </summary>
    private static void ApplyStdlibDisable(IRScript script, HashSet<string> disabled)
    {
        var typeByName = new Dictionary<string, Decl>(StringComparer.Ordinal);
        foreach (var stmt in script.Body)
        {
            switch (stmt)
            {
                case InterfaceDecl idecl: typeByName[idecl.Name.Name] = idecl; break;
                case ClassDecl cdecl: typeByName[cdecl.Name.Name] = cdecl; break;
            }
        }

        Decl? ResolvePackage(string name)
        {
            foreach (var stmt in script.Body)
            {
                switch (stmt)
                {
                    case DeclareVariableDecl dv when dv.Name.Name == name:
                        if (dv.TypeAnnotation is NamedTypeRef nt && typeByName.TryGetValue(nt.Name.Name, out var t))
                            return t;
                        return dv;
                    case DeclareModuleDecl mdecl when mdecl.ModuleName.Name == name:
                        return mdecl;
                }
            }
            return null;
        }

        foreach (var name in disabled)
        {
            var dot = name.IndexOf('.');
            if (dot < 0) continue;
            var pkg = name[..dot];
            var member = name[(dot + 1)..];
            var target = ResolvePackage(pkg);
            switch (target)
            {
                case InterfaceDecl idecl:
                    idecl.Methods.RemoveAll(m => m.Name.Name == member);
                    idecl.Fields.RemoveAll(f => f.Name.Name == member);
                    break;
                case ClassDecl cdecl:
                    cdecl.Methods.RemoveAll(m => m.Name.Name == member);
                    cdecl.Fields.RemoveAll(f => f.Name.Name == member);
                    cdecl.Accessors.RemoveAll(a => a.Name.Name == member);
                    break;
                case DeclareModuleDecl mdecl:
                    mdecl.Members.RemoveAll(d => d switch
                    {
                        DeclareFunctionDecl df => df.NamePath.Count == 1 && df.NamePath[0].Name == member,
                        DeclareVariableDecl dv => dv.Name.Name == member,
                        _ => false
                    });
                    break;
                case DeclareVariableDecl dv when dv.TypeAnnotation is StructTypeRef str:
                    str.Fields.RemoveAll(f => f.Name.Name == member);
                    break;
            }
        }

        var bare = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in disabled)
            if (name.IndexOf('.') < 0)
                bare.Add(name);

        if (bare.Count == 0) return;

        script.Body.RemoveAll(stmt => stmt switch
        {
            DeclareFunctionDecl df => df.NamePath.Count == 1 && bare.Contains(df.NamePath[0].Name),
            DeclareVariableDecl dv => bare.Contains(dv.Name.Name),
            DeclareModuleDecl mdecl => bare.Contains(mdecl.ModuleName.Name),
            InterfaceDecl idecl => bare.Contains(idecl.Name.Name),
            ClassDecl cdecl => bare.Contains(cdecl.Name.Name),
            EnumDecl edecl => bare.Contains(edecl.Name.Name),
            _ => false
        });
    }
}
