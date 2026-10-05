#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
#:package System.Management
#:property PublishAot=false

using System.Management;
using System.Runtime.Versioning;
using static System.Environment;

if (!OperatingSystem.IsWindows())
{
    return;
}

// Real-time scanning of build output is a large share of Windows build time. Best-effort.
AddExclusions(
    "ExclusionPath",
    [
        GetEnvironmentVariable("GITHUB_WORKSPACE")!,
        GetEnvironmentVariable("DOTNET_ROOT")!,
        GetEnvironmentVariable("RUNNER_TEMP")!,
        Path.Combine(GetFolderPath(SpecialFolder.UserProfile), ".nuget"),
    ]);

AddExclusions("ExclusionProcess", ["dotnet.exe", "MSBuild.exe", "VBCSCompiler.exe"]);

[SupportedOSPlatform("windows")]
static void AddExclusions(string preference, string[] values)
{
    try
    {
        using var preferences = new ManagementClass(@"root\Microsoft\Windows\Defender", "MSFT_MpPreference", null);
        using var parameters = preferences.GetMethodParameters("Add");
        parameters[preference] = values;
        _ = preferences.InvokeMethod("Add", parameters, null);
        Console.WriteLine($"Defender {preference}: {string.Join(", ", values)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Skipped Defender {preference} ({ex.Message})");
    }
}
