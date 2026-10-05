#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using static System.Environment;
const int InvalidArgumentsExitCode = 2;

args = [
    System.Environment.GetEnvironmentVariable("WORKING_DIRECTORY") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("PROJECT_KEY") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("ORGANIZATION") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("HOST_URL") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("VERSION") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("COVERAGE_GLOB") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("EXTRA_BEGIN_ARGS") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("EXCLUSIONS") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("COVERAGE_EXCLUSIONS") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("CPD_EXCLUSIONS") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("TEST_EXCLUSIONS") ?? string.Empty];

if (args is not [
    var workingDirectory,
    var projectKey,
    var organization,
    var hostUrl,
    var version,
    var coverageGlob,
    var extraBeginArgs,
    var exclusions,
    var coverageExclusions,
    var cpdExclusions,
    var testExclusions])
{
    Console.WriteLine("::error::Expected the eleven SonarScanner begin arguments.");
    return InvalidArgumentsExitCode;
}

Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, workingDirectory));

return Process.Run(
    "dotnet",
    [
        "sonarscanner", "begin",
        $"/k:{projectKey}",
        .. Switch("/o:", organization),
        $"/d:sonar.host.url={hostUrl}",
        $"/d:sonar.token={GetEnvironmentVariable("SONAR_TOKEN")}",
        "/d:sonar.scanner.skipJreProvisioning=true",
        $"/d:sonar.cs.cobertura.reportsPaths={coverageGlob}",

        // S3267 asks for LINQ in loops, which contradicts PerformanceSharp's PSH1100 that this repo enforces.
        "/d:sonar.issue.ignore.multicriteria=linqLoops",
        "/d:sonar.issue.ignore.multicriteria.linqLoops.ruleKey=csharpsquid:S3267",
        "/d:sonar.issue.ignore.multicriteria.linqLoops.resourceKey=**/*.cs",
        .. Switch("/d:sonar.exclusions=", exclusions),
        .. Switch("/d:sonar.coverage.exclusions=", coverageExclusions),
        .. Switch("/d:sonar.cpd.exclusions=", cpdExclusions),
        .. Switch("/d:sonar.test.exclusions=", testExclusions),
        .. Switch("/v:", version),
        .. extraBeginArgs.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries),
    ]).ExitCode;

// An empty value leaves its switch out.
static string[] Switch(string name, string value) => value is "" ? [] : [name + value];
