using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ImportSideTests
{
    private const string Value = "export local value = 1\n";

    private static List<DiagnosticCode> Errors(params (string Name, string Source)[] files)
    {
        var build = ProjectBuild.Run(config =>
        {
            config.Sides["src/logic/**"] = ["shared"];
            config.Sides["src/client/**"] = ["client", "shared"];
            config.Sides["src/server/**"] = ["server", "shared"];
        }, files);
        return build.Errors.Select(error => error.Code).ToList();
    }

    [Fact]
    public void SharedCodeCannotImportFromAServerModule()
    {
        Assert.Equal([DiagnosticCode.ErrImportWrongSide], Errors(("src/server/secret.neb", Value), ("src/logic/recipes.neb", "import { value } from \"server/secret\"\nprint(value)\n")));
    }

    [Fact]
    public void AClientModuleCannotImportFromAServerModule()
    {
        Assert.Equal([DiagnosticCode.ErrImportWrongSide], Errors(("src/server/secret.neb", Value), ("src/client/ui.neb", "import { value } from \"server/secret\"\nprint(value)\n")));
    }

    [Fact]
    public void ASideEffectImportIsChecked()
    {
        Assert.Equal([DiagnosticCode.ErrImportWrongSide], Errors(("src/server/secret.neb", "print(1)\n"), ("src/logic/recipes.neb", "import \"server/secret\"\n")));
    }

    [Fact]
    public void AReExportIsChecked()
    {
        Assert.Equal([DiagnosticCode.ErrImportWrongSide], Errors(("src/server/secret.neb", Value), ("src/logic/recipes.neb", "export { value } from \"server/secret\"\n")));
    }

    [Fact]
    public void AGlobImportIsChecked()
    {
        Assert.Equal([DiagnosticCode.ErrImportWrongSide], Errors(("src/server/secret.neb", "print(1)\n"), ("src/logic/recipes.neb", "import \"server/*.neb\"\n")));
    }

    [Theory]
    [InlineData("src/client/ui.neb", "logic/shared")]
    [InlineData("src/server/main.neb", "logic/shared")]
    [InlineData("src/logic/other.neb", "logic/shared")]
    public void ASharedModuleCanBeImportedFromAnywhere(string importer, string module)
    {
        Assert.Empty(Errors(("src/logic/shared.neb", Value), (importer, $"import {{ value }} from \"{module}\"\nprint(value)\n")));
    }

    [Fact]
    public void ASideCanImportItsOwnModules()
    {
        Assert.Empty(Errors(("src/server/secret.neb", Value), ("src/server/main.neb", "import { value } from \"server/secret\"\nprint(value)\n")));
    }

    [Fact]
    public void ATypeImportMayCrossSides()
    {
        Assert.Empty(Errors(("src/server/secret.neb", "export class Secret\nend\n"), ("src/logic/recipes.neb", "import type { Secret } from \"server/secret\"\nlocal value: Secret\nprint(value)\n")));
    }

    [Fact]
    public void AModuleOutsideEverySideCanBeImported()
    {
        Assert.Empty(Errors(("src/util.neb", Value), ("src/logic/recipes.neb", "import { value } from \"util\"\nprint(value)\n")));
    }

    [Fact]
    public void AFileOutsideEverySideCanImportAnything()
    {
        Assert.Empty(Errors(("src/server/secret.neb", Value), ("src/tools.neb", "import { value } from \"server/secret\"\nprint(value)\n")));
    }
}
