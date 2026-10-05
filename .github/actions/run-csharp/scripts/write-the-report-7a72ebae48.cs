#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
#:include ReportComparison.cs
#:include ReportRenderer.cs
#:include Measurement.cs
#:include Row.cs
#:include SliceStatus.cs
#:include BenchmarkPatterns.cs
using System.Globalization;
using System.Text;
using System.Text.Json;
using ActionsCommon.Benchmarks;
using static System.Environment;
const int InvalidArgumentsExitCode = 2;

args = [
    System.Environment.GetEnvironmentVariable("RUNNER_TEMP/benchmark-ab") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("RUNNER_TEMP/benchmark-ab-report.md") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("BASELINE") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("CANDIDATE") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("FILTER") ?? string.Empty];

if (args is not [var results, var reportPath, var baseline, var candidate, var filter])
{
    Console.WriteLine("::error::Expected the results folder, report path, baseline, candidate and filter arguments.");
    return InvalidArgumentsExitCode;
}

// A step summary over 1 MiB is dropped whole.
const int SummaryBudget = 900 * 1024;

const int ExpectedPasses = 4;

const string Slower = "slower";

const string Faster = "faster";

const string TableHeader = "| Benchmark | Base | Head | Head/Base (1st) | Head/Base (2nd) | Allocated base | Allocated head | Result |\n|---|---:|---:|---:|---:|---:|---:|---|";

List<SliceStatus> slices = [];

using var plan = JsonDocument.Parse(GetEnvironmentVariable("SLICES") is { Length: > 0 } planned ? planned : "[]");

var rows = ReportComparison.CompareSlices(results, plan.RootElement, slices);

if (rows.Count == 0)
{
    Console.WriteLine("::error::No benchmark results were exported. Check the slice jobs.");
    return 1;
}

var counts = new Dictionary<string, int>(StringComparer.Ordinal);

List<Row> slowerRows = [];

List<Row> fasterRows = [];

var projects = new SortedDictionary<string, List<Row>>(StringComparer.Ordinal);

var order = new Dictionary<Row, int>();

foreach (var row in rows)
{
    _ = order.TryAdd(row, order.Count);
    counts[row.Result] = counts.GetValueOrDefault(row.Result) + 1;
    if (row.Result == Slower)
    {
        slowerRows.Add(row);
    }

    if (row.Result == Faster)
    {
        fasterRows.Add(row);
    }

    if (!projects.TryGetValue(row.Project, out var projectRows))
    {
        projectRows = [];
        projects.Add(row.Project, projectRows);
    }

    projectRows.Add(row);
}

slowerRows.Sort((left, right) =>
{
    var comparison = Math.Min(right.FirstRatio!.Value, right.SecondRatio!.Value).CompareTo(Math.Min(left.FirstRatio!.Value, left.SecondRatio!.Value));
    return comparison != 0 ? comparison : order[left].CompareTo(order[right]);
});

fasterRows.Sort((left, right) =>
{
    var comparison = Math.Max(left.FirstRatio!.Value, left.SecondRatio!.Value).CompareTo(Math.Max(right.FirstRatio!.Value, right.SecondRatio!.Value));
    return comparison != 0 ? comparison : order[left].CompareTo(order[right]);
});

var overview = new StringBuilder()
    .AppendLine("# A/B benchmark results")
    .AppendLine()
    .AppendLine(CultureInfo.InvariantCulture, $"Baseline `{baseline}`, candidate `{candidate}`, filter `{filter}`.")
    .AppendLine()
    .AppendLine(
    "Each slice runs base, head, head, base on its own runner, so base and head always share hardware. "
    + "A benchmark is faster or slower only when the 99.9% confidence intervals of its means separate in both orders.")
    .AppendLine()
    .AppendLine("| Result | Benchmarks |")
    .AppendLine("|---|---:|");

foreach (var result in (string[])["slower", "faster", "inconclusive", "incomplete", "base only", "head only"])
{
    _ = overview.AppendLine(CultureInfo.InvariantCulture, $"| {result} | {counts.GetValueOrDefault(result)} |");
}

_ = overview.AppendLine();

var slower = ReportRenderer.Section("Slower", slowerRows);

var faster = ReportRenderer.Section("Faster", fasterRows);

var sliceTable = new StringBuilder()
    .AppendLine("## Slices")
    .AppendLine()
    .AppendLine("| Slice | Project | Benchmarks | Passes exported |")
    .AppendLine("|---|---|---:|---|");

foreach (var slice in slices)
{
    var suffix = slice.Passes < ExpectedPasses ? " (timed out or failed)" : string.Empty;
    _ = sliceTable.AppendLine(CultureInfo.InvariantCulture, $"| {slice.Slice} | {slice.Project} | {slice.Benchmarks} | {slice.Passes} of {ExpectedPasses}{suffix} |");
}

_ = sliceTable.AppendLine();

var all = new StringBuilder().AppendLine("## All results").AppendLine();

foreach (var project in projects)
{
    _ = all.AppendLine(CultureInfo.InvariantCulture, $"<details><summary>{project.Key} ({project.Value.Count} benchmarks)</summary>")
        .AppendLine()
        .AppendLine(TableHeader);
    project.Value.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
    foreach (var row in project.Value)
    {
        _ = all.AppendLine(ReportRenderer.RowLine(row));
    }

    _ = all.AppendLine().AppendLine("</details>").AppendLine();
}

var report = string.Concat(overview, slower, faster, sliceTable, all);

File.WriteAllText(reportPath, report);

var summary = report.Length <= SummaryBudget
    ? report
    : string.Concat(overview, slower, faster, sliceTable, "The full results are in the `benchmark-ab-report` artifact.\n");

if (summary.Length > SummaryBudget)
{
    summary = string.Concat(overview, sliceTable, "The report is too large for the run summary. Read it in the `benchmark-ab-report` artifact.\n");
}

File.AppendAllText(GetEnvironmentVariable("GITHUB_STEP_SUMMARY")!, summary);

var missing = 0;

foreach (var slice in slices)
{
    if (slice.Passes >= ExpectedPasses)
    {
        continue;
    }

    Console.WriteLine($"::error::{slice.Slice} exported {slice.Passes} of 4 passes.");
    missing++;
}

return missing > 0 ? 1 : 0;
