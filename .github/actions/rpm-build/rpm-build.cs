// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;
using static System.Environment;
using Dependency = (string Name, Program.DependencyFlags Flags, string Version);
using Entry = (string Path, int Mode, string? Source, string? Target);
using Tags = System.Collections.Generic.SortedDictionary<int, (Program.HeaderType Type, int Count, byte[] Data)>;

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

var output = Program.Input("RPM_OUTPUT");

var entries = Program.Build(output, TimeProvider.System);

Console.WriteLine($"Built {output}: {entries} entries.");

return 0;

/// <summary>Builds a binary RPM from a folder laid out as the installed tree.</summary>
internal static partial class Program
{
    /// <summary>The package name input.</summary>
    private const string NameInput = "RPM_NAME";

    /// <summary>The package release input.</summary>
    private const string ReleaseInput = "RPM_RELEASE";

    /// <summary>The architecture input.</summary>
    private const string ArchitectureInput = "RPM_ARCH";

    /// <summary>The gzip level, written to the header as the payload flags.</summary>
    private const int BestCompression = 9;

    /// <summary>The length of the lead that starts every RPM.</summary>
    private const int LeadLength = 96;

    /// <summary>The length of a header index entry, and of the magic and counts before the index.</summary>
    private const int EntryLength = 16;

    /// <summary>The file type bits of a mode.</summary>
    private const int FileTypeMask = 0xF000;

    /// <summary>The file type bits of a directory.</summary>
    private const int DirectoryType = 0x4000;

    /// <summary>The file type bits of a regular file.</summary>
    private const int RegularType = 0x8000;

    /// <summary>The permission bits of a mode.</summary>
    private const int PermissionMask = 0xFFF;

    /// <summary>The size of the read and compression buffers.</summary>
    private const int BufferLength = 1 << 16;

    /// <summary>The hex digits in a SHA-256 digest.</summary>
    private const int DigestHexLength = SHA256.HashSizeInBytes * 2;

    /// <summary>RPM header value types.</summary>
    internal enum HeaderType
    {
        /// <summary>Big-endian 16-bit integers.</summary>
        Int16 = 3,

        /// <summary>Big-endian 32-bit integers.</summary>
        Int32 = 4,

        /// <summary>One NUL-terminated string.</summary>
        String = 6,

        /// <summary>Raw bytes, used for the region tag.</summary>
        Bin = 7,

        /// <summary>NUL-terminated strings.</summary>
        StringArray = 8,

        /// <summary>A translatable NUL-terminated string.</summary>
        I18n = 9,
    }

    /// <summary>RPMSENSE dependency flags.</summary>
    [Flags]
    internal enum DependencyFlags
    {
        /// <summary>Any version.</summary>
        None = 0,

        /// <summary>Older than the version.</summary>
        Less = 1 << 1,

        /// <summary>Newer than the version.</summary>
        Greater = 1 << 2,

        /// <summary>Equal to the version.</summary>
        Equal = 1 << 3,

        /// <summary>An rpmlib feature rather than a package.</summary>
        RpmLib = 1 << 24,
    }

    /// <summary>Gets the eight bytes that start every header: magic, version 1 and four reserved bytes.</summary>
    private static ReadOnlySpan<byte> HeaderMagic => [0x8E, 0xAD, 0xE8, 0x01, 0, 0, 0, 0];

    /// <summary>Reads an action input from the environment.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value, or empty when unset.</returns>
    internal static string Input(string name) => GetEnvironmentVariable(name) ?? string.Empty;

    /// <summary>Builds the package.</summary>
    /// <param name="output">The .rpm path.</param>
    /// <param name="clock">The clock for the build time.</param>
    /// <returns>The number of payload entries.</returns>
    /// <exception cref="InvalidOperationException">The finished headers differ in size from their placeholders.</exception>
    internal static int Build(string output, TimeProvider clock)
    {
        var time = (int)clock.GetUtcNow().ToUnixTimeSeconds();
        var payload = Collect(Path.GetFullPath(Input("RPM_SOURCE")), Lines("RPM_OWNED"), Lines("RPM_EXECUTABLES"));

        // The headers come first but hold digests of the payload, so the payload is written after
        // same-sized placeholders and the headers are written over them once the digests are known.
        var placeholder = new string('0', DigestHexLength);
        var digests = new string[payload.Count];
        for (var i = 0; i < digests.Length; i++)
        {
            digests[i] = payload[i].Source is null ? string.Empty : placeholder;
        }

        var headerLength = MainHeader(payload, time, digests, new(0, placeholder, 0, placeholder)).Length;
        var signatureLength = Signature([], 0, 0).Length;
        var padding = Pad(signatureLength, sizeof(long));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var package = File.OpenHandle(output, FileMode.Create, FileAccess.Write);
        var written = Archive(package, LeadLength + signatureLength + padding + headerLength, payload, time, digests);
        var header = MainHeader(payload, time, digests, written);
        var signature = Signature(header, written.CompressedLength, written.Length);
        if (header.Length != headerLength || signature.Length != signatureLength)
        {
            throw new InvalidOperationException("The finished headers differ in size from their placeholders.");
        }

        RandomAccess.Write(package, [Lead(), signature, new byte[padding], header], 0);
        return payload.Count;
    }

    /// <summary>Reads a multi-line input as trimmed, non-empty lines.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The lines.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string[] Lines(string name) => Input(name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Gets the RPM version: build metadata dropped, and a pre-release sorted before the release.</summary>
    /// <returns>The version.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Version() => Input("RPM_VERSION").Split('+')[0].Replace('-', '~');

    /// <summary>Lists the installed tree: files and symlinks always, directories only when the package owns them.</summary>
    /// <param name="source">The staged tree.</param>
    /// <param name="ownedLines">The owned directories.</param>
    /// <param name="executables">Paths or globs that are executable on Windows.</param>
    /// <returns>The entries, sorted by path.</returns>
    private static List<Entry> Collect(string source, string[] ownedLines, string[] executables)
    {
        const int LinkMode = 0xA000 | 0x1FF;
        var owned = new string[ownedLines.Length];
        for (var i = 0; i < owned.Length; i++)
        {
            owned[i] = ownedLines[i].TrimEnd('/');
        }

        List<Entry> payload = [];
        foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            var installed = $"/{relative}";
            if (new FileInfo(path).LinkTarget is { } target)
            {
                payload.Add((installed, LinkMode, null, target));
            }
            else if (Directory.Exists(path))
            {
                if (IsOwned(owned, installed))
                {
                    payload.Add((installed, DirectoryType | Mode(path, relative, true, executables), null, null));
                }
            }
            else
            {
                payload.Add((installed, RegularType | Mode(path, relative, false, executables), path, null));
            }
        }

        payload.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return payload;
    }

    /// <summary>Checks whether a path is an owned directory or below one.</summary>
    /// <param name="owned">The owned directories.</param>
    /// <param name="installed">The installed path.</param>
    /// <returns>True when the package owns the path.</returns>
    private static bool IsOwned(string[] owned, string installed)
    {
        foreach (var directory in owned)
        {
            if (installed == directory || installed.StartsWith($"{directory}/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets the permission bits. Windows has none, so directories and listed executables get 0755 and the rest 0644.</summary>
    /// <param name="path">The file system path.</param>
    /// <param name="relative">The path within the tree, with forward slashes.</param>
    /// <param name="directory">Whether the path is a directory.</param>
    /// <param name="executables">Paths or globs that are executable on Windows.</param>
    /// <returns>The permission bits.</returns>
    private static int Mode(string path, string relative, bool directory, string[] executables)
    {
        const int Executable = 0x1ED;
        const int Regular = 0x1A4;
        if (!OperatingSystem.IsWindows())
        {
            return (int)File.GetUnixFileMode(path) & PermissionMask;
        }

        if (directory)
        {
            return Executable;
        }

        foreach (var pattern in executables)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern.TrimStart('/'), relative, ignoreCase: false))
            {
                return Executable;
            }
        }

        return Regular;
    }

    /// <summary>Writes the gzip compressed cpio (newc) payload at an offset, recording each file's SHA-256 as it is read.</summary>
    /// <param name="package">The package.</param>
    /// <param name="offset">Where the payload starts.</param>
    /// <param name="payload">The entries.</param>
    /// <param name="time">The modification time.</param>
    /// <param name="digests">Receives the file digests.</param>
    /// <returns>The payload lengths and digests.</returns>
    private static PayloadSummary Archive(SafeFileHandle package, long offset, List<Entry> payload, int time, string[] digests)
    {
        const int DirectoryLinks = 2;
        const string Trailer = "TRAILER!!!";
        using var writer = new PayloadWriter(package, offset);
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferLength);
        try
        {
            for (var i = 0; i < payload.Count; i++)
            {
                var entry = payload[i];
                var size = Length(entry);
                CpioHeader(writer, new(i + 1, entry.Mode, IsDirectory(entry) ? DirectoryLinks : 1, time, size), $".{entry.Path}");
                if (entry.Source is { } source)
                {
                    using var content = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
                    long position = 0;
                    for (int read; (read = RandomAccess.Read(content, buffer, position)) > 0; position += read)
                    {
                        writer.Write(buffer.AsSpan(0, read));
                        fileHash.AppendData(buffer, 0, read);
                    }

                    digests[i] = Convert.ToHexStringLower(fileHash.GetHashAndReset());
                }
                else if (entry.Target is { } target)
                {
                    writer.Write(Encoding.UTF8.GetBytes(target));
                }

                Align(writer, size);
            }

            CpioHeader(writer, new(0, 0, 1, 0, 0), Trailer);
            return writer.Finish();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Writes a cpio newc header and the NUL-terminated name, padded to four bytes.</summary>
    /// <param name="writer">The payload.</param>
    /// <param name="fields">The numeric fields.</param>
    /// <param name="name">The entry name.</param>
    /// <exception cref="InvalidOperationException">A field overflows its width.</exception>
    private static void CpioHeader(PayloadWriter writer, CpioFields fields, string name)
    {
        // Magic, inode, mode, owner and group (both root), links, time, size, four device numbers (all zero), name size, checksum.
        const int CpioHeaderLength = 110;
        var nameBytes = Encoding.UTF8.GetBytes($"{name}\0");
        Span<byte> header = stackalloc byte[CpioHeaderLength];
        if (!Utf8.TryWrite(header, $"070701{fields.Inode:X8}{fields.Mode:X8}{0:X16}{fields.Links:X8}{fields.Time:X8}{fields.Size:X8}{0:X32}{nameBytes.Length:X8}{0:X8}", out var written)
            || written != CpioHeaderLength)
        {
            throw new InvalidOperationException($"The cpio header for {name} does not fit {CpioHeaderLength} bytes.");
        }

        writer.Write(header);
        writer.Write(nameBytes);
        Align(writer, CpioHeaderLength + nameBytes.Length);
    }

    /// <summary>Writes the zeros that align the payload after a run of bytes to four bytes.</summary>
    /// <param name="writer">The payload.</param>
    /// <param name="written">The length of the run.</param>
    private static void Align(PayloadWriter writer, long written)
    {
        const int Alignment = 4;
        ReadOnlySpan<byte> zeros = [0, 0, 0, 0];
        writer.Write(zeros[..Pad((int)(written % Alignment), Alignment)]);
    }

    /// <summary>Gets the size an entry stores in the payload.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The size in bytes.</returns>
    private static long Length(Entry entry) => entry switch
    {
        { Source: { } file } => new FileInfo(file).Length,
        { Target: { } link } => Encoding.UTF8.GetByteCount(link),
        _ => 0,
    };

    /// <summary>Checks whether an entry is a directory.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>True for a directory.</returns>
    private static bool IsDirectory(Entry entry) => (entry.Mode & FileTypeMask) == DirectoryType;

    /// <summary>Gets the padding that aligns a length.</summary>
    /// <param name="length">The length so far.</param>
    /// <param name="alignment">The alignment.</param>
    /// <returns>The number of zero bytes to add.</returns>
    private static int Pad(int length, int alignment) => (alignment - (length % alignment)) % alignment;

    /// <summary>Adds the package description tags.</summary>
    /// <param name="header">The main header.</param>
    /// <param name="time">The build time.</param>
    private static void PackageTags(Tags header, int time)
    {
        const int I18nTableTag = 100;
        const int NameTag = 1000;
        const int VersionTag = 1001;
        const int ReleaseTag = 1002;
        const int SummaryTag = 1004;
        const int DescriptionTag = 1005;
        const int BuildTimeTag = 1006;
        const int BuildHostTag = 1007;
        const int VendorTag = 1011;
        const int LicenseTag = 1014;
        const int GroupTag = 1016;
        const int UrlTag = 1020;
        const int OsTag = 1021;
        const int ArchTag = 1022;
        AddStrings(header, I18nTableTag, ["C"]);
        AddString(header, NameTag, Input(NameInput));
        AddString(header, VersionTag, Version());
        AddString(header, ReleaseTag, Input(ReleaseInput));
        AddI18n(header, SummaryTag, Input("RPM_SUMMARY"));
        AddI18n(header, DescriptionTag, Input("RPM_DESCRIPTION"));
        AddInt32(header, BuildTimeTag, time);
        AddString(header, BuildHostTag, "localhost");
        if (Input("RPM_VENDOR") is { Length: > 0 } vendor)
        {
            AddString(header, VendorTag, vendor);
        }

        AddString(header, LicenseTag, Input("RPM_LICENSE"));
        AddI18n(header, GroupTag, "Unspecified");
        if (Input("RPM_URL") is { Length: > 0 } url)
        {
            AddString(header, UrlTag, url);
        }

        AddString(header, OsTag, "linux");
        AddString(header, ArchTag, Input(ArchitectureInput));
    }

    /// <summary>Builds the main header.</summary>
    /// <param name="payload">The entries.</param>
    /// <param name="time">The build time.</param>
    /// <param name="digests">The file digests.</param>
    /// <param name="summary">The payload lengths and digests.</param>
    /// <returns>The header bytes.</returns>
    private static byte[] MainHeader(List<Entry> payload, int time, string[] digests, in PayloadSummary summary)
    {
        const int ImmutableRegion = 63;
        var header = new Tags();
        PackageTags(header, time);
        PayloadTags(header, payload, summary);
        FileTags(header, payload, time, digests);
        DirectoryTags(header, payload);
        DependencyTags(header);
        return Write(header, ImmutableRegion);
    }

    /// <summary>Builds the signature header: the main header's SHA-256 and the sizes rpm checks before unpacking.</summary>
    /// <param name="header">The main header.</param>
    /// <param name="compressedLength">The compressed payload length.</param>
    /// <param name="uncompressedLength">The uncompressed payload length.</param>
    /// <returns>The signature header bytes.</returns>
    private static byte[] Signature(byte[] header, long compressedLength, long uncompressedLength)
    {
        const int SignatureRegion = 62;
        const int Sha256HeaderTag = 273;
        const int SizeTag = 1000;
        const int PayloadSizeTag = 1007;
        var signature = new Tags();
        AddString(signature, Sha256HeaderTag, Convert.ToHexStringLower(SHA256.HashData(header)));
        AddInt32(signature, SizeTag, (int)(header.Length + compressedLength));
        AddInt32(signature, PayloadSizeTag, (int)uncompressedLength);
        return Write(signature, SignatureRegion);
    }

    /// <summary>Adds the installed size and the payload format and digest tags.</summary>
    /// <param name="header">The main header.</param>
    /// <param name="payload">The entries.</param>
    /// <param name="summary">The payload lengths and digests.</param>
    private static void PayloadTags(Tags header, List<Entry> payload, in PayloadSummary summary)
    {
        const int SizeTag = 1009;
        const int PayloadFormatTag = 1124;
        const int PayloadCompressorTag = 1125;
        const int PayloadFlagsTag = 1126;
        const int FileDigestAlgorithmTag = 5011;
        const int PayloadDigestTag = 5092;
        const int PayloadDigestAlgorithmTag = 5093;
        const int PayloadDigestAltTag = 5097;
        const int Sha256 = 8;
        long size = 0;
        foreach (var entry in payload)
        {
            size += Length(entry);
        }

        AddInt32(header, SizeTag, (int)size);
        AddString(header, PayloadFormatTag, "cpio");
        AddString(header, PayloadCompressorTag, "gzip");
        AddString(header, PayloadFlagsTag, $"{BestCompression}");
        AddInt32(header, FileDigestAlgorithmTag, Sha256);
        AddStrings(header, PayloadDigestTag, [summary.CompressedDigest]);
        AddInt32(header, PayloadDigestAlgorithmTag, Sha256);
        AddStrings(header, PayloadDigestAltTag, [summary.Digest]);
    }

    /// <summary>Adds the per-file tags, one value per entry.</summary>
    /// <param name="header">The main header.</param>
    /// <param name="payload">The entries.</param>
    /// <param name="time">The modification time.</param>
    /// <param name="digests">The file digests.</param>
    private static void FileTags(Tags header, List<Entry> payload, int time, string[] digests)
    {
        const int FileSizesTag = 1028;
        const int FileModesTag = 1030;
        const int FileRdevsTag = 1033;
        const int FileMtimesTag = 1034;
        const int FileDigestsTag = 1035;
        const int FileLinkTosTag = 1036;
        const int FileFlagsTag = 1037;
        const int FileUserNameTag = 1039;
        const int FileGroupNameTag = 1040;
        const int FileVerifyFlagsTag = 1045;
        const int VerifyAll = -1;
        const int FileDevicesTag = 1095;
        const int FileInodesTag = 1096;
        const int FileLangsTag = 1097;
        const int DirectorySize = 4096;
        const int LicenseFlag = 1 << 7;
        var count = payload.Count;
        var sizes = new int[count];
        var modes = new int[count];
        var links = new string[count];
        var flags = new int[count];
        var inodes = new int[count];
        for (var i = 0; i < count; i++)
        {
            var entry = payload[i];
            sizes[i] = IsDirectory(entry) ? DirectorySize : (int)Length(entry);
            modes[i] = entry.Mode;
            links[i] = entry.Target ?? string.Empty;
            flags[i] = entry.Source is not null && entry.Path.StartsWith("/usr/share/licenses/", StringComparison.Ordinal) ? LicenseFlag : 0;
            inodes[i] = i + 1;
        }

        AddInt32(header, FileSizesTag, sizes);
        AddInt16(header, FileModesTag, modes);
        AddInt16(header, FileRdevsTag, new int[count]);
        AddInt32(header, FileMtimesTag, Filled(count, time));
        AddStrings(header, FileDigestsTag, digests);
        AddStrings(header, FileLinkTosTag, links);
        AddInt32(header, FileFlagsTag, flags);
        AddStrings(header, FileUserNameTag, Filled(count, "root"));
        AddStrings(header, FileGroupNameTag, Filled(count, "root"));

        // Without verify flags rpm -V checks nothing; all bits set verifies every attribute, as rpmbuild does by default.
        AddInt32(header, FileVerifyFlagsTag, Filled(count, VerifyAll));
        AddInt32(header, FileDevicesTag, Filled(count, 1));
        AddInt32(header, FileInodesTag, inodes);
        AddStrings(header, FileLangsTag, Filled(count, string.Empty));
    }

    /// <summary>Adds the compressed file names: a directory list, then each entry's directory index and base name.</summary>
    /// <param name="header">The main header.</param>
    /// <param name="payload">The entries.</param>
    private static void DirectoryTags(Tags header, List<Entry> payload)
    {
        const int DirIndexesTag = 1116;
        const int BaseNamesTag = 1117;
        const int DirNamesTag = 1118;
        List<string> directories = [];
        var indexes = new int[payload.Count];
        var baseNames = new string[payload.Count];
        for (var i = 0; i < payload.Count; i++)
        {
            var path = payload[i].Path;
            var split = path.LastIndexOf('/') + 1;
            var directory = path[..split];
            var known = directories.IndexOf(directory);
            if (known < 0)
            {
                known = directories.Count;
                directories.Add(directory);
            }

            indexes[i] = known;
            baseNames[i] = path[split..];
        }

        AddInt32(header, DirIndexesTag, indexes);
        AddStrings(header, BaseNamesTag, baseNames);
        AddStrings(header, DirNamesTag, [.. directories]);
    }

    /// <summary>Makes an array holding one value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="count">The length.</param>
    /// <param name="value">The value.</param>
    /// <returns>The array.</returns>
    private static T[] Filled<T>(int count, T value)
    {
        var values = new T[count];
        Array.Fill(values, value);
        return values;
    }

    /// <summary>Adds the dependency tags: provides itself, requires the rpmlib features this layout uses and the requested packages.</summary>
    /// <param name="header">The main header.</param>
    private static void DependencyTags(Tags header)
    {
        const int ProvideNameTag = 1047;
        const int RequireFlagsTag = 1048;
        const int RequireNameTag = 1049;
        const int RequireVersionTag = 1050;
        const int ProvideFlagsTag = 1112;
        const int ProvideVersionTag = 1113;
        const int RecommendNameTag = 5046;
        const int RecommendVersionTag = 5047;
        const int RecommendFlagsTag = 5048;
        const DependencyFlags RpmLibAtMost = DependencyFlags.RpmLib | DependencyFlags.Less | DependencyFlags.Equal;
        AddStrings(header, ProvideNameTag, [Input(NameInput)]);
        AddInt32(header, ProvideFlagsTag, (int)DependencyFlags.Equal);
        AddStrings(header, ProvideVersionTag, [$"{Version()}-{Input(ReleaseInput)}"]);
        List<Dependency> requires =
        [
            ("rpmlib(PayloadFilesHavePrefix)", RpmLibAtMost, "4.0-1"),
            ("rpmlib(CompressedFileNames)", RpmLibAtMost, "3.0.4-1"),
            ("rpmlib(FileDigests)", RpmLibAtMost, "4.6.0-1"),
        ];
        foreach (var line in Lines("RPM_REQUIRES"))
        {
            requires.Add(ParseDependency(line));
        }

        AddDependencies(header, requires, (RequireNameTag, RequireFlagsTag, RequireVersionTag));
        if (Lines("RPM_RECOMMENDS") is { Length: > 0 } recommends)
        {
            List<Dependency> parsed = [];
            foreach (var line in recommends)
            {
                parsed.Add(ParseDependency(line));
            }

            AddDependencies(header, parsed, (RecommendNameTag, RecommendFlagsTag, RecommendVersionTag));
        }
    }

    /// <summary>Adds a dependency list as parallel name, flags and version tags.</summary>
    /// <param name="header">The main header.</param>
    /// <param name="dependencies">The dependencies.</param>
    /// <param name="tags">The name, flags and version tags.</param>
    private static void AddDependencies(Tags header, List<Dependency> dependencies, (int Name, int Flags, int Version) tags)
    {
        var names = new string[dependencies.Count];
        var flags = new int[dependencies.Count];
        var versions = new string[dependencies.Count];
        for (var i = 0; i < dependencies.Count; i++)
        {
            names[i] = dependencies[i].Name;
            flags[i] = (int)dependencies[i].Flags;
            versions[i] = dependencies[i].Version;
        }

        AddInt32(header, tags.Flags, flags);
        AddStrings(header, tags.Name, names);
        AddStrings(header, tags.Version, versions);
    }

    /// <summary>Parses "name", or "name OP version" with OP one of &lt; &lt;= = &gt;= &gt;.</summary>
    /// <param name="line">The dependency line.</param>
    /// <returns>The dependency.</returns>
    /// <exception cref="ArgumentException">The operator is not one of these.</exception>
    private static Dependency ParseDependency(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries) switch
        {
            [var name, var comparison, var version] => (name, comparison switch
            {
                "<" => DependencyFlags.Less,
                "<=" => DependencyFlags.Less | DependencyFlags.Equal,
                "=" => DependencyFlags.Equal,
                ">=" => DependencyFlags.Greater | DependencyFlags.Equal,
                ">" => DependencyFlags.Greater,
                _ => throw new ArgumentException($"Unknown operator in '{line}'.", nameof(line)),
            }, version),
            _ => (line, DependencyFlags.None, string.Empty),
        };

    /// <summary>Adds a string tag.</summary>
    /// <param name="header">The header.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="value">The value.</param>
    private static void AddString(Tags header, int tag, string value) => header[tag] = (HeaderType.String, 1, Encoding.UTF8.GetBytes($"{value}\0"));

    /// <summary>Adds a translatable string tag.</summary>
    /// <param name="header">The header.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="value">The value.</param>
    private static void AddI18n(Tags header, int tag, string value) => header[tag] = (HeaderType.I18n, 1, Encoding.UTF8.GetBytes($"{value}\0"));

    /// <summary>Adds a string array tag.</summary>
    /// <param name="header">The header.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="values">The values.</param>
    private static void AddStrings(Tags header, int tag, string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            _ = builder.Append(value).Append('\0');
        }

        header[tag] = (HeaderType.StringArray, values.Length, Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>Adds a 32-bit integer tag.</summary>
    /// <param name="header">The header.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="values">The values.</param>
    private static void AddInt32(Tags header, int tag, params int[] values)
    {
        var data = new byte[values.Length * sizeof(int)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(i * sizeof(int)), values[i]);
        }

        header[tag] = (HeaderType.Int32, values.Length, data);
    }

    /// <summary>Adds a 16-bit integer tag.</summary>
    /// <param name="header">The header.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="values">The values.</param>
    private static void AddInt16(Tags header, int tag, int[] values)
    {
        var data = new byte[values.Length * sizeof(ushort)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * sizeof(ushort)), (ushort)values[i]);
        }

        header[tag] = (HeaderType.Int16, values.Length, data);
    }

    /// <summary>Writes an RPM header: magic, counts, the region entry and the sorted index, then the store with the region trailer.</summary>
    /// <param name="header">The tags.</param>
    /// <param name="region">The region tag.</param>
    /// <returns>The header bytes.</returns>
    private static byte[] Write(Tags header, int region)
    {
        const int EntryCountOffset = 8;
        const int StoreLengthOffset = 12;
        var store = new ArrayBufferWriter<byte>();
        List<(int Tag, HeaderType Type, int Offset, int Count)> index = [];
        foreach (var (tag, (type, count, data)) in header)
        {
            var alignment = type switch { HeaderType.Int16 => sizeof(short), HeaderType.Int32 => sizeof(int), _ => 1 };
            var padding = Pad(store.WrittenCount, alignment);
            store.GetSpan(padding)[..padding].Clear();
            store.Advance(padding);
            index.Add((tag, type, store.WrittenCount, count));
            store.Write(data);
        }

        var regionOffset = store.WrittenCount;
        var total = index.Count + 1;
        WriteEntry(store.GetSpan(EntryLength), (region, HeaderType.Bin, -(total * EntryLength), EntryLength));
        store.Advance(EntryLength);

        var bytes = new byte[EntryLength + (total * EntryLength) + store.WrittenCount];
        HeaderMagic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(EntryCountOffset), total);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(StoreLengthOffset), store.WrittenCount);
        WriteEntry(bytes.AsSpan(EntryLength), (region, HeaderType.Bin, regionOffset, EntryLength));
        for (var i = 0; i < index.Count; i++)
        {
            WriteEntry(bytes.AsSpan(EntryLength + ((i + 1) * EntryLength)), index[i]);
        }

        store.WrittenSpan.CopyTo(bytes.AsSpan((total + 1) * EntryLength));
        return bytes;
    }

    /// <summary>Writes one header index entry: tag, type, store offset and value count, all big-endian.</summary>
    /// <param name="target">Receives the entry.</param>
    /// <param name="entry">The entry.</param>
    private static void WriteEntry(Span<byte> target, (int Tag, HeaderType Type, int Offset, int Count) entry)
    {
        const int TypeOffset = 4;
        const int StoreOffset = 8;
        const int CountOffset = 12;
        BinaryPrimitives.WriteInt32BigEndian(target, entry.Tag);
        BinaryPrimitives.WriteInt32BigEndian(target[TypeOffset..], (int)entry.Type);
        BinaryPrimitives.WriteInt32BigEndian(target[StoreOffset..], entry.Offset);
        BinaryPrimitives.WriteInt32BigEndian(target[CountOffset..], entry.Count);
    }

    /// <summary>Makes the 96 byte lead: magic, version 3.0, binary package, architecture, name, Linux, header-style signature, reserved.</summary>
    /// <returns>The lead.</returns>
    private static byte[] Lead()
    {
        const uint LeadMagic = 0xEDABEEDB;
        const int MajorVersionOffset = 4;
        const byte MajorVersion = 3;
        const int ArchitectureOffset = 8;
        const ushort X8664 = 1;
        const ushort Aarch64 = 19;
        const int NameOffset = 10;
        const int NameLength = 66;
        const int OsOffset = 76;
        const ushort Linux = 1;
        const int SignatureTypeOffset = 78;
        const ushort HeaderSignature = 5;
        var lead = new byte[LeadLength];
        BinaryPrimitives.WriteUInt32BigEndian(lead, LeadMagic);
        lead[MajorVersionOffset] = MajorVersion;
        BinaryPrimitives.WriteUInt16BigEndian(lead.AsSpan(ArchitectureOffset), Input(ArchitectureInput) == "x86_64" ? X8664 : Aarch64);

        // The name is NUL-terminated within its field, so it keeps at most one byte fewer than the field.
        var name = $"{Input(NameInput)}-{Version()}-{Input(ReleaseInput)}";
        _ = Encoding.ASCII.GetBytes(name.AsSpan(0, Math.Min(name.Length, NameLength - 1)), lead.AsSpan(NameOffset));
        BinaryPrimitives.WriteUInt16BigEndian(lead.AsSpan(OsOffset), Linux);
        BinaryPrimitives.WriteUInt16BigEndian(lead.AsSpan(SignatureTypeOffset), HeaderSignature);
        return lead;
    }

    /// <summary>The numeric fields of a cpio header.</summary>
    /// <param name="Inode">The inode number.</param>
    /// <param name="Mode">The file type and permission bits.</param>
    /// <param name="Links">The link count.</param>
    /// <param name="Time">The modification time.</param>
    /// <param name="Size">The data length.</param>
    private readonly record struct CpioFields(int Inode, int Mode, int Links, int Time, long Size);

    /// <summary>The payload's uncompressed and compressed lengths and SHA-256 digests.</summary>
    /// <param name="Length">The uncompressed length.</param>
    /// <param name="Digest">The uncompressed SHA-256, as lower-case hex.</param>
    /// <param name="CompressedLength">The compressed length.</param>
    /// <param name="CompressedDigest">The compressed SHA-256, as lower-case hex.</param>
    private readonly record struct PayloadSummary(long Length, string Digest, long CompressedLength, string CompressedDigest);

    /// <summary>Gzip compresses the payload into the package at a running offset, hashing both forms as they pass.</summary>
    private sealed class PayloadWriter : IDisposable
    {
        /// <summary>The package.</summary>
        private readonly SafeFileHandle _package;

        /// <summary>Where the payload starts.</summary>
        private readonly long _start;

        /// <summary>The gzip encoder.</summary>
        private readonly GZipEncoder _encoder = new(BestCompression);

        /// <summary>The digest of the uncompressed bytes.</summary>
        private readonly IncrementalHash _uncompressed = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        /// <summary>The digest of the compressed bytes.</summary>
        private readonly IncrementalHash _compressed = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        /// <summary>The compressed output buffer.</summary>
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferLength);

        /// <summary>The uncompressed length so far.</summary>
        private long _length;

        /// <summary>Where the next compressed bytes go.</summary>
        private long _offset;

        /// <summary>Initializes a new instance of the <see cref="PayloadWriter"/> class.</summary>
        /// <param name="package">The package.</param>
        /// <param name="offset">Where the payload starts.</param>
        public PayloadWriter(SafeFileHandle package, long offset)
        {
            _package = package;
            _start = offset;
            _offset = offset;
        }

        /// <summary>Compresses bytes into the package.</summary>
        /// <param name="bytes">The uncompressed bytes.</param>
        /// <exception cref="InvalidDataException">The encoder failed.</exception>
        public void Write(ReadOnlySpan<byte> bytes)
        {
            _uncompressed.AppendData(bytes);
            _length += bytes.Length;
            while (!bytes.IsEmpty)
            {
                if (_encoder.Compress(bytes, _buffer, out var consumed, out var written, isFinalBlock: false) is OperationStatus.InvalidData)
                {
                    throw new InvalidDataException("The gzip encoder rejected the payload.");
                }

                Flush(written);
                bytes = bytes[consumed..];
            }
        }

        /// <summary>Ends the gzip stream and reports the lengths and digests.</summary>
        /// <returns>The payload summary.</returns>
        /// <exception cref="InvalidDataException">The encoder failed.</exception>
        public PayloadSummary Finish()
        {
            OperationStatus status;
            do
            {
                status = _encoder.Compress([], _buffer, out _, out var written, isFinalBlock: true);
                if (status is OperationStatus.InvalidData)
                {
                    throw new InvalidDataException("The gzip encoder could not end the payload.");
                }

                Flush(written);
            }
            while (status is OperationStatus.DestinationTooSmall);

            return new(_length, Convert.ToHexStringLower(_uncompressed.GetHashAndReset()), _offset - _start, Convert.ToHexStringLower(_compressed.GetHashAndReset()));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _encoder.Dispose();
            _uncompressed.Dispose();
            _compressed.Dispose();
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        /// <summary>Writes the encoder's output at the running offset.</summary>
        /// <param name="written">The bytes in the buffer.</param>
        private void Flush(int written)
        {
            var chunk = _buffer.AsSpan(0, written);
            RandomAccess.Write(_package, chunk, _offset);
            _compressed.AppendData(chunk);
            _offset += written;
        }
    }
}
