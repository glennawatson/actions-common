#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Text.Json;

Directory.SetCurrentDirectory(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

var project = Environment.GetEnvironmentVariable("PREPARATION_PROJECT") ?? throw new InvalidOperationException("PREPARATION_PROJECT is required.");

var configuration = Environment.GetEnvironmentVariable("PREPARATION_CONFIGURATION") ?? "Release";

var temporary = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();

var output = Path.Combine(temporary, "test-preparation-app");

string[] publish = ["publish", project, "-c", configuration, "--self-contained", "false", "-o", output];

Console.WriteLine($"[command]dotnet {string.Join(' ', publish)}");

var result = Process.Run("dotnet", publish);

if (result.ExitCode != 0)
{
    return result.ExitCode;
}

using var arguments = JsonDocument.Parse(Environment.GetEnvironmentVariable("PREPARATION_ARGUMENTS") ?? "[\"test\"]");

var values = new List<string> { Path.Combine(output, $"{Path.GetFileNameWithoutExtension(project)}.dll") };

foreach (var argument in arguments.RootElement.EnumerateArray())
{
    values.Add(argument.GetString() ?? throw new InvalidDataException("Preparation arguments must be strings."));
}

Console.WriteLine($"[command]dotnet {string.Join(' ', values)}");

return Process.Run("dotnet", [.. values]).ExitCode;
