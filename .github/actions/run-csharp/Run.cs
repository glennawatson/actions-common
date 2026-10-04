#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;

var script = GetEnvironmentVariable("SCRIPT_FILE");
var sharedScript = GetEnvironmentVariable("SHARED_SCRIPT");
if (!string.IsNullOrWhiteSpace(sharedScript))
{
    script = Path.Combine(GetEnvironmentVariable("ACTION_PATH")!, "scripts", sharedScript);
}
if (string.IsNullOrWhiteSpace(script))
{
    Console.WriteLine("::error::A C# script path is required.");
    return 2;
}

var workspace = GetEnvironmentVariable("GITHUB_WORKSPACE")!;
var path = Path.GetFullPath(script, workspace);
var arguments = (GetEnvironmentVariable("SCRIPT_ARGUMENTS") ?? string.Empty)
    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
return Process.Run("dotnet", ["run", "--file", path, "--", .. arguments]).ExitCode;
