// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

return await Program.RunAsync().ConfigureAwait(false);

/// <summary>Builds a type-2 AppImage: the AppImage runtime followed by a SquashFS 4 image of the AppDir.</summary>
internal static partial class Program
{
    /// <summary>The pinned AppImage/type2-runtime release.</summary>
    private const string RuntimeRelease = "https://github.com/AppImage/type2-runtime/releases/download/20251108/";

    /// <summary>The desktop entry key that names the icon.</summary>
    private const string IconKey = "Icon=";

    /// <summary>The SquashFS magic number, "hsqs".</summary>
    private const uint Magic = 0x73717368;

    /// <summary>The SquashFS major version.</summary>
    private const ushort VersionMajor = 4;

    /// <summary>The compression identifier for zlib.</summary>
    private const ushort CompressionZlib = 1;

    /// <summary>The zlib quality; 9 gives the smallest output.</summary>
    private const int ZlibLevel = 9;

    /// <summary>The superblock flags: uncompressed inodes, fragments, xattrs and ids; no fragments; no xattrs.</summary>
    private const ushort Flags = 0x1 | 0x8 | 0x10 | 0x100 | 0x200 | 0x800;

    /// <summary>The base two logarithm of the data block size.</summary>
    private const ushort BlockLog = 17;

    /// <summary>The data block size.</summary>
    private const int BlockSize = 1 << BlockLog;

    /// <summary>The superblock length.</summary>
    private const int SuperblockLength = 96;

    /// <summary>The boundary the image is padded to.</summary>
    private const int PadBoundary = 4096;

    /// <summary>The metadata block size.</summary>
    private const int MetadataSize = 8192;

    /// <summary>The bytes a metadata block takes on disk, with its two-byte header.</summary>
    private const int MetadataStride = MetadataSize + sizeof(ushort);

    /// <summary>The metadata header bit that marks an uncompressed block.</summary>
    private const ushort MetadataUncompressed = 0x8000;

    /// <summary>The data block size bit that marks an uncompressed block.</summary>
    private const uint DataUncompressed = 1U << 24;

    /// <summary>The bits an inode reference shifts its metadata block offset by.</summary>
    private const int ReferenceShift = 16;

    /// <summary>The mask of the offset inside the metadata block of an inode reference.</summary>
    private const long ReferenceOffsetMask = 0xFFFF;

    /// <summary>The permission bits of a mode.</summary>
    private const int PermissionMask = 0xFFF;

    /// <summary>The permission bits of a symlink.</summary>
    private const int LinkMode = 0x1FF;

    /// <summary>The mode of a directory where the file system has no Unix modes (rwxr-xr-x).</summary>
    private const int DefaultDirectoryMode = 0x1ED;

    /// <summary>The mode of a file where the file system has no Unix modes (rw-r--r--).</summary>
    private const int DefaultFileMode = 0x1A4;

    /// <summary>The execute bits for user, group and others.</summary>
    private const int ExecuteBits = 0x49;

    /// <summary>The inode type of an extended directory.</summary>
    private const ushort InodeExtendedDirectory = 8;

    /// <summary>The value SquashFS adds to a directory listing size.</summary>
    private const int DirectorySizeBias = 3;

    /// <summary>The link count of a directory before its subdirectories.</summary>
    private const uint DirectoryLinkBase = 2;

    /// <summary>The most entries under one directory header.</summary>
    private const int MaxHeaderEntries = 256;

    /// <summary>The value of an absent fragment, xattr or table.</summary>
    private const uint None = uint.MaxValue;

    /// <summary>The permissions of the AppImage file (rwxr-xr-x).</summary>
    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>The icon file extensions an AppDir root may hold.</summary>
    private static readonly string[] IconExtensions = [".png", ".svg", ".xpm"];

    /// <summary>Lists every entry of one folder, hidden ones included.</summary>
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0 };

    /// <summary>The client that downloads the runtime.</summary>
    private static readonly HttpClient Client = new();

    /// <summary>The kind of an entry, valued as its basic SquashFS inode type.</summary>
    internal enum Kind
    {
        /// <summary>A directory.</summary>
        Directory = 1,

        /// <summary>A regular file.</summary>
        File = 2,

        /// <summary>A symbolic link.</summary>
        Symlink = 3,
    }

    /// <summary>Builds the AppImage from the APPIMAGE_* environment variables.</summary>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync()
    {
        var appDir = Path.GetFullPath(Input("APPIMAGE_APPDIR"));
        var output = Path.GetFullPath(Input("APPIMAGE_OUTPUT"));
        var (runtimeUrl, runtimeSha256, problem) = Runtime(Input("APPIMAGE_ARCH"), Input("APPIMAGE_RUNTIME_URL"), Input("APPIMAGE_RUNTIME_SHA256").ToLowerInvariant());
        if ((problem ?? CheckAppDir(appDir)) is { } message)
        {
            Console.WriteLine($"::error::{message}");
            return 1;
        }

        var runtime = await Client.GetByteArrayAsync(new Uri(runtimeUrl)).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(SHA256.HashData(runtime));
        if (actual != runtimeSha256)
        {
            Console.WriteLine($"::error::The runtime from {runtimeUrl} has SHA-256 {actual}, expected {runtimeSha256}.");
            return 1;
        }

        // AppRun and the listed files are executable even where the file system has no Unix modes, such as on Windows.
        HashSet<string> executables = ["AppRun"];
        foreach (var line in Input("APPIMAGE_EXECUTABLES").Split('\n'))
        {
            _ = executables.Add(line.Trim().Replace('\\', '/').Trim('/'));
        }

        var root = new Node(string.Empty, Kind.Directory, Mode(appDir, isDirectory: true, isExecutable: false), appDir, null);
        AddChildren(root, appDir, string.Empty, executables);
        var time = uint.TryParse(Input("SOURCE_DATE_EPOCH"), out var epoch) ? epoch : (uint)TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        WriteAppImage(output, runtime, root, time);
        Console.WriteLine($"Built {output}: {new FileInfo(output).Length} bytes, runtime {runtimeUrl}.");
        return 0;
    }

    /// <summary>Reads an input from its environment variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The trimmed value, or empty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Input(string name) => (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();

    /// <summary>Picks the runtime: the given one, or the pinned release for the architecture.</summary>
    /// <param name="architecture">The architecture; x86_64 and aarch64 have pinned runtimes.</param>
    /// <param name="url">The given runtime address, or empty.</param>
    /// <param name="sha256">The given lowercase SHA-256, or empty.</param>
    /// <returns>The runtime address and SHA-256, or a problem.</returns>
    private static (string Url, string Sha256, string? Problem) Runtime(string architecture, string url, string sha256)
    {
        // The hashes are the release asset digests.
        var pinned = architecture switch
        {
            "x86_64" => "2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d",
            "aarch64" => "00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444",
            _ => null,
        };

        if (url.Length > 0)
        {
            return (url, sha256, sha256.Length == 0 ? "runtime-url needs runtime-sha256." : null);
        }

        return pinned is null
            ? (url, sha256, $"No pinned runtime for '{architecture}'; use x86_64 or aarch64, or give runtime-url and runtime-sha256.")
            : ($"{RuntimeRelease}runtime-{architecture}", sha256.Length == 0 ? pinned : sha256, null);
    }

    /// <summary>Checks the AppImage specification's root entries: AppRun, one desktop file and the icon it names.</summary>
    /// <param name="appDir">The AppDir.</param>
    /// <returns>The problem, or null.</returns>
    private static string? CheckAppDir(string appDir)
    {
        var appRun = Path.Combine(appDir, "AppRun");
        if (!File.Exists(appRun) && new FileInfo(appRun).LinkTarget is null)
        {
            return $"{appDir} has no AppRun.";
        }

        var desktops = Directory.GetFiles(appDir, "*.desktop");
        if (desktops.Length != 1)
        {
            return $"{appDir} needs exactly one .desktop file at its root; found {desktops.Length}.";
        }

        foreach (var line in File.ReadLines(desktops[0]))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(IconKey, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var extension in IconExtensions)
            {
                if (Path.Exists(Path.Combine(appDir, trimmed[IconKey.Length..].Trim() + extension)))
                {
                    return null;
                }
            }

            break;
        }

        return $"{Path.GetFileName(desktops[0])} must name an icon (Icon=) whose .png, .svg or .xpm is at the AppDir root.";
    }

    /// <summary>Adds a folder's entries without following directory symlinks, sorted by the bytes of their names.</summary>
    /// <param name="parent">The folder's node.</param>
    /// <param name="directory">The folder.</param>
    /// <param name="relative">The folder's path inside the AppDir, with forward slashes.</param>
    /// <param name="executables">The paths inside the AppDir to make executable.</param>
    private static void AddChildren(Node parent, string directory, string relative, HashSet<string> executables)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", AllEntries))
        {
            var name = Path.GetFileName(path);
            var inside = relative.Length == 0 ? name : $"{relative}/{name}";
            if (new FileInfo(path).LinkTarget is { } target)
            {
                parent.Children.Add(new(name, Kind.Symlink, LinkMode, null, target.Replace('\\', '/')));
            }
            else if (Directory.Exists(path))
            {
                var child = new Node(name, Kind.Directory, Mode(path, isDirectory: true, isExecutable: false), path, null);
                AddChildren(child, path, inside, executables);
                parent.Children.Add(child);
            }
            else
            {
                parent.Children.Add(new(name, Kind.File, Mode(path, isDirectory: false, executables.Contains(inside)), path, null));
            }
        }

        parent.Children.Sort(static (left, right) => left.NameBytes.AsSpan().SequenceCompareTo(right.NameBytes));
    }

    /// <summary>Gets an entry's permission bits: from the file system where it has Unix modes, otherwise a default.</summary>
    /// <param name="path">The entry on disk.</param>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    /// <param name="isExecutable">Whether to add the execute bits.</param>
    /// <returns>The permission bits.</returns>
    private static int Mode(string path, bool isDirectory, bool isExecutable)
    {
        var execute = isExecutable ? ExecuteBits : 0;
        if (OperatingSystem.IsWindows())
        {
            return (isDirectory ? DefaultDirectoryMode : DefaultFileMode) | execute;
        }

        return ((int)File.GetUnixFileMode(path) & PermissionMask) | execute;
    }

    /// <summary>Writes the runtime, then the image after it; the file is created executable where the file system allows.</summary>
    /// <param name="output">The AppImage path.</param>
    /// <param name="runtime">The runtime, which finds the image at the end of its own ELF file.</param>
    /// <param name="root">The root directory.</param>
    /// <param name="time">The modification time of every inode, in Unix seconds.</param>
    private static void WriteAppImage(string output, byte[] runtime, Node root, uint time)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Delete(output);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, BufferSize = 0 };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = Executable;
        }

        using var file = new FileStream(output, options);
        RandomAccess.Write(file.SafeFileHandle, runtime, 0);
        WriteImage(new(file.SafeFileHandle, runtime.Length, time), root);
    }

    /// <summary>Writes a SquashFS 4 image in which every inode is owned by root.</summary>
    /// <param name="image">The image being written.</param>
    /// <param name="root">The root directory.</param>
    private static void WriteImage(Image image, Node root)
    {
        image.Offset += SuperblockLength;
        Number(root, image);
        WriteNode(image, root, image.InodeCount + 1);

        var inodeTable = image.Position;
        WriteMetadata(image, image.Inodes.WrittenSpan);
        var directoryTable = image.Position;
        WriteMetadata(image, image.Directories.WrittenSpan);

        // One metadata block holding id 0, used as both uid and gid, then the table pointing at it.
        var idBlock = image.Position;
        var tail = new ArrayBufferWriter<byte>();
        Put16(tail, MetadataUncompressed | sizeof(uint));
        Put32(tail, 0);
        var idTable = idBlock + tail.WrittenCount;
        Put64(tail, (ulong)idBlock);
        var used = idTable + sizeof(ulong);
        tail.Write(new byte[(PadBoundary - (int)(used % PadBoundary)) % PadBoundary]);
        image.Append(tail.WrittenSpan);

        var superblock = new ArrayBufferWriter<byte>(SuperblockLength);
        Put32(superblock, Magic);
        Put32(superblock, image.InodeCount);
        Put32(superblock, image.Time);
        Put32(superblock, BlockSize);
        Put32(superblock, 0);
        Put16(superblock, CompressionZlib);
        Put16(superblock, BlockLog);
        Put16(superblock, Flags);
        Put16(superblock, 1);
        Put16(superblock, VersionMajor);
        Put16(superblock, 0);
        Put64(superblock, (ulong)root.InodeReference);
        Put64(superblock, (ulong)used);
        Put64(superblock, (ulong)idTable);
        Put64(superblock, ulong.MaxValue);
        Put64(superblock, (ulong)inodeTable);
        Put64(superblock, (ulong)directoryTable);
        Put64(superblock, ulong.MaxValue);
        Put64(superblock, ulong.MaxValue);
        RandomAccess.Write(image.Handle, superblock.WrittenSpan, image.Start);
    }

    /// <summary>Numbers inodes in pre-order; the inodes themselves are written after their children.</summary>
    /// <param name="node">The node.</param>
    /// <param name="image">The image being written.</param>
    private static void Number(Node node, Image image)
    {
        node.InodeNumber = ++image.InodeCount;
        foreach (var child in node.Children)
        {
            Number(child, image);
        }
    }

    /// <summary>Writes a node and everything below it.</summary>
    /// <param name="image">The image being written.</param>
    /// <param name="node">The node.</param>
    /// <param name="parentInode">The parent's inode number.</param>
    private static void WriteNode(Image image, Node node, uint parentInode)
    {
        switch (node.Kind)
        {
            case Kind.Directory:
            {
                WriteDirectory(image, node, parentInode);
                break;
            }

            case Kind.File:
            {
                WriteFile(image, node);
                break;
            }

            default:
            {
                var target = Encoding.UTF8.GetBytes(node.Target!);
                BeginInode(image, node, (ushort)Kind.Symlink);
                Put32(image.Inodes, 1);
                Put32(image.Inodes, (uint)target.Length);
                image.Inodes.Write(target);
                break;
            }
        }
    }

    /// <summary>Writes a directory's children, listing and inode.</summary>
    /// <param name="image">The image being written.</param>
    /// <param name="node">The directory.</param>
    /// <param name="parentInode">The parent's inode number.</param>
    private static void WriteDirectory(Image image, Node node, uint parentInode)
    {
        var links = DirectoryLinkBase;
        foreach (var child in node.Children)
        {
            WriteNode(image, child, node.InodeNumber);
            links += child.Kind == Kind.Directory ? 1U : 0U;
        }

        var listingStart = image.Directories.WrittenCount;
        for (var position = 0; position < node.Children.Count;)
        {
            position += WriteListingGroup(image.Directories, node.Children, position);
        }

        // A basic inode holds the listing size in 16 bits, an extended one in 32.
        var size = image.Directories.WrittenCount - listingStart + DirectorySizeBias;
        var inodes = image.Inodes;
        if (size <= ushort.MaxValue)
        {
            BeginInode(image, node, (ushort)Kind.Directory);
            Put32(inodes, (uint)(listingStart / MetadataSize * MetadataStride));
            Put32(inodes, links);
            Put16(inodes, (ushort)size);
            Put16(inodes, (ushort)(listingStart % MetadataSize));
            Put32(inodes, parentInode);
            return;
        }

        // No lookup index; readers scan the listing.
        BeginInode(image, node, InodeExtendedDirectory);
        Put32(inodes, links);
        Put32(inodes, (uint)size);
        Put32(inodes, (uint)(listingStart / MetadataSize * MetadataStride));
        Put32(inodes, parentInode);
        Put16(inodes, 0);
        Put16(inodes, (ushort)(listingStart % MetadataSize));
        Put32(inodes, None);
    }

    /// <summary>Writes a file's data blocks and inode.</summary>
    /// <param name="image">The image being written.</param>
    /// <param name="node">The file.</param>
    /// <exception cref="InvalidDataException">The file is larger than a basic file inode can describe.</exception>
    /// <exception cref="EndOfStreamException">The file shrank while it was read.</exception>
    private static void WriteFile(Image image, Node node)
    {
        using var input = File.OpenHandle(node.Source!, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        var length = RandomAccess.GetLength(input);
        if (length > uint.MaxValue)
        {
            throw new InvalidDataException($"{node.Source} is larger than 4 GiB.");
        }

        var start = length == 0 ? 0 : image.Position;
        List<uint> sizes = [];
        for (long offset = 0; offset < length; offset += BlockSize)
        {
            var block = image.Block.AsSpan(0, (int)Math.Min(BlockSize, length - offset));
            for (var read = 0; read < block.Length;)
            {
                var count = RandomAccess.Read(input, block[read..], offset + read);
                read += count > 0 ? count : throw new EndOfStreamException($"{node.Source} shrank while it was read.");
            }

            sizes.Add(WriteBlock(image, block));
        }

        BeginInode(image, node, (ushort)Kind.File);
        var inodes = image.Inodes;
        Put32(inodes, (uint)start);
        Put32(inodes, None);
        Put32(inodes, 0);
        Put32(inodes, (uint)length);
        foreach (var size in sizes)
        {
            Put32(inodes, size);
        }
    }

    /// <summary>Writes one data block, compressed when that is smaller.</summary>
    /// <param name="image">The image being written.</param>
    /// <param name="block">The block.</param>
    /// <returns>The block's size entry for the file inode.</returns>
    private static uint WriteBlock(Image image, ReadOnlySpan<byte> block)
    {
        if (ZLibEncoder.TryCompress(block, image.Scratch, out var written, ZlibLevel) && written < block.Length)
        {
            image.Append(image.Scratch.AsSpan(0, written));
            return (uint)written;
        }

        image.Append(block);
        return (uint)block.Length | DataUncompressed;
    }

    /// <summary>Writes one directory header and its entries: inodes in one metadata block, within 32767 inode numbers.</summary>
    /// <param name="table">The directory table.</param>
    /// <param name="children">The sorted children.</param>
    /// <param name="position">The first child in the group.</param>
    /// <returns>The number of children written.</returns>
    private static int WriteListingGroup(ArrayBufferWriter<byte> table, List<Node> children, int position)
    {
        var first = children[position];
        var block = (uint)(first.InodeReference >> ReferenceShift);
        var count = 1;
        while (position + count < children.Count && count < MaxHeaderEntries)
        {
            var next = children[position + count];
            if ((uint)(next.InodeReference >> ReferenceShift) != block || Math.Abs((long)next.InodeNumber - first.InodeNumber) > short.MaxValue)
            {
                break;
            }

            count++;
        }

        Put32(table, (uint)(count - 1));
        Put32(table, block);
        Put32(table, first.InodeNumber);
        for (var index = position; index < position + count; index++)
        {
            var child = children[index];
            Put16(table, (ushort)(child.InodeReference & ReferenceOffsetMask));
            Put16(table, (ushort)(short)((long)child.InodeNumber - first.InodeNumber));

            // Listings use the basic type, even for an extended directory inode.
            Put16(table, (ushort)child.Kind);
            Put16(table, (ushort)(child.NameBytes.Length - 1));
            table.Write(child.NameBytes);
        }

        return count;
    }

    /// <summary>Records a node's inode reference and writes the common inode header.</summary>
    /// <param name="image">The image being written.</param>
    /// <param name="node">The node.</param>
    /// <param name="type">The inode type.</param>
    private static void BeginInode(Image image, Node node, ushort type)
    {
        long position = image.Inodes.WrittenCount;
        node.InodeReference = ((position / MetadataSize * MetadataStride) << ReferenceShift) | (position % MetadataSize);
        Put16(image.Inodes, type);
        Put16(image.Inodes, (ushort)node.Mode);
        Put16(image.Inodes, 0);
        Put16(image.Inodes, 0);
        Put32(image.Inodes, image.Time);
        Put32(image.Inodes, node.InodeNumber);
    }

    /// <summary>Writes a table as uncompressed metadata blocks.</summary>
    /// <param name="image">The image being written.</param>
    /// <param name="table">The table bytes.</param>
    private static void WriteMetadata(Image image, ReadOnlySpan<byte> table)
    {
        Span<byte> header = stackalloc byte[sizeof(ushort)];
        for (var offset = 0; offset < table.Length; offset += MetadataSize)
        {
            var size = Math.Min(MetadataSize, table.Length - offset);
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(MetadataUncompressed | size));
            image.Append(header);
            image.Append(table.Slice(offset, size));
        }
    }

    /// <summary>Appends a little-endian 16-bit value.</summary>
    /// <param name="table">The destination.</param>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put16(ArrayBufferWriter<byte> table, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(table.GetSpan(sizeof(ushort)), value);
        table.Advance(sizeof(ushort));
    }

    /// <summary>Appends a little-endian 32-bit value.</summary>
    /// <param name="table">The destination.</param>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put32(ArrayBufferWriter<byte> table, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(table.GetSpan(sizeof(uint)), value);
        table.Advance(sizeof(uint));
    }

    /// <summary>Appends a little-endian 64-bit value.</summary>
    /// <param name="table">The destination.</param>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put64(ArrayBufferWriter<byte> table, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(table.GetSpan(sizeof(ulong)), value);
        table.Advance(sizeof(ulong));
    }

    /// <summary>A directory, file or symlink in the image.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="kind">The entry kind.</param>
    /// <param name="mode">The permission bits.</param>
    /// <param name="source">The path on disk.</param>
    /// <param name="target">The symlink target.</param>
    internal sealed class Node(string name, Kind kind, int mode, string? source, string? target)
    {
        /// <summary>Gets the UTF-8 entry name.</summary>
        public byte[] NameBytes { get; } = Encoding.UTF8.GetBytes(name);

        /// <summary>Gets the entry kind.</summary>
        public Kind Kind { get; } = kind;

        /// <summary>Gets the permission bits.</summary>
        public int Mode { get; } = mode;

        /// <summary>Gets the path on disk.</summary>
        public string? Source { get; } = source;

        /// <summary>Gets the symlink target.</summary>
        public string? Target { get; } = target;

        /// <summary>Gets the children.</summary>
        public List<Node> Children { get; } = [];

        /// <summary>Gets or sets the inode number.</summary>
        public uint InodeNumber { get; set; }

        /// <summary>Gets or sets the inode reference: the metadata block offset shifted left 16, plus the offset inside the block.</summary>
        public long InodeReference { get; set; }
    }

    /// <summary>An image being written at tracked offsets of a file.</summary>
    /// <param name="handle">The AppImage file.</param>
    /// <param name="start">The file offset where the image starts.</param>
    /// <param name="time">The inode time.</param>
    private sealed class Image(SafeFileHandle handle, long start, uint time)
    {
        /// <summary>Gets the AppImage file.</summary>
        public SafeFileHandle Handle { get; } = handle;

        /// <summary>Gets the file offset where the image starts.</summary>
        public long Start { get; } = start;

        /// <summary>Gets the inode time.</summary>
        public uint Time { get; } = time;

        /// <summary>Gets or sets the file offset of the next write.</summary>
        public long Offset { get; set; } = start;

        /// <summary>Gets the inode table.</summary>
        public ArrayBufferWriter<byte> Inodes { get; } = new();

        /// <summary>Gets the directory table.</summary>
        public ArrayBufferWriter<byte> Directories { get; } = new();

        /// <summary>Gets the read buffer of one data block.</summary>
        public byte[] Block { get; } = new byte[BlockSize];

        /// <summary>Gets the compression buffer, large enough for the worst case of one block.</summary>
        public byte[] Scratch { get; } = new byte[ZLibEncoder.GetMaxCompressedLength(BlockSize)];

        /// <summary>Gets or sets the number of inodes.</summary>
        public uint InodeCount { get; set; }

        /// <summary>Gets the image length so far.</summary>
        public long Position => Offset - Start;

        /// <summary>Writes bytes at the next offset.</summary>
        /// <param name="data">The bytes.</param>
        public void Append(ReadOnlySpan<byte> data)
        {
            RandomAccess.Write(Handle, data, Offset);
            Offset += data.Length;
        }
    }
}
