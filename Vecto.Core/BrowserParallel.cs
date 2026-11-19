namespace Vecto.Core;

/// <summary>
/// Keep original engine calls unchanged. Single-thread on WASM, native TPL elsewhere.
/// </summary>
internal static class Parallel
{
    public static void For(int fromInclusive, int toExclusive, ParallelOptions options, Action<int> body)
    {
        if (!OperatingSystem.IsBrowser())
        {
            System.Threading.Tasks.Parallel.For(fromInclusive, toExclusive, options, body);
            return;
        }
        for (int i = fromInclusive; i < toExclusive; i++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            body(i);
        }
    }

    public static void ForEach<T>(IEnumerable<T> items, ParallelOptions options, Action<T> body)
    {
        if (!OperatingSystem.IsBrowser())
        {
            System.Threading.Tasks.Parallel.ForEach(items, options, body);
            return;
        }
        foreach (var item in items)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            body(item);
        }
    }
}
