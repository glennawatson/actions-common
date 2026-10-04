#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("REPO") ?? string.Empty, System.Environment.GetEnvironmentVariable("TAG") ?? string.Empty, System.Environment.GetEnvironmentVariable("TARGET") ?? string.Empty, System.Environment.GetEnvironmentVariable("TITLE") ?? string.Empty, System.Environment.GetEnvironmentVariable("PRERELEASE") ?? string.Empty];

if (args is not [var repository, var tag, var target, var title, var prerelease])
{
    Console.WriteLine("::error::Expected the repository, tag, target, title and pre-release arguments.");
    return 2;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

string[] flags = prerelease is "true" ? ["--prerelease"] : [];
return Process.Run(
    "gh",
    ["release", "create", tag, "--repo", repository, "--target", target, "--title", title, "--notes-file", "release-notes.md", .. flags, .. Directory.GetFiles("archives", "*.zip")]).ExitCode;
