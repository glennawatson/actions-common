#!/usr/bin/env dotnet
using static System.Environment;

File.AppendAllLines(GetEnvironmentVariable("GITHUB_ENV")!, ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "DOTNET_NOLOGO=1", "DOTNET_CLI_TELEMETRY_OPTOUT=1"]);
