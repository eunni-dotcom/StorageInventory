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
    /// <c>CommitNoLease(object)</c>, <c>BeginTransactionNoLease(object)</c>; <c>StepNoLease(stmt)</c>; <c>Dup</c> and <c>Twin</c>, two
    /// methods each that share one identity; and the types <c>LeaseBoundCommandRunner</c>, whose instance method <c>Run</c> executes a command
    /// with no lease parameter, and <c>Closures</c>, delegates over a method that creates a directory: <c>InvokedInTheHost</c> runs it
    /// there, <c>Unresolved</c> builds it and then leaves the audit unable to follow it.</summary>
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

        // two methods of one type with one identity (C4R-M05): they differ only by their return type, or by being static or an instance
        // method, which a signature of parameters alone does not tell apart. The audit refuses such a pair.
        var dupVoid = type.DefineMethod("Dup", publicStatic, typeof(void), [typeof(string)]);
        dupVoid.GetILGenerator().Emit(OpCodes.Ret);
        var dupInt = type.DefineMethod("Dup", publicStatic, typeof(int), [typeof(string)]);
        var dupIntIl = dupInt.GetILGenerator();
        dupIntIl.Emit(OpCodes.Ldc_I4_0);
        dupIntIl.Emit(OpCodes.Ret);
        var twinStatic = type.DefineMethod("Twin", publicStatic, typeof(void), [typeof(string)]);
        twinStatic.GetILGenerator().Emit(OpCodes.Ret);
        var twinInstance = type.DefineMethod("Twin", MethodAttributes.Public, typeof(void), [typeof(string)]);
        twinInstance.GetILGenerator().Emit(OpCodes.Ret);

        // delegates: a method that reaches a mutation (a directory creation) bound to an Action<string> by ldftn + newobj
        var closures = module.DefineType(Namespace + "Closures", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var createDirectory = typeof(Directory).GetMethod(nameof(Directory.CreateDirectory), [typeof(string)])!;
        var action = typeof(Action<string>);
        var actionConstructor = action.GetConstructor([typeof(object), typeof(IntPtr)])!;
        var closureTarget = closures.DefineMethod("Target", MethodAttributes.Private | MethodAttributes.Static, typeof(void), [typeof(string)]);
        var closureTargetIl = closureTarget.GetILGenerator();
        closureTargetIl.Emit(OpCodes.Ldarg_0);
        closureTargetIl.Emit(OpCodes.Call, createDirectory);
        closureTargetIl.Emit(OpCodes.Pop);
        closureTargetIl.Emit(OpCodes.Ret);

        // the delegate is built and run inside the host: accepted
        var invoked = closures.DefineMethod("InvokedInTheHost", publicStatic, typeof(void), [typeof(MutationLease)]);
        var invokedIl = invoked.GetILGenerator();
        invokedIl.Emit(OpCodes.Ldnull);
        invokedIl.Emit(OpCodes.Ldftn, closureTarget);
        invokedIl.Emit(OpCodes.Newobj, actionConstructor);
        invokedIl.Emit(OpCodes.Ldstr, "x");
        invokedIl.Emit(OpCodes.Callvirt, action.GetMethod("Invoke")!);
        invokedIl.Emit(OpCodes.Ret);

        // the delegate is built, and then the evaluation stack differs between the two paths that meet: the audit cannot follow the
        // value any further, so it fails closed
        var unresolved = closures.DefineMethod("Unresolved", publicStatic, typeof(void), [typeof(MutationLease)]);
        var unresolvedIl = unresolved.GetILGenerator();
        var join = unresolvedIl.DefineLabel();
        unresolvedIl.Emit(OpCodes.Ldnull);
        unresolvedIl.Emit(OpCodes.Ldftn, closureTarget);
        unresolvedIl.Emit(OpCodes.Newobj, actionConstructor);
        unresolvedIl.Emit(OpCodes.Pop);
        unresolvedIl.Emit(OpCodes.Ldc_I4_0);
        unresolvedIl.Emit(OpCodes.Brtrue_S, join);
        unresolvedIl.Emit(OpCodes.Ldnull);
        unresolvedIl.MarkLabel(join);
        unresolvedIl.Emit(OpCodes.Pop);
        unresolvedIl.Emit(OpCodes.Ret);

        type.CreateType();
        closures.CreateType();
        bound.CreateType();
        assembly.Save(path);
        return path;
    }
}
