using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class HostInheritanceTests
{
    private const string Host = """
        @overrideInherit("$base.Inherit($name)", index = "index", newindex = "newindex")
        @overrideCtor("$class($args)")
        @overrideIs("$value:IsA($class)")
        declare class Prop
            constructor(location: number)
            function SetScale(scale: number): nil
            function GetScale(): number
        end


        """;

    private static ProjectBuild Build(string source)
    {
        return ProjectBuild.Run(null, ("src/main.neb", Host + source));
    }

    private static string Lua(string source)
    {
        var build = Build(source);
        Assert.Empty(build.Errors);
        return build.Lua("src/main.neb");
    }

    [Fact]
    public void ASubclassIsCreatedThroughTheTemplateAndKeepsOutOfTheMetatableOfItsHost()
    {
        var lua = Lua("class Crate extends Prop\nend\n\nprint(Crate)\n");

        Assert.Contains("Crate = Prop.Inherit(\"Crate\")", lua);
        Assert.DoesNotContain("setmetatable", lua);
        Assert.DoesNotContain("Crate.__index", lua);
        Assert.DoesNotContain("Crate.new", lua);
    }

    [Fact]
    public void AConstructorFinishesTheSpawnThroughSuperAndThenSetsItsFieldDefaults()
    {
        var lua = Lua("class Crate extends Prop\n    weight: number = 5\n\n    constructor(location: number)\n        super(location + 1)\n        self:SetScale(2)\n    end\nend\n\nprint(Crate)\n");

        Assert.Contains("function Crate:Constructor(location)", lua);
        var superCall = lua.IndexOf("self.Super:Constructor(location + 1)", StringComparison.Ordinal);
        var fieldDefault = lua.IndexOf("self.weight = 5", StringComparison.Ordinal);
        var body = lua.IndexOf("self:SetScale(2)", StringComparison.Ordinal);
        Assert.True(superCall >= 0 && superCall < fieldDefault && fieldDefault < body, lua);
    }

    [Fact]
    public void AClassWithoutAConstructorForwardsItsArguments()
    {
        var lua = Lua("class Crate extends Prop\n    weight: number = 5\nend\n\nprint(Crate)\n");

        Assert.Contains("function Crate:Constructor(...)", lua);
        Assert.Contains("self.Super:Constructor(...)", lua);
    }

    [Fact]
    public void NewCallsTheClassLikeItsBase()
    {
        var lua = Lua("class Crate extends Prop\nend\n\nprint(new Crate(1))\n");

        Assert.Contains("Crate(1)", lua);
    }

    [Fact]
    public void SuperReachesAHostMethodThroughSuperAndASourceMethodThroughItsClass()
    {
        var lua = Lua("class Crate extends Prop\n    override function SetScale(scale: number): nil\n        super:SetScale(scale * 2)\n    end\nend\n\nclass Box extends Crate\n    override function SetScale(scale: number): nil\n        super:SetScale(scale + 1)\n    end\n\n    override function GetScale(): number\n        return super:GetScale()\n    end\nend\n\nprint(Box)\n");

        Assert.Contains("self.Super:SetScale(scale * 2)", lua);
        Assert.Contains("Crate.SetScale(self, scale + 1)", lua);
        Assert.Contains("return self.Super:GetScale()", lua);
    }

    [Fact]
    public void AnInheritedClassExtendsAnotherThroughItsParent()
    {
        var lua = Lua("class Crate extends Prop\nend\n\nclass Box extends Crate\n    constructor()\n        super(1)\n    end\nend\n\nprint(new Box())\n");

        Assert.Contains("Box = Crate.Inherit(\"Box\")", lua);
        Assert.Contains("Crate.Constructor(self, 1)", lua);
        Assert.Contains("Box()", lua);
    }

    [Fact]
    public void OverloadedConstructorsDispatchFromConstructor()
    {
        var lua = Lua("class Crate extends Prop\n    constructor(location: number)\n        super(location)\n    end\n\n    constructor(name: string)\n        super(0)\n        print(name)\n    end\nend\n\nprint(new Crate(\"a\"))\n");

        Assert.Contains("function Crate:Constructor__number(location)", lua);
        Assert.Contains("function Crate:Constructor__string(name)", lua);
        Assert.Contains("function Crate:Constructor(...)", lua);
        Assert.Contains("Crate.Constructor__string(self, ...)", lua);
    }

    [Fact]
    public void ATemplateOnASourceClassNamesThatClassAlone()
    {
        var lua = Lua("@overrideInherit(\"$base.Inherit('Shared')\")\nclass Crate extends Prop\nend\n\nclass Box extends Crate\nend\n\nprint(Box)\n");

        Assert.Contains("Crate = Prop.Inherit('Shared')", lua);
        Assert.Contains("Box = Crate.Inherit(\"Box\")", lua);
    }

    [Theory]
    [InlineData("    get size(): number\n        return 1\n    end\n")]
    [InlineData("    operator ()(): number\n        return 1\n    end\n")]
    public void MembersNeedingTheirOwnMetatableAreReported(string member)
    {
        var error = Assert.Single(Build("class Crate extends Prop\n" + member + "end\n\nprint(Crate)\n").Errors);

        Assert.Equal(DiagnosticCode.ErrHostInheritedMember, error.Code);
    }

    [Fact]
    public void AnIndexOperatorIsCalledThroughTheHooksOfTheHost()
    {
        var lua = Lua("class Crate extends Prop\n    operator [](key: string): number\n        return self:GetScale()\n    end\n\n    operator []=(key: string, value: number)\n        self:SetScale(value)\n    end\nend\n\nprint(Crate)\n");

        Assert.Contains("Crate.index = Crate.__op_index", lua);
        Assert.Contains("Crate.newindex = Crate.__op_newindex", lua);
        Assert.DoesNotContain("__nebra_fields", lua);
    }

    [Fact]
    public void AnIndexOperatorNeedsAHookOfTheHost()
    {
        var build = ProjectBuild.Run(null, ("src/main.neb", "@overrideInherit(\"$base.Inherit($name)\")\ndeclare class Plain\n    constructor()\nend\n\nclass Own extends Plain\n    operator [](key: string): number\n        return 1\n    end\nend\n\nprint(Own)\n"));

        Assert.Equal(DiagnosticCode.ErrHostInheritedMember, Assert.Single(build.Errors).Code);
    }

    [Fact]
    public void IsTestsThroughTheTemplateOfTheNearestClassThatHasOne()
    {
        var lua = Lua("class Crate extends Prop\nend\n\nlocal thing: any = new Crate(1)\nprint(thing is Crate, thing is Prop)\n");

        Assert.Contains("(thing:IsA(Crate))", lua);
        Assert.Contains("(thing:IsA(Prop))", lua);
    }

    [Fact]
    public void IsEvaluatesAnythingButANameOnce()
    {
        var lua = Lua("class Crate extends Prop\nend\n\nlocal function make(): any\n    return new Crate(1)\nend\n\nprint(make() is Crate)\n");

        Assert.Contains("(function(__value) return (__value:IsA(Crate)) end)(make())", lua);
    }

    [Fact]
    public void AMatchCaseTestsThroughTheTemplate()
    {
        var lua = Lua("class Crate extends Prop\nend\n\nlocal thing: any = new Crate(1)\nlocal found = match thing\n    case crate: Crate then 1\n    case _ then 2\nend\nprint(found)\n");

        Assert.Matches(@"\(\w+:IsA\(Crate\)\)", lua);
    }

    [Fact]
    public void AClassWithoutATemplateKeepsTheMetatableTest()
    {
        var lua = ProjectBuild.Run(null, ("src/main.neb", "class Own\nend\n\nlocal thing: any = new Own()\nprint(thing is Own)\n")).Lua("src/main.neb");

        Assert.DoesNotContain("IsA", lua);
    }

    [Fact]
    public void AClassExtendingADeclaredClassWithoutTheAnnotationIsUnchanged()
    {
        var build = ProjectBuild.Run(null, ("src/main.neb", "declare class Native\n    constructor()\nend\n\nclass Own extends Native\nend\n\nprint(new Own())\n"));

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("setmetatable({}, { __index = Native })", lua);
        Assert.Contains("Own.new()", lua);
    }
}
