#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
const int InvalidArgumentsExitCode = 2;

args = [
    System.Environment.GetEnvironmentVariable("REPO") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("TAG") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("TARGET") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("TITLE") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("PRERELEASE") ?? string.Empty];

if (args is not [var repository, var tag, var target, var title, var prerelease])
{
    Console.WriteLine("::error::Expected the repository, tag, target, title and pre-release arguments.");
    return InvalidArgumentsExitCode;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

string[] flags = prerelease is "true" ? ["--prerelease"] : [];

return Process.Run(
    "gh",
    [
    "release",
    "create",
    tag,
    "--repo",
    repository,
    "--target",
    target,
    "--title",
    title,
    "--notes-file",
    "release-notes.md",
    .. flags,
    .. Directory.GetFiles("archives", "*.zip")]).ExitCode;
