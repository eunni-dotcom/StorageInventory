using System.Reflection;
using System.Runtime.CompilerServices;
using StorageInventory.Testing;

namespace StorageInventory.Core.Tests;

/// <summary>
/// TEST-O5: Core's public API surface equals v1's. The baseline file was generated from the unmodified v1 build
/// (commit 521d0e4) with <c>StorageInventory.Core.Tests --public-surface &lt;file&gt;</c>, before any C1 change. It is
/// re-baselined exactly once, in C5, to allow the appended <c>ScanPhase.SavingToHistory</c> (G0-O07).
/// </summary>
public static class PublicSurfaceTests
{
    private static string BaselinePath => Path.Combine(AppContext.BaseDirectory, "PublicSurface.v1.txt");

    [Test]
    public static void Core_public_surface_equals_v1()
    {
        var expected = File.ReadAllText(BaselinePath).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var actual = PublicSurface.Describe(typeof(StorageScanResult).Assembly).TrimEnd('\n').Split('\n');
        Assert.SequenceEqual(expected, actual, "Core's public surface (TEST-O5)");
    }

    [Test]
    public static void The_observer_contract_and_the_spool_types_are_not_public()
    {
        // SINK-09 / D-46: nothing of the observer, fan-out or spool layer is part of the public surface (A-13).
        var leaked = typeof(StorageScanResult).Assembly.GetExportedTypes()
            .Where(t => t.Namespace is "StorageInventory.Core.Scanning" or "StorageInventory.Core.Spool")
            .Select(t => t.FullName)
            .ToList();
        Assert.Equal(0, leaked.Count, "public types in Scanning/Spool: " + string.Join(", ", leaked));
    }
}

/// <summary>A deterministic text rendering of an assembly's public and protected surface.</summary>
public static class PublicSurface
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static string Describe(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add(TypeLine(type));
            var members = new List<string>();
            foreach (var m in type.GetMembers(Declared))
            {
                var line = MemberLine(type, m);
                if (line is not null) members.Add("  " + line);
            }
            members.Sort(StringComparer.Ordinal);
            lines.AddRange(members);
        }
        return string.Join("\n", lines) + "\n";
    }

    private static string TypeLine(Type t)
    {
        var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";
        var mods = new List<string>();
        if (t.IsClass && t.IsAbstract && t.IsSealed) mods.Add("static");
        else if (t.IsClass && t.IsAbstract) mods.Add("abstract");
        else if (t.IsClass && t.IsSealed) mods.Add("sealed");
        var bases = new List<string>();
        if (t.IsEnum) bases.Add(Name(Enum.GetUnderlyingType(t)));
        else if (t.BaseType is { } b && b != typeof(object) && b != typeof(ValueType)) bases.Add(Name(b));
        bases.AddRange(t.GetInterfaces().Select(Name).Order(StringComparer.Ordinal));
        return $"{string.Join(" ", mods.Append(kind))} {Name(t)}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}";
    }

    private static string? MemberLine(Type owner, MemberInfo m)
    {
        switch (m)
        {
            case FieldInfo f when Visible(f.IsPublic, f.IsFamily, f.IsFamilyOrAssembly):
                if (owner.IsEnum) return f.IsLiteral ? $"value {f.Name} = {Convert.ToInt64(f.GetRawConstantValue())}" : null;
                return $"{Access(f.IsPublic)} {(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}field {Name(f.FieldType)} {f.Name}{(f.IsLiteral ? " = " + f.GetRawConstantValue() : "")}";
            case ConstructorInfo c when Visible(c.IsPublic, c.IsFamily, c.IsFamilyOrAssembly):
                return $"{Access(c.IsPublic)} {(c.IsStatic ? "static " : "")}ctor({Parameters(c)})";
            case PropertyInfo p:
            {
                var accessors = new List<string>();
                if (p.GetMethod is { } g && Visible(g.IsPublic, g.IsFamily, g.IsFamilyOrAssembly)) accessors.Add(Access(g.IsPublic) + " get");
                if (p.SetMethod is { } s && Visible(s.IsPublic, s.IsFamily, s.IsFamilyOrAssembly))
                {
                    var init = s.ReturnParameter.GetRequiredCustomModifiers().Any(x => x == typeof(IsExternalInit));
                    accessors.Add(Access(s.IsPublic) + (init ? " init" : " set"));
                }
                if (accessors.Count == 0) return null;
                var any = p.GetMethod ?? p.SetMethod!;
                var index = p.GetIndexParameters();
                var indexText = index.Length > 0 ? "[" + string.Join(", ", index.Select(x => Name(x.ParameterType))) + "]" : "";
                return $"{(any.IsStatic ? "static " : "")}property {Name(p.PropertyType)} {p.Name}{indexText} {{ {string.Join("; ", accessors)}; }}";
            }
            case EventInfo e when e.AddMethod is { } add && Visible(add.IsPublic, add.IsFamily, add.IsFamilyOrAssembly):
                return $"{Access(add.IsPublic)} event {Name(e.EventHandlerType!)} {e.Name}";
            case MethodInfo mi when !mi.IsSpecialName || mi.Name.StartsWith("op_", StringComparison.Ordinal):
                if (!Visible(mi.IsPublic, mi.IsFamily, mi.IsFamilyOrAssembly)) return null;
                var mods = new List<string> { Access(mi.IsPublic) };
                if (mi.IsStatic) mods.Add("static");
                if (mi.IsAbstract) mods.Add("abstract");
                else if (mi.IsVirtual && !mi.IsFinal && !owner.IsInterface) mods.Add("virtual");
                var generic = mi.IsGenericMethodDefinition ? "<" + string.Join(", ", mi.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                return $"{string.Join(" ", mods)} method {Name(mi.ReturnType)} {mi.Name}{generic}({Parameters(mi)})";
            case Type:
                return null;   // nested exported types are listed as types of their own
            default:
                return null;
        }
    }

    private static bool Visible(bool isPublic, bool isFamily, bool isFamilyOrAssembly) => isPublic || isFamily || isFamilyOrAssembly;

    private static string Access(bool isPublic) => isPublic ? "public" : "protected";

    private static string Parameters(MethodBase method) => string.Join(", ", method.GetParameters().Select(p =>
    {
        var prefix = p.IsOut ? "out " : p.ParameterType.IsByRef ? (p.IsIn ? "in " : "ref ") : "";
        var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        var optional = p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "";
        return $"{prefix}{Name(type)} {p.Name}{optional}";
    }));

    private static string Name(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        if (t.IsArray) return Name(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        if (Nullable.GetUnderlyingType(t) is { } underlying) return Name(underlying) + "?";
        var name = (t.IsNested ? Name(t.DeclaringType!) + "+" : t.Namespace is null ? "" : t.Namespace + ".") + StripArity(t.Name);
        if (!t.IsGenericType) return name;
        return name + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

}
