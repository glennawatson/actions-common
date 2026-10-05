#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, GetEnvironmentVariable("WORKING_DIRECTORY")!));

var configuration = GetEnvironmentVariable("CONFIGURATION")!;

var solution = GetEnvironmentVariable("SOLUTION_FILE") ?? string.Empty;

string[] options = [$"-p:Configuration={configuration}"];

string[] target = solution is "" ? [] : [solution];

return Process.Run("dotnet", ["restore", .. options, .. target]).ExitCode;
