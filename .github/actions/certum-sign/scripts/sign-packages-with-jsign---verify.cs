#!/usr/bin/env dotnet
// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using System.Text;
using static System.Environment;
const int MaximumSigningAttempts = 15;

const int SigningRetrySeconds = 4;

const int InvalidArgumentsExitCode = 2;

args = [System.Environment.GetEnvironmentVariable("TS_URL") ?? string.Empty, System.Environment.GetEnvironmentVariable("PKG_GLOB") ?? string.Empty];

if (args is not [var timestampUrl, var packagesGlob])
{
    Console.WriteLine("::error::Expected the timestamp URL and packages glob arguments.");
    return InvalidArgumentsExitCode;
}

if (GetEnvironmentVariable("JSIGN_JAR") is not { Length: > 0 } jar)
{
    Console.WriteLine("::error::The prebuilt certum-signer image must set JSIGN_JAR.");
    return 1;
}

var keystore = Path.Combine(GetEnvironmentVariable("RUNNER_TEMP")!, "sunpkcs11.conf");

File.WriteAllLines(keystore, ["name = SimplySign", $"library = {GetEnvironmentVariable("SS_PKCS11")}", "slotListIndex = 0"]);

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

var folder = Path.GetDirectoryName(packagesGlob) is { Length: > 0 } directory ? directory : ".";

string[] packages = Directory.Exists(folder) ? [.. Directory.GetFiles(folder, Path.GetFileName(packagesGlob)).Order(StringComparer.Ordinal)] : [];

if (packages is [])
{
    Console.WriteLine($"::error::no packages matched: {packagesGlob}");
    return 1;
}

foreach (var package in packages)
{
    Console.WriteLine($"::group::sign {package}");

    // The keystore can report no certificate for a few seconds after login, so only that error is retried.
    // jsign auto-selects the token's single key; the RFC3161 timestamp outlives the certificate.
    for (var attempt = 1; attempt <= MaximumSigningAttempts; attempt++)
    {
        string[] commandArguments =
        [
    "-jar",
    jar,
    "--storetype",
    "PKCS11",
    "--keystore",
    keystore,
    "--storepass",
    string.Empty,
    "--alg",
    "SHA-256",
    "--tsmode",
    "RFC3161",
    "--tsaurl",
    timestampUrl,
    "--replace",
    package];

        Console.WriteLine($"[command]java {string.Join(' ', commandArguments)}");

        var start = new ProcessStartInfo("java", commandArguments) { RedirectStandardOutput = true, RedirectStandardError = true };

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start jsign.");

        var standardOutput = ForwardOutputAsync(process.StandardOutput, Console.Out);

        var standardError = ForwardOutputAsync(process.StandardError, Console.Error);

        var status = await process.WaitForExitStatusAsync().ConfigureAwait(false);

        var output = await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false);

        if (status.ExitCode == 0)
        {
            break;
        }

        if (!output.Contains("No certificate found", StringComparison.Ordinal))
        {
            return status.ExitCode;
        }

        if (attempt is MaximumSigningAttempts)
        {
            Console.WriteLine("::error::keystore exposed no certificate to jsign");
            return 1;
        }

        Console.WriteLine($"keystore not populated yet (attempt {attempt})");
        Thread.Sleep(TimeSpan.FromSeconds(SigningRetrySeconds));
    }

    if (package.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) && Process.Run("dotnet", ["nuget", "verify", package, "--all", "--verbosity", "detailed"]) is { ExitCode: not 0 } verify)
    {
        return verify.ExitCode;
    }

    Console.WriteLine("::endgroup::");
}

return 0;

static async Task<string> ForwardOutputAsync(StreamReader source, TextWriter destination)
{
    var captured = new StringBuilder();
    while (await source.ReadLineAsync().ConfigureAwait(false) is { } line)
    {
        _ = captured.AppendLine(line);
        await destination.WriteLineAsync(line).ConfigureAwait(false);
    }

    return captured.ToString();
}
