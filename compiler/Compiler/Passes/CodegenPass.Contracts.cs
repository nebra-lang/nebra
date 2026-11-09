using System.Text;
using Nebra.Compiler.Annotations;
using Nebra.Compiler.Codegen;
using Nebra.IR;

using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class CodegenPass
{
    /// <summary>
    /// The Lua function testing a value against <paramref name="contract"/>. Each lives in one
    /// table of the chunk, so contracts may refer to each other in any order, themselves included.
    /// </summary>
    private string ContractValidator(LuaGenerator gen, InterfaceType contract)
    {
        var validators = gen.RequireHelper("contracts", name => $"local {name} = {{}}");
        var validator = gen.RequireHelper($"contract_{contract.ID.Value}", name => ContractValidatorBody(gen, validators, name, contract));
        return $"{validators}.{validator}";
    }

    private string ContractValidatorBody(LuaGenerator gen, string validators, string name, InterfaceType contract)
    {
        var members = Type.BaseInterfacesOf(contract).Prepend(contract).ToList();
        var fields = members.SelectMany(member => member.Fields).GroupBy(field => field.Key).Select(group => group.First()).ToList();
        var methods = members.SelectMany(member => member.Methods.Keys).Distinct().ToList();
        var keys = fields.Select(field => field.Key).Concat(methods).Select(key => $"k ~= {LiteralValue.Of(key).Text}").ToList();

        var body = new StringBuilder();
        body.AppendLine($"{validators}.{name} = function(v)");
        body.AppendLine("  if type(v) ~= \"table\" then return false end");
        if (keys.Count > 0)
        {
            body.AppendLine($"  for k in pairs(v) do if {string.Join(" and ", keys)} then return false end end");
        }
        else
        {
            body.AppendLine("  if next(v) ~= nil then return false end");
        }

        foreach (var field in fields)
        {
            AppendContractField(gen, body, field.Key, field.Value.Type, contract.RulesOf(field.Key));
        }

        foreach (var method in methods)
        {
            body.AppendLine($"  if type(v[{LiteralValue.Of(method).Text}]) ~= \"function\" then return false end");
        }

        body.AppendLine("  return true");
        body.Append("end");
        return body.ToString();
    }

    private void AppendContractField(LuaGenerator gen, StringBuilder body, string name, Type type, IReadOnlyList<ContractRule> rules)
    {
        body.AppendLine("  do");
        body.AppendLine($"    local f = v[{LiteralValue.Of(name).Text}]");

        var typeCheck = ContractValueCheck(gen, type, "f");
        if (typeCheck != null)
        {
            body.AppendLine($"    if not ({typeCheck}) then return false end");
        }

        if (rules.Count > 0)
        {
            var conditions = string.Join(" and ", rules.Select(rule => $"({rule.LuaCondition("f")})"));
            body.AppendLine($"    if f ~= nil and not ({conditions}) then return false end");
        }

        body.AppendLine("  end");
    }

    /// <summary>A Lua condition testing a field against its type, or <c>null</c> when nothing can be tested at runtime.</summary>
    private string? ContractValueCheck(LuaGenerator gen, Type type, string value)
    {
        switch (type)
        {
            case InterfaceType { IsContract: true } nested:
                return $"{ContractValidator(gen, nested)}({value})";
            case LiteralType literal:
                return $"{value} == {literal.Value.Text}";
            case UnionType union:
            {
                var checks = union.Types.Select(member => ContractValueCheck(gen, member, value)).ToList();
                return checks.Any(check => check == null) ? null : "(" + string.Join(" or ", checks) + ")";
            }
            default:
                return RuntimeCheck(gen, type, value, false);
        }
    }

    private void EmitContractCheck(PassContext ctx, PackageContext pkg, LuaGenerator gen, InterfaceType contract, Expr value)
    {
        gen.Write(ContractValidator(gen, contract) + "(");
        EmitExpr(ctx, pkg, gen, value);
        gen.Write(")");
    }
}
