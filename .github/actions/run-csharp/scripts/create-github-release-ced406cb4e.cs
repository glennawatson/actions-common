#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

return Process.Run(
    "gh",
    [
    "release", "create", GetEnvironmentVariable("TAG")!,
    "--repo", GetEnvironmentVariable("REPOSITORY")!,
    "--target", GetEnvironmentVariable("TARGET")!,
    "--title", GetEnvironmentVariable("VERSION")!,
    "--notes-file", "release-notes.md",
    .. Directory.GetFiles("signed", "*.nupkg"),
]).ExitCode;
