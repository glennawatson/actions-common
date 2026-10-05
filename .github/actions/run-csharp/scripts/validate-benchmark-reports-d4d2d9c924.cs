#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using static System.Environment;

var root = Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, "benchmark-artifacts");

// BenchmarkDotNet exits zero on a validation error, so the exported reports are the evidence it ran.
var reports = Directory.Exists(root) ? Directory.GetFiles(root, "*-report-github.md", SearchOption.AllDirectories) : [];

if (reports is [])
{
    Console.WriteLine("::error::No benchmark reports were produced. Check the validation errors above.");
    return 1;
}

// A job whose build fails still exports its rows, with every measurement as NA.
List<string> unmeasured = [];

foreach (var report in reports)
{
    var rows = 0;
    foreach (var line in File.ReadLines(report))
    {
        if (!line.StartsWith('|'))
        {
            continue;
        }

        foreach (var cell in line.Split('|'))
        {
            if (cell.Trim() != "NA")
            {
                continue;
            }

            rows++;
            break;
        }
    }

    if (rows <= 0)
    {
        continue;
    }

    var name = Path.GetFileName(report).Replace("-report-github.md", string.Empty, StringComparison.Ordinal);
    unmeasured.Add($"{name} ({rows} rows)");
}

if (unmeasured is not [])
{
    Console.WriteLine($"::error::These benchmarks measured nothing: {string.Join("; ", unmeasured)}");
    return 1;
}

return 0;
