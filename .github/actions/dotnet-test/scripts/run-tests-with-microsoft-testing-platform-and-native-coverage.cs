#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("SRC_FOLDER") ?? string.Empty, System.Environment.GetEnvironmentVariable("SOLUTION_FILE") ?? string.Empty, System.Environment.GetEnvironmentVariable("CONFIGURATION") ?? string.Empty, System.Environment.GetEnvironmentVariable("DETAILED_LOGGING") ?? string.Empty, System.Environment.GetEnvironmentVariable("TEST_TIMEOUT") ?? string.Empty];

if (args is not [var srcFolder, var solutionFile, var configuration, var detailedLogging, var testTimeout])
{
    Console.WriteLine("::error::Expected the source folder, solution file, configuration, detailed logging and test timeout arguments.");
    return 2;
}

Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, srcFolder));

if (!File.Exists("testconfig.json"))
{
    Console.WriteLine($"::error::MTP requires '{srcFolder}/testconfig.json' (manages coverage and execution). See https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-extensions-code-coverage");
    return 1;
}

string[] solution = (solutionFile, Directory.EnumerateFiles(".", "*.slnx").Concat(Directory.EnumerateFiles(".", "*.sln")).FirstOrDefault()) switch
{
    (not "", _) => ["--solution", Path.GetFullPath(solutionFile)],
    (_, { } found) => ["--solution", Path.GetFullPath(found)],
    _ => [],
};

// --verbosity is VSTest-only and ignored by MTP. Synchronous writes keep the log when the runner kills a hung test.
string[] diagnostics = detailedLogging is "true"
    ? ["--diagnostic", "--diagnostic-verbosity", "Trace", "--diagnostic-synchronous-write", "--log-level", "Trace", "--output", "Detailed"]
    : [];

string[] timeout = testTimeout is "" ? [] : ["--timeout", testTimeout];

// No --results-directory: parallel test hosts sharing one directory collide on log_<ts>.diag names.
return Process.Run(
    "dotnet",
    ["test", .. solution, "--configuration", configuration, .. diagnostics, .. timeout, "--coverage", "--coverage-output-format", "cobertura"]).ExitCode;
