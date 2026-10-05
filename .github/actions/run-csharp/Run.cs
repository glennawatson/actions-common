#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
const int InvalidArgumentsExitCode = 2;

var script = GetEnvironmentVariable("SCRIPT_FILE");

var sharedScript = GetEnvironmentVariable("SHARED_SCRIPT");

if (!string.IsNullOrWhiteSpace(sharedScript))
{
    script = Path.Combine(GetEnvironmentVariable("ACTION_PATH")!, "scripts", sharedScript);
}

if (string.IsNullOrWhiteSpace(script))
{
    Console.WriteLine("::error::A C# script path is required.");
    return InvalidArgumentsExitCode;
}

var workspace = GetEnvironmentVariable("GITHUB_WORKSPACE")!;

var path = Path.GetFullPath(script, workspace);

var arguments = (GetEnvironmentVariable("SCRIPT_ARGUMENTS") ?? string.Empty)
    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

return Process.Run("dotnet", ["run", "--file", path, "--", .. arguments]).ExitCode;
