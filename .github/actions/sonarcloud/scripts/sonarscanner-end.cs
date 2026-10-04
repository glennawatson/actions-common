#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, GetEnvironmentVariable("WORKING_DIRECTORY")!));
return Process.Run("dotnet", ["sonarscanner", "end", $"/d:sonar.token={GetEnvironmentVariable("SONAR_TOKEN")}"]).ExitCode;
