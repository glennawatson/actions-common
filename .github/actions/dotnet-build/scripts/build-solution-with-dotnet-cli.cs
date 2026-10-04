#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, GetEnvironmentVariable("WORKING_DIRECTORY")!));
var configuration = GetEnvironmentVariable("CONFIGURATION")!;
var solution = GetEnvironmentVariable("SOLUTION_FILE") ?? string.Empty;
string[] options = ["--no-restore", "--configuration", configuration];
string[] target = solution is "" ? [] : [solution];
return Process.Run("dotnet", ["build", .. options, .. target]).ExitCode;
