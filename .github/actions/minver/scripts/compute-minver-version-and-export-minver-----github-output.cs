#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("TAG_PREFIX") ?? string.Empty, System.Environment.GetEnvironmentVariable("MINIMUM_MAJOR_MINOR") ?? string.Empty, System.Environment.GetEnvironmentVariable("DEFAULT_PRE_RELEASE") ?? string.Empty, System.Environment.GetEnvironmentVariable("AUTO_INCREMENT") ?? string.Empty, System.Environment.GetEnvironmentVariable("VERBOSITY") ?? string.Empty, System.Environment.GetEnvironmentVariable("VERSION_OVERRIDE") ?? string.Empty];

if (args is not [var tagPrefix, var minimumMajorMinor, var defaultPreRelease, var autoIncrement, var verbosity, var versionOverride])
{
    Console.WriteLine("::error::Expected the tag prefix, minimum major.minor, pre-release identifiers, auto-increment, verbosity and version override arguments.");
    return 2;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

string semVer2;
if (versionOverride is not "")
{
    semVer2 = versionOverride;
    Console.WriteLine($"Version override supplied; skipping MinVer git walk. Using: {semVer2}");
}
else
{
    // An empty prefix is passed by leaving the option out, which is also MinVer's own default.
    var minver = Process.RunAndCaptureText(
        "minver",
        [
            "--verbosity", verbosity, "--auto-increment", autoIncrement,
            .. Option("--tag-prefix", tagPrefix),
            .. Option("--minimum-major-minor", minimumMajorMinor),
            .. Option("--default-pre-release-identifiers", defaultPreRelease),
        ]);

    Console.Error.Write(minver.StandardError);
    if (minver is not { ExitStatus.ExitCode: 0, StandardOutput: var output })
    {
        return minver.ExitStatus.ExitCode;
    }

    semVer2 = output.Trim();
    Console.WriteLine($"MinVer produced: {semVer2}");
}

// SemVer2 is "<major>.<minor>.<patch>[-<pre>][+<meta>]".
var core = semVer2.Split('-', '+')[0];
var preRelease = semVer2[core.Length..] is ['-', .. var rest] ? rest.Split('+')[0] : string.Empty;

var height = Process.RunAndCaptureText("git", ["describe", "--tags", "--abbrev=0", "--match", $"{tagPrefix}[0-9]*"]) switch
{
    { ExitStatus.ExitCode: 0, StandardOutput: var lastTag } => Git("rev-list", "--count", $"{lastTag.Trim()}..HEAD"),
    _ => Git("rev-list", "--count", "HEAD"),
};

(string Name, string Value)[] values =
[
    ("SemVer2", semVer2),
    ("SimpleVersion", core),

    // Package feeds reject build metadata, so the NuGet version drops it.
    ("NuGetPackageVersion", preRelease is "" ? core : $"{core}-{preRelease}"),
    ("GitCommitId", Git("rev-parse", "HEAD")),
    ("VersionHeight", height),
];

// MinVer's MSBuild task stamps MINVERVERSIONOVERRIDE verbatim instead of walking git per project.
File.AppendAllLines(GetEnvironmentVariable("GITHUB_ENV")!, [.. values.Select(value => $"MINVER_{value.Name}={value.Value}"), $"MINVERVERSIONOVERRIDE={semVer2}"]);
File.AppendAllLines(GetEnvironmentVariable("GITHUB_OUTPUT")!, values.Select(value => $"{value.Name}={value.Value}"));
return 0;

static string[] Option(string name, string value) => value is "" ? [] : [name, value];

static string Git(params IEnumerable<string> arguments) => Process.RunAndCaptureText("git", arguments).StandardOutput.Trim();
