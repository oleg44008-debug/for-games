using ConverterChecks;

namespace SystemChecks;

internal sealed class CheckSuite
{
    private int _passed;
    private readonly List<string> _failures = [];

    internal async Task RunAsync(string name, Func<FixtureBox, Task> body)
    {
        try
        {
            using var fixture = new FixtureBox();
            await body(fixture);
            _passed++;
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception ex)
        {
            string detail = name + " — " + ex.GetType().Name + ": " + ex.Message;
            _failures.Add(detail);
            Console.WriteLine("FAIL: " + detail);
        }
    }

    internal Task RunAsync(string name, Action<FixtureBox> body)
        => RunAsync(name, box => { body(box); return Task.CompletedTask; });

    internal int Report()
    {
        Console.WriteLine($"RESULT: {_passed} passed; {_failures.Count} failed.");
        foreach (string failure in _failures) Console.WriteLine("FAILURE: " + failure);
        return _failures.Count == 0 ? 0 : 1;
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException) { return; }
        throw new InvalidOperationException("Expected request rejection.");
    }

    internal static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException) { return; }
        throw new InvalidOperationException("Expected request rejection.");
    }
}
