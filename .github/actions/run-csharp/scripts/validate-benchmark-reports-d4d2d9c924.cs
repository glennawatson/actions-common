#!/usr/bin/env dotnet
using static System.Environment;

var root = Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, "benchmark-artifacts");

// BenchmarkDotNet exits zero on a validation error, so the exported reports are the evidence it ran.
string[] reports = Directory.Exists(root) ? Directory.GetFiles(root, "*-report-github.md", SearchOption.AllDirectories) : [];
if (reports is [])
{
    Console.WriteLine("::error::No benchmark reports were produced. Check the validation errors above.");
    return 1;
}

// A job whose build fails still exports its rows, with every measurement as NA.
var unmeasured = reports
    .Select(report => (Name: Path.GetFileName(report).Replace("-report-github.md", string.Empty), Rows: File.ReadLines(report).Count(line => line.StartsWith('|') && line.Split('|').Any(cell => cell.Trim() == "NA"))))
    .Where(report => report.Rows > 0)
    .Select(report => $"{report.Name} ({report.Rows} rows)")
    .ToArray();

if (unmeasured is not [])
{
    Console.WriteLine($"::error::These benchmarks measured nothing: {string.Join("; ", unmeasured)}");
    return 1;
}

return 0;
