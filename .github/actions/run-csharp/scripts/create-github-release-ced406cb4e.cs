#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);
return Process.Run("gh",
[
    "release", "create", GetEnvironmentVariable("TAG")!,
    "--repo", GetEnvironmentVariable("REPOSITORY")!,
    "--target", GetEnvironmentVariable("TARGET")!,
    "--title", GetEnvironmentVariable("VERSION")!,
    "--notes-file", "release-notes.md",
    .. Directory.GetFiles("signed", "*.nupkg"),
]).ExitCode;
