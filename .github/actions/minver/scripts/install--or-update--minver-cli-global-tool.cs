#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
File.AppendAllLines(GetEnvironmentVariable("GITHUB_PATH")!, [Path.Combine(GetFolderPath(SpecialFolder.UserProfile), ".dotnet", "tools")]);

return Process.Run("dotnet", ["tool", "update", "--global", "minver-cli"]).ExitCode;
