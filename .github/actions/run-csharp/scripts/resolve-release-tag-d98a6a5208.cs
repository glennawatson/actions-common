#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("BUMP_TAG") ?? string.Empty, System.Environment.GetEnvironmentVariable("BUMP_PRERELEASE") ?? string.Empty, System.Environment.GetEnvironmentVariable("VERSION_OVERRIDE") ?? string.Empty];

if (args is not [var bumpTag, var bumpPrerelease, var versionOverride])
{
    Console.WriteLine("::error::Expected the bump tag, bump pre-release and version override arguments.");
    return 2;
}

var outputs = GetEnvironmentVariable("GITHUB_OUTPUT")!;
if (versionOverride is "")
{
    File.AppendAllLines(outputs, [$"tag={bumpTag}", $"prerelease={(bumpPrerelease is "" ? "false" : bumpPrerelease)}"]);
    return 0;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);
if (Process.Run("git", ["rev-parse", "-q", "--verify", $"refs/tags/{versionOverride}"], silent: true) is { ExitCode: 0 })
{
    Console.WriteLine($"::error::Tag {versionOverride} already exists. Refusing to re-release an existing version.");
    return 1;
}

File.AppendAllLines(outputs, [$"tag={versionOverride}", $"prerelease={(versionOverride.Contains('-') ? "true" : "false")}"]);
return 0;
