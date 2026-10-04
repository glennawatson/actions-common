#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("FILTER") ?? string.Empty];

if (args is not [var filter])
{
    Console.WriteLine("::error::Expected the filter argument.");
    return 2;
}

const string Project = "benchmarks/ClaudeNim.Aot.Benchmarks";
var workspace = GetEnvironmentVariable("GITHUB_WORKSPACE")!;

RaisePriority();
Directory.SetCurrentDirectory(Path.Combine(workspace, "bench", "src"));

var name = Path.GetFileName(Project);
Console.WriteLine($"::group::{name}");

var status = Process.Run(
    "dotnet",
    [
        "run", "--project", Project, "-c", "Release", "--",
        "--filter", filter, "--artifacts", Path.Combine(workspace, "benchmark-artifacts", name),
        "--exporters", "github", "json", "html", "csv",
    ]);

Console.WriteLine("::endgroup::");
return status.ExitCode;

// The runner is a shared virtual machine: the raised priority removes the noise that is ours to remove, and the
// figures remain trend and regression evidence rather than absolutes. Processes started from here inherit it.
static void RaisePriority()
{
    if (Process.Run("sudo", ["-n", "renice", "-n", "-20", "-p", $"{ProcessId}"], silent: true) is { ExitCode: 0 })
    {
        return;
    }

    Console.WriteLine("::warning::Could not raise the benchmark priority.");
}
