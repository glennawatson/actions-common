// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (args is not [var timestampUrl, var packagesGlob, var targetType])
{
    Console.WriteLine("::error::Expected the timestamp URL, packages glob and target type arguments.");
    return Program.UsageError;
}

if (targetType is not (Program.NuGet or Program.Authenticode))
{
    Console.WriteLine($"::error::Unknown target type {targetType}; use {Program.NuGet} or {Program.Authenticode}.");
    return Program.UsageError;
}

if (GetEnvironmentVariable("JSIGN_JAR") is not { Length: > 0 } jar)
{
    Console.WriteLine("::error::The prebuilt certum-signer image must set JSIGN_JAR.");
    return 1;
}

var keystore = Path.Combine(GetEnvironmentVariable("RUNNER_TEMP")!, "sunpkcs11.conf");

await File.WriteAllLinesAsync(keystore, ["name = SimplySign", $"library = {GetEnvironmentVariable("SS_PKCS11")}", "slotListIndex = 0"]).ConfigureAwait(false);

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

var packages = Program.Match(packagesGlob);

if (packages is [])
{
    Console.WriteLine($"::error::no packages matched: {packagesGlob}");
    return 1;
}

foreach (var package in packages)
{
    Console.WriteLine($"::group::sign {package}");
    if (await Program.SignAsync(jar, keystore, timestampUrl, package, CancellationToken.None).ConfigureAwait(false) is not 0 and var failed)
    {
        return failed;
    }

    if (targetType is Program.NuGet && await Program.VerifyAsync(package, CancellationToken.None).ConfigureAwait(false) is not 0 and var unverified)
    {
        return unverified;
    }

    Console.WriteLine("::endgroup::");
}

return 0;

/// <summary>Signs files with jsign through the Certum token's PKCS#11 keystore.</summary>
internal static partial class Program
{
    /// <summary>The exit code for bad arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The target type for NuGet packages, which are verified after signing.</summary>
    internal const string NuGet = "nuget";

    /// <summary>The target type for Windows executables and installers.</summary>
    internal const string Authenticode = "authenticode";

    /// <summary>The longest wait for the keystore to show the certificate after login.</summary>
    private static readonly TimeSpan CertificateTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The interval between jsign attempts while the keystore shows no certificate.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(4);

    /// <summary>Lists the files that match a glob whose wildcards are in the file name only.</summary>
    /// <param name="glob">The glob, relative to the working directory.</param>
    /// <returns>The matching files, sorted ordinally.</returns>
    internal static string[] Match(string glob)
    {
        var folder = Path.GetDirectoryName(glob) is { Length: > 0 } directory ? directory : ".";
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var files = Directory.GetFiles(folder, Path.GetFileName(glob));
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>Signs one file, retrying while the keystore reports no certificate.</summary>
    /// <param name="jar">The jsign jar.</param>
    /// <param name="keystore">The SunPKCS11 configuration.</param>
    /// <param name="timestampUrl">The RFC 3161 timestamp authority.</param>
    /// <param name="package">The file to sign.</param>
    /// <param name="cancellationToken">Stops jsign and the retries.</param>
    /// <returns>Zero on success, otherwise the exit code to stop with.</returns>
    internal static async Task<int> SignAsync(string jar, string keystore, string timestampUrl, string package, CancellationToken cancellationToken)
    {
        // The keystore can report no certificate for a few seconds after login, so only that error is retried.
        // jsign auto-selects the token's single key; the RFC3161 timestamp outlives the certificate.
        string[] arguments =
        [
            "-jar", jar, "--storetype", "PKCS11", "--keystore", keystore, "--storepass", string.Empty,
            "--alg", "SHA-256", "--tsmode", "RFC3161", "--tsaurl", timestampUrl, "--replace", package,
        ];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(CertificateTimeout);
        using var timer = new PeriodicTimer(RetryInterval);
        var attempt = 1;
        try
        {
            do
            {
                var result = await Process.RunAndCaptureTextAsync("java", arguments, cancellationToken).ConfigureAwait(false);
                var output = result.StandardOutput + result.StandardError;
                Console.WriteLine(output);
                if (result.ExitStatus.ExitCode is 0 || !output.Contains("No certificate found", StringComparison.Ordinal))
                {
                    return result.ExitStatus.ExitCode;
                }

                Console.WriteLine($"keystore not populated yet (attempt {attempt})");
                attempt++;
            }
            while (await timer.WaitForNextTickAsync(limit.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout passed with the keystore still empty.
        }

        Console.WriteLine("::error::keystore exposed no certificate to jsign");
        return 1;
    }

    /// <summary>Checks a signed NuGet package with dotnet nuget verify.</summary>
    /// <param name="package">The package.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> VerifyAsync(string package, CancellationToken cancellationToken) =>
        (await Process.RunAsync("dotnet", ["nuget", "verify", package, "--all", "--verbosity", "detailed"], cancellationToken: cancellationToken).ConfigureAwait(false)).ExitCode;
}
