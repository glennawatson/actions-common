#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("FILTER") ?? string.Empty];

if (args is not [var filter])
{
    Console.WriteLine("::error::Expected the filter argument.");
    return;
}

// A step summary is dropped whole once it passes 1 MiB, so the tables stop before that and the artifact carries the rest.
const long Budget = 900 * 1024;

var summary = GetEnvironmentVariable("GITHUB_STEP_SUMMARY")!;
var root = Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, "benchmark-artifacts");

// With a revision input the triggering commit is not the measured one, so the commit is read from the measured tree.
var commit = Process.RunAndCaptureText("git", ["-C", Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, "bench"), "rev-parse", "HEAD"]).StandardOutput.Trim();

File.AppendAllLines(
    summary,
    [
        "# Benchmark results",
        "",
        $"Filter `{filter}`, commit `{commit}`.",
        "",
        "The runner is shared, so read these for trend and regression rather than as absolute figures.",
        "Full JSON, HTML and CSV exports are attached to this run as `benchmark-reports`.",
        "",
    ]);

string[] reports = Directory.Exists(root) ? [.. Directory.GetFiles(root, "*-report-github.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal)] : [];
if (reports is [])
{
    File.AppendAllLines(summary, ["No benchmark reports were exported."]);
    return;
}

List<string> skipped = [];
foreach (var report in reports)
{
    var title = Path.GetFileName(report).Replace("-report-github.md", string.Empty);
    if (new FileInfo(summary).Length + new FileInfo(report).Length > Budget)
    {
        skipped.Add(title);
        continue;
    }

    File.AppendAllLines(summary, [$"<details><summary>{title}</summary>", "", .. File.ReadLines(report), "", "</details>", ""]);
}

if (skipped is not [])
{
    File.AppendAllLines(summary, ["", $"Omitted here to stay inside the summary limit: {string.Join(", ", skipped)}. Read them in the benchmark-reports artifact.", ""]);
}
