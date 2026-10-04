#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("PROJECT") ?? string.Empty, System.Environment.GetEnvironmentVariable("SLICE") ?? string.Empty, System.Environment.GetEnvironmentVariable("RUNNER_TEMP/benchmark-ab") ?? string.Empty, System.Environment.GetEnvironmentVariable("WARMUP_COUNT") ?? string.Empty, System.Environment.GetEnvironmentVariable("ITERATION_COUNT") ?? string.Empty];

if (args is not [var project, var slice, var results, var warmupCount, var iterationCount])
{
    Console.WriteLine("::error::Expected the project, slice, results folder, warmup count and iteration count arguments.");
    return 2;
}

string[] iterations = ["--warmupCount", warmupCount, "--iterationCount", iterationCount, "--launchCount", "1"];

string[] filters = ["--filter", .. GetEnvironmentVariable("BENCHMARKS")!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
var workspace = GetEnvironmentVariable("GITHUB_WORKSPACE")!;

RaisePriority();

// Both trees build first, so no measurement shares the machine with a compiler.
foreach (var side in (string[])["base", "head"])
{
    Console.WriteLine($"::group::Build {side}");
    var build = Process.Run("dotnet", ["build", Path.Combine(workspace, "ab", side, "src", project), "-c", "Release"]);
    Console.WriteLine("::endgroup::");
    if (build is { ExitCode: not 0 })
    {
        Console.WriteLine($"::error::Building {side} {project} exited with {build.ExitCode}");
        return build.ExitCode;
    }
}

foreach (var run in (string[][])[["r1", "base"], ["r1", "head"], ["r2", "head"], ["r2", "base"]])
{
    var (order, side) = (run[0], run[1]);

    // BenchmarkDotNet searches below the working directory, which must hold one tree.
    Directory.SetCurrentDirectory(Path.Combine(workspace, "ab", side, "src"));
    Console.WriteLine($"::group::{order} {side}");

    var status = Process.Run(
        "dotnet",
        [
            "run", "--project", Path.Combine(workspace, "ab", side, "src", project), "-c", "Release", "--no-build", "--",
            .. filters, .. iterations, "--artifacts", Path.Combine(results, slice, order, side), "--exporters", "github", "fulljson",
        ]);

    Console.WriteLine("::endgroup::");
    if (status is { ExitCode: not 0 })
    {
        Console.WriteLine($"::error::{order} {side} {slice} exited with {status.ExitCode}");
        return status.ExitCode;
    }
}

return 0;

// Processes started from here inherit the priority.
static void RaisePriority()
{
    if (Process.Run("sudo", ["-n", "renice", "-n", "-20", "-p", $"{ProcessId}"], silent: true) is { ExitCode: 0 })
    {
        return;
    }

    Console.WriteLine("::warning::Could not raise the benchmark priority.");
}
