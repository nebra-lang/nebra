using Nebra.PackageManager;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Build-scoped pass that enumerates installed packages from <c>nebra_modules/</c> and
/// stashes the result in <see cref="PassContext.Cache"/> under
/// <see cref="InstalledPackages.CacheKey"/>. Consumed by later passes that want to
/// auto-discover types, annotation plugins or module code without requiring explicit
/// entries in <c>nebra.toml</c>.
/// </summary>
public sealed class ResolvePackagesPass() : Pass(PassName, PassScope.PerBuild)
{
    public const string PassName = "ResolvePackages";

    public override bool Run(PassContext context)
    {
        var packages = InstalledPackages.Discover(context.Config.ProjectRoot);
        context.Cache[InstalledPackages.CacheKey] = packages;
        return true;
    }
}
