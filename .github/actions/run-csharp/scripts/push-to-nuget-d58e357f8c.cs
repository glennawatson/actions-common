#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;

var packages = Directory.GetFiles(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, "signed"), "*.nupkg");
if (packages is [])
{
    Console.WriteLine("::error::No signed packages were downloaded.");
    return 1;
}

foreach (var package in packages.Order(StringComparer.Ordinal))
{
    if (Process.Run("dotnet", ["nuget", "push", package, "--source", "https://api.nuget.org/v3/index.json", "--api-key", GetEnvironmentVariable("NUGET_API_KEY")!]) is { ExitCode: not 0 } push)
    {
        return push.ExitCode;
    }
}

return 0;
