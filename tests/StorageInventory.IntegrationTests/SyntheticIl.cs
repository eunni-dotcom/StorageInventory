using System.Reflection;
using System.Reflection.Emit;
using StorageInventory.Library;

namespace StorageInventory.IntegrationTests;

/// <summary>
/// Methods that C# in this test project cannot express (an indirect call, <c>calli</c>) or cannot compile (a call on a SQLite type, which
/// the Library hides from its consumers with <c>PrivateAssets="compile"</c>) are written as IL into a throw-away assembly
/// (<see cref="PersistedAssemblyBuilder"/>), which <see cref="IlAudit"/> then reads like any other. The assembly is never loaded.
/// </summary>
internal static class SyntheticIl
{
    internal const string Namespace = "Synth.";

    private static Type Sqlite(string name) => Type.GetType($"Microsoft.Data.Sqlite.{name}, Microsoft.Data.Sqlite", throwOnError: true)!;

    /// <summary>Writes the assembly and returns its path. Methods (all public static unless noted):
    /// <c>CalliNoLease</c>, <c>CalliWithLease(lease)</c>; <c>CommandNoLease(object)</c>, <c>CommandWithLease(lease, object)</c>;
    /// <c>CommitNoLease(object)</c>, <c>BeginTransactionNoLease(object)</c>; <c>StepNoLease(stmt)</c>; and the type
    /// <c>LeaseBoundCommandRunner</c>, whose instance method <c>Run</c> executes a command with no lease parameter.</summary>
    internal static string Build(string directory)
    {
        var name = "SynthFixtures_" + Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(directory, name + ".dll");
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(name);
        const MethodAttributes publicStatic = MethodAttributes.Public | MethodAttributes.Static;

        var type = module.DefineType(Namespace + "Cases", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var target = type.DefineMethod("Target", MethodAttributes.Private | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        target.GetILGenerator().Emit(OpCodes.Ret);

        void Calli(string methodName, Type[] parameters)
        {
            var m = type.DefineMethod(methodName, publicStatic, typeof(void), parameters);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Ldftn, target);
            il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, typeof(void), Type.EmptyTypes, null);
            il.Emit(OpCodes.Ret);
        }
        Calli("CalliNoLease", Type.EmptyTypes);
        Calli("CalliWithLease", [typeof(MutationLease)]);

        void CallOn(string methodName, Type[] parameters, Type owner, string member, int objectArgument, bool virtualCall = true)
        {
            var m = type.DefineMethod(methodName, publicStatic, typeof(void), parameters);
            var il = m.GetILGenerator();
            var callee = owner.GetMethod(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, Type.EmptyTypes)
                ?? owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).First(x => x.Name == member);
            il.Emit(OpCodes.Ldarg, objectArgument);
            if (!owner.IsValueType) il.Emit(OpCodes.Castclass, owner);
            for (var i = 0; i < callee.GetParameters().Length; i++) il.Emit(OpCodes.Ldnull);
            il.Emit(virtualCall ? OpCodes.Callvirt : OpCodes.Call, callee);
            if (callee.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        }
        CallOn("CommandNoLease", [typeof(object)], Sqlite("SqliteCommand"), "ExecuteNonQuery", 0);
        CallOn("CommandWithLease", [typeof(MutationLease), typeof(object)], Sqlite("SqliteCommand"), "ExecuteNonQuery", 1);
        CallOn("CommitNoLease", [typeof(object)], Sqlite("SqliteTransaction"), "Commit", 0);
        CallOn("BeginTransactionNoLease", [typeof(object)], Sqlite("SqliteConnection"), "BeginTransaction", 0);

        var statementType = Type.GetType("SQLitePCL.sqlite3_stmt, SQLitePCLRaw.core", throwOnError: true)!;
        var raw = Type.GetType("SQLitePCL.raw, SQLitePCLRaw.core", throwOnError: true)!.GetMethod("sqlite3_step", [statementType])!;
        var step = type.DefineMethod("StepNoLease", publicStatic, typeof(void), [statementType]);
        var stepIl = step.GetILGenerator();
        stepIl.Emit(OpCodes.Ldarg_0);
        stepIl.Emit(OpCodes.Call, raw);
        stepIl.Emit(OpCodes.Pop);
        stepIl.Emit(OpCodes.Ret);

        // a type that holds a lease and runs a command on an instance method that takes no lease: the earlier audit's "lease-bound type"
        var bound = module.DefineType(Namespace + "LeaseBoundCommandRunner", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        bound.DefineField("_lease", typeof(MutationLease), FieldAttributes.Private);
        var run = bound.DefineMethod("Run", MethodAttributes.Public, typeof(void), [typeof(object)]);
        var runIl = run.GetILGenerator();
        runIl.Emit(OpCodes.Ldarg_1);
        runIl.Emit(OpCodes.Castclass, Sqlite("SqliteCommand"));
        runIl.Emit(OpCodes.Callvirt, Sqlite("SqliteCommand").GetMethod("ExecuteNonQuery", Type.EmptyTypes)!);
        runIl.Emit(OpCodes.Pop);
        runIl.Emit(OpCodes.Ret);
        var ctor = bound.DefineDefaultConstructor(MethodAttributes.Public);
        _ = ctor;

        type.CreateType();
        bound.CreateType();
        assembly.Save(path);
        return path;
    }
}
