#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
const int InvalidArgumentsExitCode = 2;

args = [
    System.Environment.GetEnvironmentVariable("BUMP_TAG") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("BUMP_PRERELEASE") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("VERSION_OVERRIDE") ?? string.Empty];

if (args is not [var bumpTag, var bumpPrerelease, var versionOverride])
{
    Console.WriteLine("::error::Expected the bump tag, bump pre-release and version override arguments.");
    return InvalidArgumentsExitCode;
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
