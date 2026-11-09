using Nebra.Configuration;

namespace Nebra.Compiler;

/// <summary>
/// Removes what earlier builds left in the output directory, so the output of a deleted source
/// file cannot be loaded or shipped. Only a directory of its own is touched, and files written
/// since the build started, such as by a pre-build script, stay.
/// </summary>
public static class OutputCleaner
{
    /// <summary>
    /// Clears the output directory of the project at <paramref name="projectRoot"/> of everything
    /// older than <paramref name="buildStart"/>. Returns why it was left alone, or <c>null</c>.
    /// </summary>
    public static string? Clean(Config config, string projectRoot, DateTime buildStart)
    {
        var root = Path.GetFullPath(projectRoot);
        var output = Path.GetFullPath(Path.Combine(root, config.Output));
        var source = Path.GetFullPath(Path.Combine(root, config.Source));

        if (!Directory.Exists(output)) return null;

        var refusal = Refusal(root, output, source);
        if (refusal != null) return refusal;

        RemoveStale(output, buildStart);
        return null;
    }

    private static string? Refusal(string root, string output, string source)
    {
        if (!IsBeneath(output, root)) return "it is not a subdirectory of the project";
        if (output == source || IsBeneath(source, output)) return "it contains the source directory";
        if (new DirectoryInfo(output).LinkTarget != null) return "it is a symbolic link";
        if (File.Exists(Path.Combine(output, "nebra.toml"))) return "it contains a nebra.toml";

        return Directory.Exists(Path.Combine(output, "nebra_modules")) ? "it contains installed packages" : null;
    }

    private static bool IsBeneath(string path, string directory)
    {
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static void RemoveStale(string directory, DateTime buildStart)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            RemoveEntry(entry, buildStart);
        }
    }

    private static void RemoveEntry(FileSystemInfo entry, DateTime buildStart)
    {
        if (entry is DirectoryInfo directory && entry.LinkTarget == null)
        {
            RemoveStale(directory.FullName, buildStart);
            if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete();
            return;
        }

        if (entry.LastWriteTimeUtc < buildStart) entry.Delete();
    }
}
