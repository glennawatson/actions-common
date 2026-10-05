#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

var result = Process.RunAndCaptureText("git", ["rev-parse", "HEAD"]);

if (result.ExitStatus.ExitCode != 0)
{
    return result.ExitStatus.ExitCode;
}

File.AppendAllLines(GetEnvironmentVariable("GITHUB_OUTPUT")!, [$"sha={result.StandardOutput.Trim()}"]);

return 0;
