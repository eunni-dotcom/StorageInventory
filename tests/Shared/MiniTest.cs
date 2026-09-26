using System.Diagnostics;
using System.Reflection;

namespace StorageInventory.Testing;

// A deliberately tiny test runner so the test projects need no third-party packages (no xUnit/NUnit download).
// Test methods: public static, marked [Test], returning void or Task, in a public class.

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public sealed class SkipException(string reason) : Exception(reason);

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message = "expected false") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? context = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{(context is null ? "" : context + ": ")}expected <{expected}> but was <{actual}>");
        }
    }

    public static T NotNull<T>(T? value, string message = "expected a value") where T : class
        => value ?? throw new AssertionException(message);

    public static void Null(object? value, string message = "expected null")
    {
        if (value is not null) throw new AssertionException($"{message}: was <{value}>");
    }

    public static void Contains(string expectedSubstring, string? actual)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new AssertionException($"expected text containing <{expectedSubstring}> but was <{actual}>");
        }
    }

    public static TException Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException ex) { return ex; }
        catch (Exception ex) { throw new AssertionException($"expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertionException($"expected {typeof(TException).Name} but nothing was thrown");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? context = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        for (var i = 0; i < Math.Max(e.Count, a.Count); i++)
        {
            if (i >= e.Count || i >= a.Count || !EqualityComparer<T>.Default.Equals(e[i], a[i]))
            {
                var ev = i < e.Count ? e[i]?.ToString() : "<missing>";
                var av = i < a.Count ? a[i]?.ToString() : "<missing>";
                throw new AssertionException($"{(context is null ? "" : context + ": ")}sequences differ at index {i}: expected <{ev}> but was <{av}> (counts {e.Count} vs {a.Count})");
            }
        }
    }

    public static void Skip(string reason) => throw new SkipException(reason);
}

public static class TestRunner
{
    /// <summary>Runs every [Test] in the assembly whose "Class.Method" name contains one of the non-flag arguments
    /// (or all tests when there are none). Returns the process exit code: 0 = no failures.</summary>
    public static int Run(Assembly assembly, string[] args)
    {
        var filters = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var tests = assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => (Type: t, Method: m)))
            .Where(x => x.Method.GetCustomAttribute<TestAttribute>() is not null)
            .Select(x => (Name: $"{x.Type.Name}.{x.Method.Name}", x.Method))
            .Where(x => filters.Length == 0 || filters.Any(f => x.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        int pass = 0, fail = 0, skip = 0;
        var total = Stopwatch.StartNew();
        foreach (var (name, method) in tests)
        {
            var sw = Stopwatch.StartNew();
            string outcome;
            string detail = "";
            try
            {
                var returned = method.Invoke(null, null);
                if (returned is Task task) task.GetAwaiter().GetResult();
                outcome = "PASS";
                pass++;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                if (inner is SkipException) { outcome = "SKIP"; skip++; detail = inner.Message; }
                else { outcome = "FAIL"; fail++; detail = inner is AssertionException ? inner.Message : inner.ToString(); }
            }
            var colour = outcome switch { "PASS" => ConsoleColor.Green, "SKIP" => ConsoleColor.Yellow, _ => ConsoleColor.Red };
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = colour;
            Console.Write(outcome);
            Console.ForegroundColor = previous;
            Console.WriteLine($"  {name} ({sw.ElapsedMilliseconds} ms){(detail.Length > 0 ? "  -- " + detail : "")}");
        }
        Console.WriteLine();
        Console.WriteLine($"RESULT {assembly.GetName().Name}: {pass} PASS, {fail} FAIL, {skip} SKIP in {total.Elapsed.TotalSeconds:0.0} s");
        return fail == 0 ? 0 : 1;
    }
}
