#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);
var packages = Directory.GetFiles("signed", "*.nupkg");
if (packages.Length == 0) return 1;
foreach (var package in packages.Order(StringComparer.Ordinal))
{
    var push = Process.Run("dotnet", ["nuget", "push", package, "--source", "https://api.nuget.org/v3/index.json", "--api-key", GetEnvironmentVariable("NUGET_API_KEY")!]);
    if (push.ExitCode != 0) return push.ExitCode;
}
return 0;
