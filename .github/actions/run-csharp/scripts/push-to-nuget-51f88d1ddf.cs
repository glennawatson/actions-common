#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

var packages = Directory.GetFiles("signed", "*.nupkg");

if (packages.Length == 0)
{
    return 1;
}

foreach (var package in packages.Order(StringComparer.Ordinal))
{
    var push = Process.Run("dotnet", ["nuget", "push", package, "--source", "https://api.nuget.org/v3/index.json", "--api-key", GetEnvironmentVariable("NUGET_API_KEY")!]);
    if (push.ExitCode != 0)
    {
        return push.ExitCode;
    }
}

return 0;
