// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using static System.Environment;

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

var output = Program.Input("DEB_OUTPUT");

var entries = Program.Build(output, TimeProvider.System);

Console.WriteLine($"Built {output}: {entries} entries.");

return 0;

/// <summary>Builds a .deb from a folder laid out as the installed tree.</summary>
internal static partial class Program
{
    /// <summary>The mode for directories and executables: 0755.</summary>
    private const UnixFileMode Executable = (UnixFileMode)0x1ED;

    /// <summary>The mode for other files: 0644.</summary>
    private const UnixFileMode Regular = (UnixFileMode)0x1A4;

    /// <summary>The gzip level for both archives.</summary>
    private const int BestCompression = 9;

    /// <summary>The length of an ar member header.</summary>
    private const int ArHeaderLength = 60;

    /// <summary>The tar block size; entries and their data are padded to it.</summary>
    private const int TarBlock = 512;

    /// <summary>Reads an action input from the environment.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value, or empty when unset.</returns>
    internal static string Input(string name) => GetEnvironmentVariable(name) ?? string.Empty;

    /// <summary>Builds the package, streaming the data archive straight into the .deb.</summary>
    /// <param name="output">The .deb path.</param>
    /// <param name="clock">The clock for the modification times when SOURCE_DATE_EPOCH is unset.</param>
    /// <returns>The number of entries in the tree.</returns>
    internal static int Build(string output, TimeProvider clock)
    {
        var modified = DateTimeOffset.FromUnixTimeSeconds(
            long.TryParse(Input("SOURCE_DATE_EPOCH"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ? epoch : clock.GetUtcNow().ToUnixTimeSeconds());
        var source = Path.GetFullPath(Input("DEB_SOURCE"));
        var entries = Directory.GetFileSystemEntries(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 });
        Array.Sort(entries, StringComparer.Ordinal);

        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var package = new FileStream(output, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write });

        // The .deb is an ar archive: debian-binary, then the control and data archives.
        package.Write("!<arch>\n"u8);
        WriteMember(package, "debian-binary", "2.0\n"u8, modified);
        WriteControl(package, Control(InstalledSize(entries)), modified);
        WriteData(package, source, entries, modified);
        return entries.Length;
    }

    /// <summary>Reads a multi-line input as trimmed, non-empty lines.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The lines.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string[] Lines(string name) => Input(name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Totals the size of the regular files, which the control file needs before the data is written.</summary>
    /// <param name="entries">The tree's paths.</param>
    /// <returns>The total size in bytes.</returns>
    private static long InstalledSize(string[] entries)
    {
        long size = 0;
        foreach (var path in entries)
        {
            var info = new FileInfo(path);
            if (info.Exists && info.LinkTarget is null)
            {
                size += info.Length;
            }
        }

        return size;
    }

    /// <summary>Writes the control.tar.gz member. The archive is small, so it is built in pooled buffers.</summary>
    /// <param name="package">The .deb.</param>
    /// <param name="control">The control file text.</param>
    /// <param name="modified">The modification time.</param>
    /// <exception cref="InvalidOperationException">The archive did not fit its compression buffer.</exception>
    private static void WriteControl(Stream package, string control, DateTimeOffset modified)
    {
        // One header block, the data padded to a block, then two zero blocks to end the archive.
        const int EndBlocks = 2;
        var content = Encoding.UTF8.GetBytes(control);
        var tarLength = TarBlock + (((content.Length + TarBlock - 1) / TarBlock) * TarBlock) + (EndBlocks * TarBlock);
        var tar = ArrayPool<byte>.Shared.Rent(tarLength);
        var compressed = ArrayPool<byte>.Shared.Rent((int)GZipEncoder.GetMaxCompressedLength(tarLength));
        try
        {
            using var tarStream = new WritableMemoryStream(tar);
            using (var writer = new TarWriter(tarStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                using var data = new ReadOnlyMemoryStream(content);
                var file = Entry(TarEntryType.RegularFile, "./control", Regular, modified);
                file.DataStream = data;
                writer.WriteEntry(file);
            }

            if (!GZipEncoder.TryCompress(tar.AsSpan(0, (int)tarStream.Position), compressed, out var written, BestCompression))
            {
                throw new InvalidOperationException("The control archive did not fit its compression buffer.");
            }

            WriteMember(package, "control.tar.gz", compressed.AsSpan(0, written), modified);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
            ArrayPool<byte>.Shared.Return(tar);
        }
    }

    /// <summary>Streams the data.tar.gz member: every directory, file and symlink of the tree, owned by root.</summary>
    /// <param name="package">The .deb, positioned after the control member.</param>
    /// <param name="source">The staged tree.</param>
    /// <param name="entries">The tree's paths, sorted.</param>
    /// <param name="modified">The modification time.</param>
    private static void WriteData(FileStream package, string source, string[] entries, DateTimeOffset modified)
    {
        // The member size is only known once the archive is compressed, so its header is written over a placeholder.
        Span<byte> header = stackalloc byte[ArHeaderLength];
        var headerOffset = package.Position;
        package.Write(header);
        using (var gzip = new GZipStream(package, new ZLibCompressionOptions { CompressionLevel = BestCompression }, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var executables = Lines("DEB_EXECUTABLES");
            tar.WriteEntry(Entry(TarEntryType.Directory, "./", Executable, modified));
            foreach (var path in entries)
            {
                WriteDataEntry(tar, path, Path.GetRelativePath(source, path).Replace('\\', '/'), executables, modified);
            }
        }

        var length = package.Position - headerOffset - ArHeaderLength;
        WritePadding(package, length);
        package.Flush();
        ArHeader(header, "data.tar.gz", modified, length);
        RandomAccess.Write(package.SafeFileHandle, header, headerOffset);
    }

    /// <summary>Writes one tree entry to the data archive.</summary>
    /// <param name="tar">The data archive.</param>
    /// <param name="path">The file system path.</param>
    /// <param name="relative">The path within the tree, with forward slashes.</param>
    /// <param name="executables">Paths or globs that are executable on Windows.</param>
    /// <param name="modified">The modification time.</param>
    private static void WriteDataEntry(TarWriter tar, string path, string relative, string[] executables, DateTimeOffset modified)
    {
        const UnixFileMode LinkMode = (UnixFileMode)0x1FF;
        var info = new FileInfo(path);
        if (info.LinkTarget is { } target)
        {
            var link = Entry(TarEntryType.SymbolicLink, $"./{relative}", LinkMode, modified);
            link.LinkName = target;
            tar.WriteEntry(link);
            return;
        }

        if (Directory.Exists(path))
        {
            tar.WriteEntry(Entry(TarEntryType.Directory, $"./{relative}/", Mode(path, relative, true, executables), modified));
            return;
        }

        using var content = new FileStream(path, new FileStreamOptions { Options = FileOptions.SequentialScan });
        var file = Entry(TarEntryType.RegularFile, $"./{relative}", Mode(path, relative, false, executables), modified);
        file.DataStream = content;
        tar.WriteEntry(file);
    }

    /// <summary>Gets the permission bits. Windows has none, so directories and listed executables get 0755 and the rest 0644.</summary>
    /// <param name="path">The file system path.</param>
    /// <param name="relative">The path within the tree, with forward slashes.</param>
    /// <param name="directory">Whether the path is a directory.</param>
    /// <param name="executables">Paths or globs that are executable on Windows.</param>
    /// <returns>The permission bits.</returns>
    private static UnixFileMode Mode(string path, string relative, bool directory, string[] executables)
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.GetUnixFileMode(path);
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

    /// <summary>Writes the control file.</summary>
    /// <param name="installedSize">The total file size in bytes.</param>
    /// <returns>The control file text.</returns>
    private static string Control(long installedSize)
    {
        const int Kibibyte = 1024;
        var control = new StringBuilder();
        _ = control.Append("Package: ").Append(Input("DEB_NAME")).Append('\n');
        _ = control.Append("Version: ").Append(Input("DEB_VERSION").Split('+')[0].Replace('-', '~')).Append('\n');
        _ = control.Append("Architecture: ").Append(Input("DEB_ARCH")).Append('\n');
        _ = control.Append("Maintainer: ").Append(Input("DEB_MAINTAINER")).Append('\n');
        _ = control.Append("Installed-Size: ").Append((installedSize + Kibibyte - 1) / Kibibyte).Append('\n');
        if (Lines("DEB_DEPENDS") is { Length: > 0 } depends)
        {
            _ = control.Append("Depends: ").AppendJoin(", ", depends).Append('\n');
        }

        if (Lines("DEB_RECOMMENDS") is { Length: > 0 } recommends)
        {
            _ = control.Append("Recommends: ").AppendJoin(", ", recommends).Append('\n');
        }

        _ = control.Append("Section: ").Append(Input("DEB_SECTION")).Append("\nPriority: optional\n");
        if (Input("DEB_HOMEPAGE") is { Length: > 0 } homepage)
        {
            _ = control.Append("Homepage: ").Append(homepage).Append('\n');
        }

        // The long description is indented one space, with a lone "." standing for a blank line.
        _ = control.Append("Description: ").Append(Input("DEB_SUMMARY")).Append('\n');
        foreach (var line in Input("DEB_DESCRIPTION").Split('\n'))
        {
            _ = control.Append(' ').Append(string.IsNullOrWhiteSpace(line) ? "." : line.TrimEnd()).Append('\n');
        }

        return control.ToString();
    }

    /// <summary>Makes a tar entry owned by root.</summary>
    /// <param name="type">The entry type.</param>
    /// <param name="name">The entry name.</param>
    /// <param name="mode">The permission bits.</param>
    /// <param name="modified">The modification time.</param>
    /// <returns>The entry.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UstarTarEntry Entry(TarEntryType type, string name, UnixFileMode mode, DateTimeOffset modified) =>
        new(type, name) { Mode = mode, Uid = 0, Gid = 0, UserName = "root", GroupName = "root", ModificationTime = modified };

    /// <summary>Writes an ar member: the header, the content, then a newline when the length is odd.</summary>
    /// <param name="package">The .deb.</param>
    /// <param name="name">The member name.</param>
    /// <param name="content">The member content.</param>
    /// <param name="modified">The modification time.</param>
    private static void WriteMember(Stream package, string name, ReadOnlySpan<byte> content, DateTimeOffset modified)
    {
        Span<byte> header = stackalloc byte[ArHeaderLength];
        ArHeader(header, name, modified, content.Length);
        package.Write(header);
        package.Write(content);
        WritePadding(package, content.Length);
    }

    /// <summary>Writes the newline that keeps the next ar member on an even offset.</summary>
    /// <param name="package">The .deb.</param>
    /// <param name="length">The member length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePadding(Stream package, long length)
    {
        if (length % sizeof(short) != 0)
        {
            package.WriteByte((byte)'\n');
        }
    }

    /// <summary>Formats the fixed-width ar member header: name, time, owner, group, mode, size and terminator.</summary>
    /// <param name="header">Receives the header.</param>
    /// <param name="name">The member name.</param>
    /// <param name="modified">The modification time.</param>
    /// <param name="length">The member length.</param>
    /// <exception cref="InvalidOperationException">The fields overflow their widths.</exception>
    private static void ArHeader(Span<byte> header, string name, DateTimeOffset modified, long length)
    {
        const int NameWidth = -16;
        const int TimeWidth = -12;
        const int OwnerWidth = -6;
        const int ModeWidth = -8;
        const int SizeWidth = -10;
        var seconds = modified.ToUnixTimeSeconds();
        if (!Utf8.TryWrite(header, $"{name,NameWidth}{seconds,TimeWidth}{0,OwnerWidth}{0,OwnerWidth}{"100644",ModeWidth}{length,SizeWidth}`\n", out var written)
            || written != ArHeaderLength)
        {
            throw new InvalidOperationException($"The ar header for {name} does not fit {ArHeaderLength} bytes.");
        }
    }
}
