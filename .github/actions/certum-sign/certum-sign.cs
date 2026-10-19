// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using static System.Environment;

if (args is not [var timestampUrl, var authenticodeGlob, var nugetGlob, var rpmGlob, var checksumsGlob])
{
    Console.WriteLine("::error::Expected the timestamp URL, authenticode glob, NuGet glob, RPM glob and checksums glob arguments.");
    return Program.UsageError;
}

if ((authenticodeGlob, nugetGlob, rpmGlob, checksumsGlob) is ("", "", "", ""))
{
    nugetGlob = "signed/*.nupkg";
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

if (!Program.TryMatch(authenticodeGlob, excludeChecksums: false, out var authenticode)
    || !Program.TryMatch(nugetGlob, excludeChecksums: false, out var nuget)
    || !Program.TryMatch(rpmGlob, excludeChecksums: false, out var rpm)
    || !Program.TryMatch(checksumsGlob, excludeChecksums: true, out var checksumFiles))
{
    return 1;
}

string[] toSign = [.. authenticode, .. nuget, .. rpm];

if (toSign.Length > 0)
{
    if ((GetEnvironmentVariable("CERTUM_USER_ID"), GetEnvironmentVariable("CERTUM_OTP_URI")) is not ({ Length: > 0 }, { Length: > 0 }))
    {
        Console.WriteLine("::error::Map the CERTUM_USER_ID and CERTUM_OTP_URI secrets into the job env.");
        return 1;
    }

    await Program.WaitForFreshTotpWindowAsync(CancellationToken.None).ConfigureAwait(false);

    // One run signs every file, so SafeSign logs in to SimplySign once. It reads the credentials from the environment.
    Console.WriteLine("::group::sign files");
    var sign = await Process.RunAsync(
        "safesign",
        ["sign", .. toSign, "--provider", "certum", "--hash", "SHA256", "--timestamp-url", timestampUrl],
        cancellationToken: CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine("::endgroup::");
    if (sign is { ExitCode: not 0 })
    {
        return sign.ExitCode;
    }

    await Program.RecordTotpWindowAsync(CancellationToken.None).ConfigureAwait(false);
}

foreach (var package in nuget)
{
    Console.WriteLine($"::group::verify {package}");
    var verify = await Process.RunAsync(
        "dotnet",
        ["nuget", "verify", package, "--all", "--verbosity", "detailed"],
        cancellationToken: CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine("::endgroup::");
    if (verify is { ExitCode: not 0 })
    {
        return verify.ExitCode;
    }
}

if (checksumsGlob is { Length: > 0 })
{
    var folder = Path.GetDirectoryName(checksumsGlob) is { Length: > 0 } directory ? directory : ".";
    var content = Program.Checksums(checksumFiles);
    await File.WriteAllBytesAsync(Path.Combine(folder, Program.ChecksumsName), content, CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"Wrote {Program.ChecksumsName} for {checksumFiles.Length} files.");
}

return 0;

/// <summary>Signs files with SafeSign, verifies signed NuGet packages, and writes SHA256SUMS.</summary>
internal static partial class Program
{
    /// <summary>The exit code for bad arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The file name written for release checksums.</summary>
    internal const string ChecksumsName = "SHA256SUMS";

    /// <summary>The environment variable tracking the last 30-second TOTP step used in this job.</summary>
    private const string LastTotpStepVariable = "CERTUM_LAST_TOTP_STEP";

    /// <summary>The RFC 6238 TOTP period in seconds.</summary>
    private const long TotpPeriodSeconds = 30;

    /// <summary>The two spaces sha256sum puts between the hash and the file name.</summary>
    private static readonly byte[] Separator = "  "u8.ToArray();

    /// <summary>Matches files for an optional glob and logs an error when a non-empty glob matches nothing.</summary>
    /// <param name="glob">The file glob, or empty to skip.</param>
    /// <param name="excludeChecksums">True to exclude existing SHA256SUMS files.</param>
    /// <param name="files">Receives the matched files sorted ordinally.</param>
    /// <returns>False when a non-empty glob matched no files.</returns>
    internal static bool TryMatch(string glob, bool excludeChecksums, out string[] files)
    {
        if (glob is "")
        {
            files = [];
            return true;
        }

        var folder = Path.GetDirectoryName(glob) is { Length: > 0 } directory ? directory : ".";
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"::error::no files matched: {glob}");
            files = [];
            return false;
        }

        List<string> matched = [];
        foreach (var file in Directory.EnumerateFiles(folder, Path.GetFileName(glob)))
        {
            if (!excludeChecksums || !Path.GetFileName(file.AsSpan()).StartsWith(ChecksumsName, StringComparison.Ordinal))
            {
                matched.Add(file);
            }
        }

        if (matched is [])
        {
            Console.WriteLine($"::error::no files matched: {glob}");
            files = [];
            return false;
        }

        matched.Sort(StringComparer.Ordinal);
        files = [.. matched];
        return true;
    }

    /// <summary>Waits until the next 30-second TOTP window when an earlier step in the same job already used the current window.</summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes once the current 30-second TOTP window is unused.</returns>
    internal static async Task WaitForFreshTotpWindowAsync(CancellationToken cancellationToken)
    {
        if (GetEnvironmentVariable(LastTotpStepVariable) is not { Length: > 0 } raw
            || !long.TryParse(raw, CultureInfo.InvariantCulture, out var lastStep))
        {
            return;
        }

        var now = TimeProvider.System.GetUtcNow();
        var currentStep = now.ToUnixTimeSeconds() / TotpPeriodSeconds;
        if (currentStep > lastStep)
        {
            return;
        }

        var nextWindowStart = DateTimeOffset.FromUnixTimeSeconds((lastStep + 1) * TotpPeriodSeconds);
        var delay = nextWindowStart - now + TimeSpan.FromSeconds(1);
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the current 30-second TOTP step in GITHUB_ENV so a later step does not reuse the same code.</summary>
    /// <param name="cancellationToken">Stops the file write.</param>
    /// <returns>A task that completes when the step is recorded.</returns>
    internal static async Task RecordTotpWindowAsync(CancellationToken cancellationToken)
    {
        if (GetEnvironmentVariable("GITHUB_ENV") is not { Length: > 0 } envFile)
        {
            return;
        }

        var step = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() / TotpPeriodSeconds;
        await File.AppendAllLinesAsync(envFile, [$"{LastTotpStepVariable}={step}"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes SHA256SUMS in the format sha256sum -c reads: hash, two spaces, file name, newline.</summary>
    /// <param name="files">The files.</param>
    /// <returns>The SHA256SUMS bytes.</returns>
    internal static ReadOnlyMemory<byte> Checksums(string[] files)
    {
        const int HexLength = SHA256.HashSizeInBytes * 2;
        var sums = new ArrayBufferWriter<byte>();
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        foreach (var file in files)
        {
            using (var stream = new FileStream(file, new FileStreamOptions { Options = FileOptions.SequentialScan }))
            {
                _ = SHA256.HashData(stream, hash);
            }

            _ = Convert.TryToHexStringLower(hash, sums.GetSpan(HexLength), out var written);
            sums.Advance(written);
            sums.Write(Separator);
            var name = Path.GetFileName(file);
            sums.Advance(Encoding.UTF8.GetBytes(name, sums.GetSpan(Encoding.UTF8.GetByteCount(name) + 1)));
            sums.Write("\n"u8);
        }

        return sums.WrittenMemory;
    }
}
