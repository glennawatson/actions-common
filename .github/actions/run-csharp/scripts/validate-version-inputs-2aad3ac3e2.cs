#!/usr/bin/env dotnet
args = [System.Environment.GetEnvironmentVariable("BUMP") ?? string.Empty, System.Environment.GetEnvironmentVariable("PRE_RELEASE") ?? string.Empty, System.Environment.GetEnvironmentVariable("VERSION_OVERRIDE") ?? string.Empty];
if (args is not [var choice, var preRelease, var versionOverride])
{
    Console.WriteLine("::error::Expected the bump, pre-release and version override arguments.");
    return 2;
}

var bump = choice is "(pick one)" ? string.Empty : choice;
string? error = (bump, preRelease, versionOverride) switch
{
    ("", _, "") => "Pick a bump level (major, minor or patch), or give a version override.",
    (not "", _, not "") => "'bump' and 'versionOverride' are mutually exclusive: set exactly one.",
    (_, not "", not "") => "'preRelease' cannot be combined with 'versionOverride': put any pre-release suffix in the override itself.",
    _ => null,
};

if (error is not null)
{
    Console.WriteLine($"::error::{error}");
    return 1;
}

return 0;
