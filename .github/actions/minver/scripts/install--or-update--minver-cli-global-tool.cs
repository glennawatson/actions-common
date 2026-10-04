#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
File.AppendAllLines(GetEnvironmentVariable("GITHUB_PATH")!, [Path.Combine(GetFolderPath(SpecialFolder.UserProfile), ".dotnet", "tools")]);
return Process.Run("dotnet", ["tool", "update", "--global", "minver-cli"]).ExitCode;
