#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);
var result = Process.RunAndCaptureText("git", ["rev-parse", "HEAD"]);
if (result.ExitStatus.ExitCode != 0) return result.ExitStatus.ExitCode;
File.AppendAllLines(GetEnvironmentVariable("GITHUB_OUTPUT")!, [$"sha={result.StandardOutput.Trim()}"]);
return 0;
