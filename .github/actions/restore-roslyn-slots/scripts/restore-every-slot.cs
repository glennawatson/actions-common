#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("SRC_FOLDER") ?? string.Empty, System.Environment.GetEnvironmentVariable("PROJECTS") ?? string.Empty, System.Environment.GetEnvironmentVariable("SLOTS") ?? string.Empty];

if (args is not [var srcFolder, var projectList, var slotList])
{
    Console.WriteLine("::error::Expected the source folder, projects and slots arguments.");
    return 2;
}

string[] projects = Split(projectList), slots = Split(slotList);
if (projects is [] || slots is [])
{
    Console.WriteLine("::error::Both 'projects' and 'slots' must name at least one entry.");
    return 2;
}

Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, srcFolder));

foreach (var slot in slots)
{
    foreach (var project in projects)
    {
        Console.WriteLine($"::group::restore {slot} {project}");
        var restore = Process.Run("dotnet", ["restore", project, $"-p:RoslynVersion={slot}"]);
        Console.WriteLine("::endgroup::");

        if (restore is { ExitCode: not 0 })
        {
            Console.WriteLine($"::error::Restoring {project} for {slot} exited with {restore.ExitCode}");
            return restore.ExitCode;
        }
    }
}

return 0;

static string[] Split(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
