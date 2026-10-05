// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Field = (string Key, string[] Values, bool Scalar);

return Program.Run();

/// <summary>Writes an AUR -bin PKGBUILD and .SRCINFO that repackage release .deb files.</summary>
internal static partial class Program
{
    /// <summary>The suffix of a binary AUR package name.</summary>
    private const string BinSuffix = "-bin";

    /// <summary>The length of an ar member header.</summary>
    private const int ArHeaderLength = 60;

    /// <summary>The length of an ar member name.</summary>
    private const int ArNameLength = 16;

    /// <summary>The offset of the size in an ar member header.</summary>
    private const int ArSizeOffset = 48;

    /// <summary>The length of the size in an ar member header.</summary>
    private const int ArSizeLength = 10;

    /// <summary>The prefix of a .deb's data archive member.</summary>
    private const string DataMemberPrefix = "data.tar";

    /// <summary>The Arch architectures and the Debian file name suffix of each.</summary>
    private static readonly (string Arch, string Suffix)[] Architectures = [("x86_64", "_amd64.deb"), ("aarch64", "_arm64.deb")];

    /// <summary>Gets the magic at the start of an ar archive, such as a .deb.</summary>
    private static ReadOnlySpan<byte> ArMagic => "!<arch>\n"u8;

    /// <summary>Writes the files from the AUR_* environment variables.</summary>
    /// <returns>The exit code.</returns>
    internal static int Run()
    {
        var name = Input("AUR_NAME");
        var version = Input("AUR_VERSION");
        var release = Input("AUR_RELEASE") is { Length: > 0 } given ? given : "1";
        var template = Input("AUR_SOURCE_URL");
        var debs = Input("AUR_DEBS");
        var problem = !template.Contains("{file}", StringComparison.Ordinal) ? "source-url-template needs a {file} placeholder." : null;
        problem ??= !PackageName().IsMatch(name) || !PackageRelease().IsMatch(release) ? $"'{name}' is not a valid package name or '{release}' is not a valid pkgrel." : null;
        var packages = problem is null ? FindPackages(debs, out problem) : [];
        if (problem is not null)
        {
            Console.WriteLine($"::error::{problem}");
            return 1;
        }

        var pkgver = ArchVersion(version);
        var fields = Fields(name, pkgver, release, packages);
        foreach (var package in packages)
        {
            // Each download is renamed so makepkg's cache names the version and architecture.
            var url = template.Replace("{version}", version, StringComparison.Ordinal).Replace("{file}", package.File, StringComparison.Ordinal);
            fields.Add(($"source_{package.Arch}", [$"{name}-{pkgver}-{package.Arch}.deb::{url}"], false));
            fields.Add(($"sha256sums_{package.Arch}", [package.Sha256], false));
        }

        var output = Input("AUR_OUTPUT");
        _ = Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "PKGBUILD"), Pkgbuild(name, fields, packages));
        File.WriteAllText(Path.Combine(output, ".SRCINFO"), Srcinfo(name, fields));
        Console.WriteLine($"Wrote {Path.Combine(output, "PKGBUILD")} and .SRCINFO for {name} {pkgver}-{release}.");
        return 0;
    }

    /// <summary>Reads an input, with runs of whitespace collapsed as makepkg does when it writes .SRCINFO.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value, or empty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Input(string name) => Normalize(Environment.GetEnvironmentVariable(name) ?? string.Empty);

    /// <summary>Reads a newline-separated input.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The non-empty lines, normalized.</returns>
    private static string[] Lines(string name)
    {
        List<string> lines = [];
        foreach (var line in (Environment.GetEnvironmentVariable(name) ?? string.Empty).Split('\n'))
        {
            if (Normalize(line) is { Length: > 0 } value)
            {
                lines.Add(value);
            }
        }

        return [.. lines];
    }

    /// <summary>Collapses runs of whitespace to one space and trims the ends.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The normalized value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Normalize(string value) => Whitespace().Replace(value, " ").Trim();

    /// <summary>Finds and hashes the .deb for each architecture.</summary>
    /// <param name="folder">The folder holding the .deb files.</param>
    /// <param name="problem">The problem, or null.</param>
    /// <returns>The packages found.</returns>
    private static List<Package> FindPackages(string folder, out string? problem)
    {
        List<Package> packages = [];
        problem = Directory.Exists(folder) ? null : $"The debs folder '{folder}' does not exist.";
        if (problem is not null)
        {
            return packages;
        }

        foreach (var (arch, suffix) in Architectures)
        {
            var matches = Directory.GetFiles(folder, $"*{suffix}");
            if (matches.Length > 1)
            {
                problem = $"More than one *{suffix} in {folder}.";
                return packages;
            }

            if (matches.Length == 0)
            {
                continue;
            }

            using var deb = new FileStream(matches[0], new FileStreamOptions { Options = FileOptions.SequentialScan });
            if (DataMember(deb) is not { } data)
            {
                problem = $"{matches[0]} is not a .deb with a data.tar member.";
                return packages;
            }

            deb.Position = 0;
            packages.Add(new(arch, Path.GetFileName(matches[0]), Convert.ToHexStringLower(SHA256.HashData(deb)), data));
        }

        problem = packages.Count == 0 ? $"{folder} holds no *_amd64.deb or *_arm64.deb." : null;
        return packages;
    }

    /// <summary>Gets the fields shared by every architecture, in the order makepkg --printsrcinfo writes them.</summary>
    /// <param name="name">The package name.</param>
    /// <param name="pkgver">The Arch version.</param>
    /// <param name="release">The package release.</param>
    /// <param name="packages">The packages found.</param>
    /// <returns>The fields.</returns>
    private static List<Field> Fields(string name, string pkgver, string release, List<Package> packages)
    {
        var arches = new string[packages.Count];
        for (var i = 0; i < packages.Count; i++)
        {
            arches[i] = packages[i].Arch;
        }

        // A -bin package replaces the package built from source.
        string[] provides = name.EndsWith(BinSuffix, StringComparison.Ordinal) ? [name[..^BinSuffix.Length]] : [];
        return
        [
            ("pkgdesc", [Input("AUR_DESCRIPTION")], true),
            ("pkgver", [pkgver], true),
            ("pkgrel", [release], true),
            ("url", [Input("AUR_URL")], true),
            ("arch", arches, false),
            ("license", Lines("AUR_LICENSE"), false),
            ("depends", Lines("AUR_DEPENDS"), false),
            ("optdepends", Lines("AUR_OPTDEPENDS"), false),
            ("provides", provides, false),
            ("conflicts", provides, false),
            ("options", Lines("AUR_OPTIONS"), false),
        ];
    }

    /// <summary>Writes the PKGBUILD.</summary>
    /// <param name="name">The package name.</param>
    /// <param name="fields">The fields.</param>
    /// <param name="packages">The packages found.</param>
    /// <returns>The PKGBUILD text.</returns>
    private static string Pkgbuild(string name, List<Field> fields, List<Package> packages)
    {
        var text = new StringBuilder();
        if (Input("AUR_MAINTAINER") is { Length: > 0 } maintainer)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"# Maintainer: {maintainer}\n");
        }

        _ = text.Append(CultureInfo.InvariantCulture, $"pkgname={name}\n");
        foreach (var field in fields)
        {
            AppendAssignment(text, field);
        }

        // makepkg unpacks each .deb into srcdir; its data archive holds the installed tree.
        var data = packages[0].Data;
        foreach (var package in packages)
        {
            data = package.Data == data ? data : $"{DataMemberPrefix}.*";
        }

        _ = text.Append(CultureInfo.InvariantCulture, $"\npackage() {{\n  bsdtar -xf {data} -C \"$pkgdir\"\n}}\n");
        return text.ToString();
    }

    /// <summary>Appends a field as a single-quoted bash scalar or array assignment.</summary>
    /// <param name="text">The PKGBUILD text.</param>
    /// <param name="field">The field; one without values writes nothing.</param>
    private static void AppendAssignment(StringBuilder text, Field field)
    {
        if (field.Values.Length == 0)
        {
            return;
        }

        _ = text.Append(field.Key).Append(field.Scalar ? "=" : "=(");
        for (var i = 0; i < field.Values.Length; i++)
        {
            _ = text.Append(i == 0 ? string.Empty : " ").Append('\'').Append(field.Values[i].Replace("'", "'\\''", StringComparison.Ordinal)).Append('\'');
        }

        _ = text.Append(field.Scalar ? "\n" : ")\n");
    }

    /// <summary>Writes the .SRCINFO the AUR reads instead of running the PKGBUILD.</summary>
    /// <param name="name">The package name.</param>
    /// <param name="fields">The fields.</param>
    /// <returns>The .SRCINFO text.</returns>
    private static string Srcinfo(string name, List<Field> fields)
    {
        var text = new StringBuilder();
        _ = text.Append(CultureInfo.InvariantCulture, $"pkgbase = {name}\n");
        foreach (var (key, values, _) in fields)
        {
            foreach (var value in values)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"\t{key} = {value}\n");
            }
        }

        _ = text.Append(CultureInfo.InvariantCulture, $"\npkgname = {name}\n");
        return text.ToString();
    }

    /// <summary>Converts a semantic version to a pacman version.</summary>
    /// <param name="version">The semantic version.</param>
    /// <returns>A version whose pre-release sorts before the release: 1.2.0-rc.1 becomes 1.2.0rc.1.</returns>
    private static string ArchVersion(string version)
    {
        var core = version.Split('+')[0];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || dash == core.Length - 1)
        {
            return VersionInvalid().Replace(core.TrimEnd('-'), ".");
        }

        // vercmp ranks a trailing letter segment below the bare version, but a trailing separator above it.
        var preRelease = VersionInvalid().Replace(core[(dash + 1)..], ".");
        var prefix = char.IsAsciiLetter(preRelease[0]) ? string.Empty : "pre";
        return $"{VersionInvalid().Replace(core[..dash], ".")}{prefix}{preRelease}";
    }

    /// <summary>Finds the data archive member of a .deb.</summary>
    /// <param name="deb">The .deb, read from its start.</param>
    /// <returns>The member name, or null.</returns>
    private static string? DataMember(Stream deb)
    {
        Span<byte> header = stackalloc byte[ArHeaderLength];
        var magic = header[..ArMagic.Length];
        if (deb.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) != magic.Length || !magic.SequenceEqual(ArMagic))
        {
            return null;
        }

        while (deb.Position + ArHeaderLength <= deb.Length)
        {
            deb.ReadExactly(header);
            var member = Encoding.ASCII.GetString(header[..ArNameLength]).TrimEnd().TrimEnd('/');
            if (member.StartsWith(DataMemberPrefix, StringComparison.Ordinal))
            {
                return member;
            }

            if (!long.TryParse(header.Slice(ArSizeOffset, ArSizeLength), NumberStyles.None | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var size))
            {
                return null;
            }

            // Members are padded to an even length.
            _ = deb.Seek(size + (size & 1), SeekOrigin.Current);
        }

        return null;
    }

    /// <summary>Matches runs of whitespace.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Matches a valid pacman package name.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("^[a-z0-9@_+][a-z0-9@._+-]*$")]
    private static partial Regex PackageName();

    /// <summary>Matches a valid pkgrel.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex(@"^[1-9][0-9]*(\.[0-9]+)?$")]
    private static partial Regex PackageRelease();

    /// <summary>Matches characters pacman does not allow in a version.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("[^A-Za-z0-9._]")]
    private static partial Regex VersionInvalid();

    /// <summary>A release .deb.</summary>
    /// <param name="Arch">The Arch architecture.</param>
    /// <param name="File">The file name.</param>
    /// <param name="Sha256">The lowercase SHA-256.</param>
    /// <param name="Data">The data archive member name.</param>
    private sealed record Package(string Arch, string File, string Sha256, string Data);
}
