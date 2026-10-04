#!/usr/bin/env dotnet
using System.Diagnostics;
using static System.Environment;
args = [System.Environment.GetEnvironmentVariable("TS_URL") ?? string.Empty, System.Environment.GetEnvironmentVariable("PKG_GLOB") ?? string.Empty];

if (args is not [var timestampUrl, var packagesGlob])
{
    Console.WriteLine("::error::Expected the timestamp URL and packages glob arguments.");
    return 2;
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
    for (var attempt = 1; ; attempt++)
    {
        var result = Process.RunAndCaptureText(
            "java",
            ["-jar", jar, "--storetype", "PKCS11", "--keystore", keystore, "--storepass", string.Empty, "--alg", "SHA-256", "--tsmode", "RFC3161", "--tsaurl", timestampUrl, "--replace", package]);

        var output = result.StandardOutput + result.StandardError;
        Console.WriteLine(output);

        if (result is { ExitStatus.ExitCode: 0 })
        {
            break;
        }

        if (!output.Contains("No certificate found", StringComparison.Ordinal))
        {
            return result.ExitStatus.ExitCode;
        }

        if (attempt is 15)
        {
            Console.WriteLine("::error::keystore exposed no certificate to jsign");
            return 1;
        }

        Console.WriteLine($"keystore not populated yet (attempt {attempt})");
        Thread.Sleep(TimeSpan.FromSeconds(4));
    }

    if (Process.Run("dotnet", ["nuget", "verify", package, "--all", "--verbosity", "detailed"]) is { ExitCode: not 0 } verify)
    {
        return verify.ExitCode;
    }

    Console.WriteLine("::endgroup::");
}

return 0;
