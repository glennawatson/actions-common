// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Text.Json;

namespace ActionsCommon.Benchmarks;

/// <summary>Loads and compares benchmark export data.</summary>
internal static class ReportComparison
{
    /// <summary>The verdict for separated intervals with a slower candidate.</summary>
    private const string Slower = "slower";

    /// <summary>The verdict for separated intervals with a faster candidate.</summary>
    private const string Faster = "faster";

    /// <summary>Compares two confidence intervals.</summary>
    /// <param name = "before">The first baseline measurement.</param>
    /// <param name = "after">The first candidate measurement.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static string Verdict(in Measurement before, in Measurement after)
    {
        if (after.Lower > before.Upper)
        {
            return Slower;
        }

        return after.Upper < before.Lower ? Faster : "unresolved";
    }

    /// <summary>Loads exported benchmark measurements.</summary>
    /// <param name = "folder">The export folder.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static Dictionary<string, Measurement> Load(string folder)
    {
        var results = new Dictionary<string, Measurement>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
        {
            return results;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*full.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var benchmark in document.RootElement.GetProperty("Benchmarks").EnumerateArray())
            {
                if (!benchmark.TryGetProperty("Statistics", out var statistics) || statistics.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var allocated = benchmark.TryGetProperty("Memory", out var memory) && memory.ValueKind == JsonValueKind.Object ? memory.GetProperty("BytesAllocatedPerOperation").GetInt64() : -1;
                var interval = statistics.GetProperty("ConfidenceInterval");
                results[Key(benchmark)] = new(statistics.GetProperty("Mean").GetDouble(), interval.GetProperty("Lower").GetDouble(), interval.GetProperty("Upper").GetDouble(), allocated);
            }
        }

        return results;
    }

    /// <summary>Reads the benchmark name and characteristics.</summary>
    /// <param name = "benchmark">The exported benchmark data.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static string Key(JsonElement benchmark)
    {
        var name = benchmark.GetProperty("FullName").GetString();
        var characteristics = BenchmarkPatterns.MyRegex.Match(benchmark.GetProperty("DisplayInfo").GetString() ?? string.Empty).Groups[1].Value;
        return characteristics is "" ? name! : $"{name} ({characteristics})";
    }

    /// <summary>Compares the exported measurements for each slice.</summary>
    /// <param name = "results">The exported results folder.</param>
    /// <param name = "plan">The planned slices.</param>
    /// <param name = "slices">The slice export counts.</param>
    /// <returns>The comparison data or formatted value.</returns>
    internal static List<Row> CompareSlices(string results, JsonElement plan, List<SliceStatus> slices)
    {
        string[] passes = ["r1/base", "r1/head", "r2/head", "r2/base"];
        List<Row> rows = [];
        foreach (var entry in plan.EnumerateArray())
        {
            CompareSlice(results, entry, slices, rows, passes);
        }

        return rows;
    }

    /// <summary>Compares a benchmark across both execution orders.</summary>
    /// <param name = "project">The benchmark project name.</param>
    /// <param name = "key">The benchmark key.</param>
    /// <param name = "measured">The measurements in execution order.</param>
    /// <returns>The comparison data or formatted value.</returns>
    private static Row CompareBenchmark(string project, string key, Dictionary<string, Measurement>[] measured)
    {
        var (firstBase, firstHead, secondHead, secondBase) = (measured[0], measured[1], measured[2], measured[3]);
        var hasFirstBase = firstBase.TryGetValue(key, out var before);
        var hasSecondBase = secondBase.TryGetValue(key, out var beforeAgain);
        var hasFirstHead = firstHead.TryGetValue(key, out var after);
        var hasSecondHead = secondHead.TryGetValue(key, out var afterAgain);
        if (hasFirstBase && hasSecondBase && hasFirstHead && hasSecondHead)
        {
            return CompleteRow(project, key, before, after, beforeAgain, afterAgain);
        }

        var availableBase = AvailableMeasurement(hasFirstBase, before, hasSecondBase, beforeAgain);
        var availableHead = AvailableMeasurement(hasFirstHead, after, hasSecondHead, afterAgain);
        return new(project, key, availableBase, availableHead, null, null, (hasFirstBase || hasSecondBase, hasFirstHead || hasSecondHead) switch
        {
            (true, true) => "incomplete",
            (true, false) => "base only",
            _ => "head only",
        });
    }

    /// <summary>Selects the first available measurement.</summary>
    /// <param name="hasFirst">Whether the first measurement exists.</param>
    /// <param name="first">The first measurement.</param>
    /// <param name="hasSecond">Whether the second measurement exists.</param>
    /// <param name="second">The second measurement.</param>
    /// <returns>The available measurement, or null.</returns>
    private static Measurement? AvailableMeasurement(bool hasFirst, in Measurement first, bool hasSecond, in Measurement second)
    {
        if (hasFirst)
        {
            return first;
        }

        return hasSecond ? second : null;
    }

    /// <summary>Adds comparison rows and export counts for a slice.</summary>
    /// <param name = "results">The exported results folder.</param>
    /// <param name = "entry">The planned slice.</param>
    /// <param name = "slices">The slice export counts.</param>
    /// <param name = "rows">The comparison rows.</param>
    /// <param name = "passes">The execution order folders.</param>
    private static void CompareSlice(string results, JsonElement entry, List<SliceStatus> slices, List<Row> rows, ReadOnlySpan<string> passes)
    {
        var slice = entry.GetProperty("slice").GetString()!;
        var project = Path.GetFileName(entry.GetProperty("project").GetString()!);
        var benchmarks = entry.GetProperty("benchmarks").GetInt32();
        var measured = new Dictionary<string, Measurement>[passes.Length];
        var exported = 0;
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < passes.Length; index++)
        {
            measured[index] = Load(Path.Combine(results, slice, passes[index]));
            if (measured[index].Count > 0)
            {
                exported++;
            }

            keys.UnionWith(measured[index].Keys);
        }

        slices.Add(new(slice, project, benchmarks, exported));
        foreach (var key in keys)
        {
            rows.Add(CompareBenchmark(project, key, measured));
        }
    }

    /// <summary>Compares four available measurements.</summary>
    /// <param name = "project">The benchmark project name.</param>
    /// <param name = "key">The benchmark key.</param>
    /// <param name = "before">The first baseline measurement.</param>
    /// <param name = "after">The first candidate measurement.</param>
    /// <param name = "beforeAgain">The second baseline measurement.</param>
    /// <param name = "afterAgain">The second candidate measurement.</param>
    /// <returns>The comparison data or formatted value.</returns>
    private static Row CompleteRow(string project, string key, in Measurement before, in Measurement after, in Measurement beforeAgain, in Measurement afterAgain)
    {
        var result = (Verdict(before, after), Verdict(beforeAgain, afterAgain)) switch
        {
            (Slower, Slower) => Slower,
            (Faster, Faster) => Faster,
            _ => "inconclusive",
        };
        return new(project, key, before, after, after.Mean / before.Mean, afterAgain.Mean / beforeAgain.Mean, result);
    }
}
