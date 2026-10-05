#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
#:include VersionPatterns.cs
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ActionsCommon.Versioning;
using static System.Environment;
const int InvalidArgumentsExitCode = 2;

args = [
    System.Environment.GetEnvironmentVariable("BUMP") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("TAG_PREFIX") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("PUSH_TAG") ?? string.Empty,
    System.Environment.GetEnvironmentVariable("PRE_RELEASE_ID") ?? string.Empty];

if (args is not [var bump, var tagPrefix, var pushTag, var preReleaseId])
{
    Console.WriteLine("::error::Expected the bump, tag prefix, push-tag and pre-release id arguments.");
    return InvalidArgumentsExitCode;
}

if (bump is "")
{
    Console.WriteLine("No bump requested; skipping version/tag computation (CI pre-release path).");
    return 0;
}

if (bump is not ("major" or "minor" or "patch"))
{
    Console.WriteLine($"::error::bump must be one of major|minor|patch (got '{bump}').");
    return 1;
}

// No dots, so "<core>-<id>.<N>" has exactly one numeric counter to parse.
if (preReleaseId is not "" && !VersionPatterns.MyRegex.IsMatch(preReleaseId))
{
    Console.WriteLine($"::error::pre-release-id '{preReleaseId}' must be a SemVer 2.0 alphanumeric/hyphen identifier (no dots).");
    return 1;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

Version? latest = null;

foreach (var candidate in Git("tag", "--list", $"{tagPrefix}[0-9]*.[0-9]*.[0-9]*").StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    if (!candidate.StartsWith(tagPrefix, StringComparison.Ordinal))
    {
        continue;
    }

    var match = VersionPatterns.Core.Match(candidate[tagPrefix.Length..]);
    if (!match.Success)
    {
        continue;
    }

    var candidateVersion = new Version(Number(match.Groups[1]), Number(match.Groups[2]), Number(match.Groups[3]));
    if (latest is null || candidateVersion > latest)
    {
        latest = candidateVersion;
    }
}

if (latest is null)
{
    Console.WriteLine($"No RTM tag ({tagPrefix}X.Y.Z) found in history; seeding first release from 0.0.0.");
}

var last = latest ?? new Version(0, 0, 0);

var core = bump switch
{
    "major" => new Version(last.Major + 1, 0, 0),
    "minor" => new Version(last.Major, last.Minor + 1, 0),
    _ => new Version(last.Major, last.Minor, last.Build + 1),
};

var version = preReleaseId switch
{
    "" => core.ToString(),
    _ => $"{core}-{preReleaseId}.{NextCounter(tagPrefix, core, preReleaseId)}",
};

var tag = $"{tagPrefix}{version}";

Console.WriteLine($"Latest RTM tag: {tagPrefix}{last} -> next ({bump}{(preReleaseId is "" ? string.Empty : $", {preReleaseId}")}): {tag}");

if (Process.Run("git", ["rev-parse", "-q", "--verify", $"refs/tags/{tag}"], silent: true) is { ExitCode: 0 })
{
    Console.WriteLine($"::error::Tag {tag} already exists. Refusing to re-release an existing version.");
    return 1;
}

if (pushTag is "true")
{
    _ = Git("config", "user.name", "github-actions[bot]");
    _ = Git("config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com");
    _ = Git("tag", tag);
    _ = Git("push", "origin", tag);
    Console.WriteLine($"Pushed tag {tag}.");
}
else
{
    Console.WriteLine($"push-tag=false: computed {tag} but not creating it now (deferred, e.g. to GitHub release creation).");
}

// MinVer's MSBuild task stamps this verbatim instead of walking git.
File.AppendAllLines(GetEnvironmentVariable("GITHUB_ENV")!, [$"MINVERVERSIONOVERRIDE={version}"]);

File.AppendAllLines(GetEnvironmentVariable("GITHUB_OUTPUT")!, [$"version={version}", $"tag={tag}", $"prerelease={(preReleaseId is "" ? "false" : "true")}"]);

return 0;

static int NextCounter(string tagPrefix, Version core, string preReleaseId)
{
    var highest = 0;
    foreach (var tag in Git("tag", "--list", $"{tagPrefix}{core}-{preReleaseId}.[0-9]*").StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (int.TryParse(tag.AsSpan(tag.LastIndexOf('.') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
        {
            highest = Math.Max(highest, counter);
        }
    }

    return highest + 1;
}

static int Number(Group group) => int.Parse(group.ValueSpan, CultureInfo.InvariantCulture);

static ProcessTextOutput Git(params IEnumerable<string> arguments)
{
    var result = Process.RunAndCaptureText("git", arguments);
    Console.Error.Write(result.StandardError);
    if (result is not { ExitStatus.ExitCode: 0 })
    {
        Exit(result.ExitStatus.ExitCode);
    }

    return result;
}
