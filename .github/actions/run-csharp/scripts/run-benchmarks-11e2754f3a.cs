#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
const int InvalidArgumentsExitCode = 2;

args = [System.Environment.GetEnvironmentVariable("SUITE") ?? string.Empty, System.Environment.GetEnvironmentVariable("FILTER") ?? string.Empty];

if (args is not [var suite, var filter])
{
    Console.WriteLine("::error::Expected the suite and filter arguments.");
    return InvalidArgumentsExitCode;
}

string[] projects = suite switch
{
    "stylesharp" => ["benchmarks/StyleSharp.Analyzers.Benchmarks"],
    "performancesharp" => ["benchmarks/PerformanceSharp.Analyzers.Benchmarks"],
    "securitysharp" => ["benchmarks/SecuritySharp.Analyzers.Benchmarks"],
    "all" => ["benchmarks/StyleSharp.Analyzers.Benchmarks", "benchmarks/PerformanceSharp.Analyzers.Benchmarks", "benchmarks/SecuritySharp.Analyzers.Benchmarks"],
    _ => [],
};

if (projects is [])
{
    Console.WriteLine($"::error::Unknown benchmark suite '{suite}'.");
    return InvalidArgumentsExitCode;
}

var workspace = GetEnvironmentVariable("GITHUB_WORKSPACE")!;

RaisePriority();

Directory.SetCurrentDirectory(Path.Combine(workspace, "bench", "src"));

foreach (var project in projects)
{
    var name = Path.GetFileName(project);
    Console.WriteLine($"::group::{name}");

    var status = Process.Run(
        "dotnet",
        [
            "run", "--project", project, "-c", "Release", "--",
            "--filter", filter, "--artifacts", Path.Combine(workspace, "benchmark-artifacts", name),
            "--exporters", "github", "json", "html", "csv",
        ]);

    Console.WriteLine("::endgroup::");
    if (status is not { ExitCode: not 0 })
    {
        continue;
    }

    Console.WriteLine($"::error::{name} exited with {status.ExitCode}");
    return status.ExitCode;
}

return 0;

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
