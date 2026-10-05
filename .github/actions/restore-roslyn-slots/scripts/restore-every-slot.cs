#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
const int InvalidArgumentsExitCode = 2;

args = [
    System.Environment.GetEnvironmentVariable("SRC_FOLDER") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("PROJECTS") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("SLOTS") ?? string.Empty];

if (args is not [var srcFolder, var projectList, var slotList])
{
    Console.WriteLine("::error::Expected the source folder, projects and slots arguments.");
    return InvalidArgumentsExitCode;
}

var projects = Split(projectList);

var slots = Split(slotList);

if (projects is [] || slots is [])
{
    Console.WriteLine("::error::Both 'projects' and 'slots' must name at least one entry.");
    return InvalidArgumentsExitCode;
}

Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, srcFolder));

foreach (var slot in slots)
{
    foreach (var project in projects)
    {
        Console.WriteLine($"::group::restore {slot} {project}");
        var restore = Process.Run("dotnet", ["restore", project, $"-p:RoslynVersion={slot}"]);
        Console.WriteLine("::endgroup::");

        if (restore is not { ExitCode: not 0 })
        {
            continue;
        }

        Console.WriteLine($"::error::Restoring {project} for {slot} exited with {restore.ExitCode}");
        return restore.ExitCode;
    }
}

return 0;

static string[] Split(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
