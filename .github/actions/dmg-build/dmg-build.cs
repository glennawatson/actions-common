// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:package System.IO.Hashing

using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.IO.Enumeration;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static System.Buffers.Binary.BinaryPrimitives;
using static System.Environment;

if (args is not [var work, var source, var volumeName, var output, var applicationsLink])
{
    Console.WriteLine("::error::Expected the working folder, source, volume name, output and applications-link arguments.");
    return Program.UsageError;
}

source = Path.GetFullPath(source, work);

output = Path.GetFullPath(output, work);

if (!Directory.Exists(source))
{
    Console.WriteLine($"::error::source folder not found: {source}");
    return 1;
}

var executables = (GetEnvironmentVariable("DMG_EXECUTABLES") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var items = Program.Collect(source, volumeName, applicationsLink is "true", executables);

var image = Program.BuildVolume(items, Program.VolumeDate(TimeProvider.System));

_ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);

var length = Program.WriteImage(image, output);

Console.WriteLine($"wrote {output}: {items.Count - 1} entries, {image.Length / Program.Mebibyte} MiB volume, {length / Program.Kibibyte} KiB image");

return 0;

/// <summary>Builds a compressed (UDZO) disk image holding a case-sensitive HFS Plus (HFSX) volume, laid out as in Apple's TN1150.</summary>
internal static partial class Program
{
    /// <summary>The exit code for bad arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>Bytes in a kibibyte, for the summary line.</summary>
    internal const int Kibibyte = 1024;

    /// <summary>Bytes in a mebibyte, for the summary line.</summary>
    internal const int Mebibyte = 1024 * 1024;

    /// <summary>The catalog node id of the root folder.</summary>
    private const uint RootId = 2;

    /// <summary>The parent id recorded for the root folder.</summary>
    private const uint RootParentId = 1;

    /// <summary>The first catalog node id for user files and folders.</summary>
    private const uint FirstUserId = 16;

    /// <summary>Seconds from the HFS epoch (1904) to the Unix epoch (1970).</summary>
    private const long MacEpochOffset = 2_082_844_800;

    /// <summary>The allocation block size.</summary>
    private const int BlockSize = 4096;

    /// <summary>The B-tree node size.</summary>
    private const int NodeSize = 4096;

    /// <summary>The owner and group id meaning "unknown", which macOS maps to the user viewing the volume.</summary>
    private const uint UnknownOwner = 99;

    /// <summary>The mode for directories and executables: 0755.</summary>
    private const int ExecutableMode = 0x1ED;

    /// <summary>The mode for other files: 0644.</summary>
    private const int RegularMode = 0x1A4;

    /// <summary>The permission bits of a mode.</summary>
    private const int PermissionMask = 0xFFF;

    /// <summary>The longest HFS Plus name, in UTF-16 units.</summary>
    private const int MaxNameLength = 255;

    /// <summary>Bits in a byte, for allocation and node maps.</summary>
    private const int BitsPerByte = 8;

    /// <summary>The map bit for the first block or node of a byte.</summary>
    private const int HighBit = 0x80;

    /// <summary>Blocks that hold the boot block and volume header, and the extents tree.</summary>
    private const int HeaderAndExtentsBlocks = 2;

    /// <summary>Blocks known before the bitmap is sized: header, one bitmap block and extents tree.</summary>
    private const int FixedBlocks = 3;

    /// <summary>Free blocks added to every volume.</summary>
    private const int SpareBlocks = 256;

    /// <summary>One block per this many used blocks is added as free space.</summary>
    private const int SparePercent = 100;

    /// <summary>The volume header offset, after the boot blocks.</summary>
    private const int VolumeHeaderOffset = 1024;

    /// <summary>The volume header length.</summary>
    private const int VolumeHeaderLength = 512;

    /// <summary>The distance from the end of the volume to the alternate volume header.</summary>
    private const int AlternateHeaderFromEnd = 1024;

    /// <summary>The fork data length in a volume header or file record.</summary>
    private const int ForkLength = 80;

    /// <summary>The length of a folder record.</summary>
    private const int FolderRecordLength = 88;

    /// <summary>The length of a file record.</summary>
    private const int FileRecordLength = 248;

    /// <summary>The length of a B-tree node descriptor.</summary>
    private const int NodeDescriptorLength = 14;

    /// <summary>The length of a record offset at the end of a node.</summary>
    private const int OffsetLength = 2;

    /// <summary>The length of the B-tree header record.</summary>
    private const int HeaderRecordLength = 106;

    /// <summary>The length of the B-tree user data record.</summary>
    private const int UserRecordLength = 128;

    /// <summary>The header node bytes not used by the map record.</summary>
    private const int HeaderNodeOverhead = 256;

    /// <summary>The longest catalog key.</summary>
    private const ushort CatalogMaxKeyLength = 516;

    /// <summary>The longest extents key.</summary>
    private const ushort ExtentsMaxKeyLength = 10;

    /// <summary>B-tree attributes: big keys.</summary>
    private const uint BigKeys = 2;

    /// <summary>B-tree attributes: big keys and variable-length index keys.</summary>
    private const uint BigVariableKeys = 6;

    /// <summary>The HFSX key compare type for binary (case-sensitive) names.</summary>
    private const byte BinaryCompare = 0xBC;

    /// <summary>The node kind of a leaf node.</summary>
    private const sbyte LeafNode = -1;

    /// <summary>The node kind of an index node.</summary>
    private const sbyte IndexNode = 0;

    /// <summary>The node kind of the header node.</summary>
    private const sbyte HeaderNode = 1;

    /// <summary>The bytes in a sector.</summary>
    private const int SectorSize = 512;

    /// <summary>The sectors in each compressed chunk.</summary>
    private const int ChunkSectors = 512;

    /// <summary>The length of a chunk entry in the mish table.</summary>
    private const int ChunkLength = 40;

    /// <summary>The mish offset of the chunk table.</summary>
    private const int MishChunksOffset = 204;

    /// <summary>The length of the koly trailer.</summary>
    private const int KolyLength = 512;

    /// <summary>The zlib level used for chunks.</summary>
    private const int BestCompression = 9;

    /// <summary>A chunk of zeros, stored without data.</summary>
    private const uint ZeroChunk = 2;

    /// <summary>A chunk stored as is.</summary>
    private const uint RawChunk = 1;

    /// <summary>A zlib compressed chunk.</summary>
    private const uint ZlibChunk = 0x80000005;

    /// <summary>The chunk that ends the table.</summary>
    private const uint TerminatorChunk = 0xFFFFFFFF;

    /// <summary>Lists every entry, hidden ones included, as the image must hold the whole folder.</summary>
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    /// <summary>Catalog entry kinds.</summary>
    internal enum Kind
    {
        /// <summary>A folder.</summary>
        Folder = 1,

        /// <summary>A regular file.</summary>
        File = 2,

        /// <summary>A symbolic link.</summary>
        Link = 3,
    }

    /// <summary>Gets the volume date in HFS time: SOURCE_DATE_EPOCH when set, otherwise now.</summary>
    /// <param name="clock">The clock used without SOURCE_DATE_EPOCH.</param>
    /// <returns>Seconds since 1904.</returns>
    internal static uint VolumeDate(TimeProvider clock)
    {
        var stamp = long.TryParse(GetEnvironmentVariable("SOURCE_DATE_EPOCH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            ? epoch
            : clock.GetUtcNow().ToUnixTimeSeconds();
        return (uint)(stamp + MacEpochOffset);
    }

    /// <summary>Lists the volume's entries: the root, the source folder's tree and the optional Applications link.</summary>
    /// <param name="source">The source folder.</param>
    /// <param name="volumeName">The volume name.</param>
    /// <param name="applicationsLink">Whether to add an Applications link.</param>
    /// <param name="executables">Paths or globs that are executable on Windows.</param>
    /// <returns>The entries, root first.</returns>
    internal static List<Item> Collect(string source, string volumeName, bool applicationsLink, string[] executables)
    {
        var root = new Item(RootId, RootParentId, volumeName, Kind.Folder, ExecutableMode, null, null);
        List<Item> items = [root];
        Walk(source, new(source), root, items, executables);
        if (applicationsLink)
        {
            _ = Add(items, root, new("Applications", Kind.Link, ExecutableMode, null, "/Applications"u8.ToArray()));
        }

        return items;
    }

    /// <summary>Lays out the volume: boot block and header, bitmap, extents tree, catalog tree, file data, free space, alternate header.</summary>
    /// <param name="items">The entries.</param>
    /// <param name="date">The volume date in HFS time.</param>
    /// <returns>The volume image.</returns>
    internal static byte[] BuildVolume(List<Item> items, uint date)
    {
        var extents = Tree([], ExtentsMaxKeyLength, BigKeys, 0);
        var catalogLength = Catalog(items, date).Length;
        var layout = Plan(items, catalogLength);
        var image = new byte[layout.Total * BlockSize];
        extents.CopyTo(image.AsSpan((int)layout.ExtentsStart * BlockSize));
        Catalog(items, date).CopyTo(image.AsSpan((int)layout.CatalogStart * BlockSize));
        CopyData(image, items);
        MarkUsed(image.AsSpan(BlockSize, (int)layout.BitmapBlocks * BlockSize), layout);
        var header = image.AsSpan(VolumeHeaderOffset, VolumeHeaderLength);
        VolumeHeader(header, items, date, layout, (extents.Length, catalogLength));
        header.CopyTo(image.AsSpan(image.Length - AlternateHeaderFromEnd));
        return image;
    }

    /// <summary>Writes the UDIF image: zlib chunks, the blkx property list and the koly trailer.</summary>
    /// <param name="image">The volume image.</param>
    /// <param name="path">The .dmg path.</param>
    /// <returns>The image file length.</returns>
    internal static long WriteImage(byte[] image, string path)
    {
        var sectors = image.Length / SectorSize;
        var chunkCount = ((sectors + ChunkSectors - 1) / ChunkSectors) + 1;
        var mish = new byte[MishChunksOffset + (ChunkLength * chunkCount)];
        var dataCrc = new Crc32();
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write);
        var dataLength = WriteChunks(handle, image, mish, dataCrc);
        var imageCrc = Crc32.HashToUInt32(image);
        Mish(mish, sectors, chunkCount, imageCrc);
        var xml = Plist(mish);
        RandomAccess.Write(handle, xml, dataLength);
        Span<byte> koly = stackalloc byte[KolyLength];
        Koly(koly, dataLength, xml.Length, (dataCrc.GetCurrentHashAsUInt32(), imageCrc), sectors);
        RandomAccess.Write(handle, koly, dataLength + xml.Length);
        return dataLength + xml.Length + KolyLength;
    }

    /// <summary>Adds the source folder's entries below a parent, keeping symbolic links as links.</summary>
    /// <param name="source">The source folder, for relative paths.</param>
    /// <param name="directory">The folder to walk.</param>
    /// <param name="parent">The folder's entry.</param>
    /// <param name="items">Receives the entries.</param>
    /// <param name="executables">Paths or globs that are executable on Windows.</param>
    private static void Walk(string source, DirectoryInfo directory, Item parent, List<Item> items, string[] executables)
    {
        var entries = directory.GetFileSystemInfos("*", AllEntries);
        Array.Sort(entries, static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        foreach (var info in entries)
        {
            var relative = Path.GetRelativePath(source, info.FullName).Replace('\\', '/');
            if (info.LinkTarget is { } target)
            {
                _ = Add(items, parent, new(info.Name, Kind.Link, Mode(info, relative, false, executables), null, Encoding.UTF8.GetBytes(target)));
                continue;
            }

            if (info is DirectoryInfo folder)
            {
                Walk(source, folder, Add(items, parent, new(info.Name, Kind.Folder, Mode(info, relative, true, executables), null, null)), items, executables);
                continue;
            }

            _ = Add(items, parent, new(info.Name, Kind.File, Mode(info, relative, false, executables), info.FullName, null));
        }
    }

    /// <summary>Gets the permission bits. Windows has none, so directories and listed executables get 0755 and the rest 0644.</summary>
    /// <param name="info">The entry.</param>
    /// <param name="relative">The path within the source, with forward slashes.</param>
    /// <param name="folder">Whether the entry is a folder.</param>
    /// <param name="executables">Paths or globs that are executable on Windows.</param>
    /// <returns>The mode.</returns>
    private static int Mode(FileSystemInfo info, string relative, bool folder, string[] executables)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (int)info.UnixFileMode;
        }

        if (folder)
        {
            return ExecutableMode;
        }

        foreach (var pattern in executables)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern.TrimStart('/'), relative, ignoreCase: false))
            {
                return ExecutableMode;
            }
        }

        return RegularMode;
    }

    /// <summary>Adds an entry below a parent, giving it the next catalog node id.</summary>
    /// <param name="items">The entries.</param>
    /// <param name="parent">The parent folder.</param>
    /// <param name="entry">The entry's name, kind, mode and content.</param>
    /// <returns>The added entry.</returns>
    private static Item Add(List<Item> items, Item parent, in Entry entry)
    {
        var item = new Item((uint)items.Count + (FirstUserId - 1), parent.Id, entry.Name, entry.Kind, entry.Mode, entry.Path, entry.Link);
        items.Add(item);
        parent.Valence++;
        if (item.Kind is Kind.Folder)
        {
            parent.Folders++;
        }

        return item;
    }

    /// <summary>Places the bitmap, catalog and file data, and sizes the volume.</summary>
    /// <param name="items">The entries; receives each data start block.</param>
    /// <param name="catalogLength">The catalog file length.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="InvalidOperationException">The volume would be larger than 2 GiB.</exception>
    private static Layout Plan(List<Item> items, int catalogLength)
    {
        long dataBlocks = 0;
        foreach (var item in items)
        {
            dataBlocks += Blocks(item.Size);
        }

        var catalogBlocks = catalogLength / BlockSize;
        var used = FixedBlocks + catalogBlocks + dataBlocks;
        var total = used + SpareBlocks + (used / SparePercent);
        var bitmapBlocks = 1L;
        while (bitmapBlocks * BlockSize * BitsPerByte < total + bitmapBlocks)
        {
            bitmapBlocks++;
        }

        total += bitmapBlocks;
        if (total * BlockSize > Array.MaxLength)
        {
            throw new InvalidOperationException("The volume would be larger than 2 GiB.");
        }

        var catalogStart = HeaderAndExtentsBlocks + bitmapBlocks;
        var next = catalogStart + catalogBlocks;
        foreach (var item in items)
        {
            if (item.Size <= 0)
            {
                continue;
            }

            item.Start = (uint)next;
            next += Blocks(item.Size);
        }

        return new(bitmapBlocks, total, catalogStart, next);
    }

    /// <summary>Copies each file's data and each link's target into its blocks.</summary>
    /// <param name="image">The volume image.</param>
    /// <param name="items">The entries.</param>
    private static void CopyData(byte[] image, List<Item> items)
    {
        foreach (var item in items)
        {
            if (item.Size <= 0)
            {
                continue;
            }

            var data = image.AsSpan((int)item.Start * BlockSize, (int)item.Size);
            if (item.Link is { } link)
            {
                link.CopyTo(data);
                continue;
            }

            using var file = new FileStream(item.Path!, new FileStreamOptions { Options = FileOptions.SequentialScan, BufferSize = 0 });
            file.ReadExactly(data);
        }
    }

    /// <summary>Marks the used blocks and the alternate header block in the allocation bitmap.</summary>
    /// <param name="bitmap">The allocation bitmap.</param>
    /// <param name="layout">The layout.</param>
    private static void MarkUsed(Span<byte> bitmap, in Layout layout)
    {
        for (var block = 0L; block < layout.Total; block++)
        {
            if (block >= layout.Next && block != layout.Total - 1)
            {
                continue;
            }

            bitmap[(int)(block / BitsPerByte)] |= (byte)(HighBit >> (int)(block % BitsPerByte));
        }
    }

    /// <summary>Writes the volume header fields at their TN1150 offsets.</summary>
    /// <param name="header">The volume header.</param>
    /// <param name="items">The entries.</param>
    /// <param name="date">The volume date in HFS time.</param>
    /// <param name="layout">The layout.</param>
    /// <param name="lengths">The extents and catalog file lengths.</param>
    private static void VolumeHeader(Span<byte> header, List<Item> items, uint date, in Layout layout, (int Extents, int Catalog) lengths)
    {
        const ushort HfsxSignature = 0x4858;
        const ushort HfsxVersion = 5;
        const uint Unmounted = 0x100;
        const uint MountedBy = 0x31302E30;
        const int ClumpSize = 0x10000;
        var folders = 0;
        foreach (var item in items)
        {
            folders += item.Kind is Kind.Folder ? 1 : 0;
        }

        WriteUInt16BigEndian(header, HfsxSignature);
        WriteUInt16BigEndian(header[VolumeOffsets.Version..], HfsxVersion);
        WriteUInt32BigEndian(header[VolumeOffsets.Attributes..], Unmounted);
        WriteUInt32BigEndian(header[VolumeOffsets.LastMountedVersion..], MountedBy);
        WriteUInt32BigEndian(header[VolumeOffsets.CreateDate..], date);
        WriteUInt32BigEndian(header[VolumeOffsets.ModifyDate..], date);
        WriteUInt32BigEndian(header[VolumeOffsets.CheckedDate..], date);
        WriteInt32BigEndian(header[VolumeOffsets.FileCount..], items.Count - folders);
        WriteInt32BigEndian(header[VolumeOffsets.FolderCount..], folders - 1);
        WriteInt32BigEndian(header[VolumeOffsets.AllocationBlockSize..], BlockSize);
        WriteUInt32BigEndian(header[VolumeOffsets.TotalBlocks..], (uint)layout.Total);
        WriteUInt32BigEndian(header[VolumeOffsets.FreeBlocks..], (uint)(layout.Total - layout.Next - 1));
        WriteUInt32BigEndian(header[VolumeOffsets.NextAllocation..], (uint)layout.Next);
        WriteInt32BigEndian(header[VolumeOffsets.ResourceClumpSize..], ClumpSize);
        WriteInt32BigEndian(header[VolumeOffsets.DataClumpSize..], ClumpSize);
        WriteUInt32BigEndian(header[VolumeOffsets.NextCatalogId..], items[^1].Id + 1);
        WriteUInt32BigEndian(header[VolumeOffsets.WriteCount..], 1);
        WriteUInt64BigEndian(header[VolumeOffsets.EncodingsBitmap..], 1);
        RandomNumberGenerator.Fill(header.Slice(VolumeOffsets.VolumeId, sizeof(ulong)));
        Fork(header[VolumeOffsets.AllocationFile..], layout.BitmapBlocks * BlockSize, 1, layout.BitmapBlocks);
        Fork(header[VolumeOffsets.ExtentsFile..], lengths.Extents, layout.ExtentsStart, 1);
        Fork(header[VolumeOffsets.CatalogFile..], lengths.Catalog, layout.CatalogStart, lengths.Catalog / BlockSize);
    }

    /// <summary>Gets the blocks a size needs.</summary>
    /// <param name="size">The size in bytes.</param>
    /// <returns>The block count.</returns>
    private static long Blocks(long size) => (size + BlockSize - 1) / BlockSize;

    /// <summary>Writes fork data: logical size, clump size, total blocks and a single extent.</summary>
    /// <param name="fork">The fork data.</param>
    /// <param name="size">The logical size.</param>
    /// <param name="start">The first block.</param>
    /// <param name="blocks">The block count.</param>
    private static void Fork(Span<byte> fork, long size, long start, long blocks)
    {
        const int ClumpSizeOffset = 8;
        const int TotalBlocksOffset = 12;
        const int ExtentStartOffset = 16;
        const int ExtentCountOffset = 20;
        WriteInt64BigEndian(fork, size);
        WriteUInt32BigEndian(fork[ClumpSizeOffset..], (uint)(blocks * BlockSize));
        WriteUInt32BigEndian(fork[TotalBlocksOffset..], (uint)blocks);
        if (blocks <= 0)
        {
            return;
        }

        WriteUInt32BigEndian(fork[ExtentStartOffset..], (uint)start);
        WriteUInt32BigEndian(fork[ExtentCountOffset..], (uint)blocks);
    }

    /// <summary>Builds the catalog file: a record and a thread for every folder, file and link, sorted by parent and binary name.</summary>
    /// <param name="items">The entries.</param>
    /// <param name="date">The volume date in HFS time.</param>
    /// <returns>The catalog B-tree.</returns>
    /// <exception cref="InvalidOperationException">Two entries in a folder share a name.</exception>
    private static byte[] Catalog(List<Item> items, uint date)
    {
        List<CatalogRecord> records = [];
        foreach (var item in items)
        {
            records.Add(new(item.Parent, item.Name, Key(item.Parent, item.Name), Record(item, date)));
            records.Add(new(item.Id, string.Empty, Key(item.Id, string.Empty), Thread(item)));
        }

        records.Sort(static (left, right) => left.Parent != right.Parent ? left.Parent.CompareTo(right.Parent) : string.CompareOrdinal(left.Name, right.Name));
        List<TreeRecord> sorted = [with(records.Count)];
        for (var i = 0; i < records.Count; i++)
        {
            if (i > 0 && records[i].Parent == records[i - 1].Parent && string.Equals(records[i].Name, records[i - 1].Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Two entries share the name '{records[i].Name}' after Unicode decomposition.");
            }

            sorted.Add(new(records[i].Key, records[i].Data));
        }

        return Tree(sorted, CatalogMaxKeyLength, BigVariableKeys, BinaryCompare);
    }

    /// <summary>Builds a folder or file record: ids, dates, BSD owner and mode, and the data fork for files and links.</summary>
    /// <param name="item">The entry.</param>
    /// <param name="date">The volume date in HFS time.</param>
    /// <returns>The record.</returns>
    private static byte[] Record(Item item, uint date)
    {
        const short FolderRecord = 1;
        const short FileRecord = 2;
        const ushort HasFolderCount = 0x10;
        const ushort ThreadExists = 0x2;
        const int FolderType = 0x4000;
        const int FileType = 0x8000;
        const int LinkType = 0xA000;
        var folder = item.Kind is Kind.Folder;
        var record = new byte[folder ? FolderRecordLength : FileRecordLength];
        WriteInt16BigEndian(record, folder ? FolderRecord : FileRecord);
        WriteUInt16BigEndian(record.AsSpan(RecordOffsets.Flags), folder ? HasFolderCount : ThreadExists);
        WriteUInt32BigEndian(record.AsSpan(RecordOffsets.Valence), item.Valence);
        WriteUInt32BigEndian(record.AsSpan(RecordOffsets.Id), item.Id);
        WriteUInt32BigEndian(record.AsSpan(RecordOffsets.CreateDate), date);
        WriteUInt32BigEndian(record.AsSpan(RecordOffsets.ContentModifyDate), date);
        WriteUInt32BigEndian(record.AsSpan(RecordOffsets.AttributeModifyDate), date);
        WriteUInt32BigEndian(record.AsSpan(RecordOffsets.AccessDate), date);

        // BSD info: owner, group, then the mode with its file type bits.
        WriteUInt32BigEndian(record.AsSpan(RecordOffsets.Owner), UnknownOwner);
        WriteUInt32BigEndian(record.AsSpan(RecordOffsets.Group), UnknownOwner);
        var type = item.Kind switch
        {
            Kind.Folder => FolderType,
            Kind.File => FileType,
            _ => LinkType,
        };
        WriteUInt16BigEndian(record.AsSpan(RecordOffsets.Mode), (ushort)((item.Mode & PermissionMask) | type));
        if (folder)
        {
            WriteUInt32BigEndian(record.AsSpan(RecordOffsets.FolderCount), item.Folders);
            return record;
        }

        if (item.Kind is Kind.Link)
        {
            "slnkrhap"u8.CopyTo(record.AsSpan(RecordOffsets.FinderInfo));
        }

        Fork(record.AsSpan(RecordOffsets.DataFork, ForkLength), item.Size, item.Start, Blocks(item.Size));
        return record;
    }

    /// <summary>Builds a thread record, which finds an entry's parent and name from its id.</summary>
    /// <param name="item">The entry.</param>
    /// <returns>The thread record.</returns>
    private static byte[] Thread(Item item)
    {
        const short FolderThread = 3;
        const short FileThread = 4;
        const int ParentOffset = 4;
        const int NameOffset = 8;
        var thread = new byte[NameOffset + sizeof(ushort) + (sizeof(char) * item.Name.Length)];
        WriteInt16BigEndian(thread, item.Kind is Kind.Folder ? FolderThread : FileThread);
        WriteUInt32BigEndian(thread.AsSpan(ParentOffset), item.Parent);
        Name(thread.AsSpan(NameOffset), item.Name);
        return thread;
    }

    /// <summary>Builds a catalog key: key length, parent id and name.</summary>
    /// <param name="parent">The parent id.</param>
    /// <param name="name">The name.</param>
    /// <returns>The key.</returns>
    private static byte[] Key(uint parent, string name)
    {
        const int ParentOffset = 2;
        const int NameOffset = 6;
        var key = new byte[NameOffset + sizeof(ushort) + (sizeof(char) * name.Length)];
        WriteUInt16BigEndian(key, (ushort)(key.Length - sizeof(ushort)));
        WriteUInt32BigEndian(key.AsSpan(ParentOffset), parent);
        Name(key.AsSpan(NameOffset), name);
        return key;
    }

    /// <summary>Writes an HFS Plus Unicode name: its length, then big-endian UTF-16 units.</summary>
    /// <param name="target">Receives the name.</param>
    /// <param name="name">The name.</param>
    private static void Name(Span<byte> target, string name)
    {
        WriteUInt16BigEndian(target, (ushort)name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            WriteUInt16BigEndian(target[(sizeof(ushort) + (sizeof(char) * i))..], name[i]);
        }
    }

    /// <summary>Builds a B-tree file: header node, packed leaf nodes, then index levels up to a single root.</summary>
    /// <param name="records">The leaf records, sorted.</param>
    /// <param name="maxKeyLength">The longest key.</param>
    /// <param name="attributes">The tree attributes.</param>
    /// <param name="compareType">The key compare type.</param>
    /// <returns>The tree file.</returns>
    /// <exception cref="InvalidOperationException">The tree needs more nodes than one header map holds.</exception>
    private static byte[] Tree(List<TreeRecord> records, ushort maxKeyLength, uint attributes, byte compareType)
    {
        var levels = Levels(records);
        var totalNodes = 1;
        foreach (var level in levels)
        {
            totalNodes += level.Count;
        }

        if (totalNodes > (NodeSize - HeaderNodeOverhead) * BitsPerByte)
        {
            throw new InvalidOperationException("The catalog needs more nodes than one header map holds.");
        }

        var tree = new byte[totalNodes * NodeSize];
        var root = WriteLevels(tree, levels);
        var header = new byte[HeaderRecordLength];
        WriteUInt16BigEndian(header, (ushort)levels.Count);
        WriteInt32BigEndian(header.AsSpan(TreeHeaderOffsets.RootNode), root);
        WriteInt32BigEndian(header.AsSpan(TreeHeaderOffsets.LeafRecords), records.Count);
        WriteInt32BigEndian(header.AsSpan(TreeHeaderOffsets.FirstLeaf), levels.Count is 0 ? 0 : 1);
        WriteInt32BigEndian(header.AsSpan(TreeHeaderOffsets.LastLeaf), levels.Count is 0 ? 0 : levels[0].Count);
        WriteUInt16BigEndian(header.AsSpan(TreeHeaderOffsets.TreeNodeSize), NodeSize);
        WriteUInt16BigEndian(header.AsSpan(TreeHeaderOffsets.MaxKeyLength), maxKeyLength);
        WriteInt32BigEndian(header.AsSpan(TreeHeaderOffsets.TotalNodes), totalNodes);
        WriteInt32BigEndian(header.AsSpan(TreeHeaderOffsets.ClumpSize), tree.Length);
        header[TreeHeaderOffsets.CompareType] = compareType;
        WriteUInt32BigEndian(header.AsSpan(TreeHeaderOffsets.Attributes), attributes);
        var map = new byte[NodeSize - HeaderNodeOverhead];
        for (var i = 0; i < totalNodes; i++)
        {
            map[i / BitsPerByte] |= (byte)(HighBit >> (i % BitsPerByte));
        }

        Node(tree.AsSpan(0, NodeSize), new(0, 0, HeaderNode, 0), [header, new byte[UserRecordLength], map]);
        return tree;
    }

    /// <summary>Packs records into leaf nodes, then each level's first keys into index nodes, until one node is the root.</summary>
    /// <param name="records">The leaf records.</param>
    /// <returns>The levels, leaves first; empty for an empty tree.</returns>
    private static List<List<List<TreeRecord>>> Levels(List<TreeRecord> records)
    {
        List<List<List<TreeRecord>>> levels = [];
        var first = 1U;
        for (var current = records; current.Count > 0;)
        {
            var nodes = Pack(current);
            levels.Add(nodes);
            if (nodes.Count is 1)
            {
                break;
            }

            // Each index record holds a child's first key and its big-endian node number.
            List<TreeRecord> index = [with(nodes.Count)];
            for (var i = 0; i < nodes.Count; i++)
            {
                var pointer = new byte[sizeof(uint)];
                WriteUInt32BigEndian(pointer, first + (uint)i);
                index.Add(new(nodes[i][0].Key, pointer));
            }

            first += (uint)nodes.Count;
            current = index;
        }

        return levels;
    }

    /// <summary>Fills nodes in order, starting a new node when a record does not fit.</summary>
    /// <param name="records">The records.</param>
    /// <returns>The nodes.</returns>
    private static List<List<TreeRecord>> Pack(List<TreeRecord> records)
    {
        List<List<TreeRecord>> nodes = [];
        var free = 0;
        foreach (var record in records)
        {
            var length = record.Key.Length + record.Data.Length + OffsetLength;
            if (nodes.Count is 0 || length > free)
            {
                nodes.Add([]);
                free = NodeSize - NodeDescriptorLength - OffsetLength;
            }

            nodes[^1].Add(record);
            free -= length;
        }

        return nodes;
    }

    /// <summary>Writes every level's nodes after the header node, linking siblings.</summary>
    /// <param name="tree">The tree file.</param>
    /// <param name="levels">The levels, leaves first.</param>
    /// <returns>The root node number, or zero for an empty tree.</returns>
    private static int WriteLevels(byte[] tree, List<List<List<TreeRecord>>> levels)
    {
        var number = 1;
        for (var level = 0; level < levels.Count; level++)
        {
            var nodes = levels[level];
            for (var i = 0; i < nodes.Count; i++)
            {
                var forward = i + 1 < nodes.Count ? number + 1 : 0;
                var backward = i > 0 ? number - 1 : 0;
                List<byte[]> joined = [with(nodes[i].Count)];
                foreach (var record in nodes[i])
                {
                    joined.Add([.. record.Key, .. record.Data]);
                }

                Node(tree.AsSpan(number * NodeSize, NodeSize), new(forward, backward, level is 0 ? LeafNode : IndexNode, level + 1), joined);
                number++;
            }
        }

        return number - 1;
    }

    /// <summary>Writes a node descriptor, its records from offset 14 and the record offsets backwards from the end.</summary>
    /// <param name="node">The node.</param>
    /// <param name="descriptor">The links, kind and height.</param>
    /// <param name="records">The records.</param>
    private static void Node(Span<byte> node, NodeDescriptor descriptor, List<byte[]> records)
    {
        const int BackwardOffset = 4;
        const int KindOffset = 8;
        const int HeightOffset = 9;
        const int CountOffset = 10;
        WriteInt32BigEndian(node, descriptor.Forward);
        WriteInt32BigEndian(node[BackwardOffset..], descriptor.Backward);
        node[KindOffset] = (byte)descriptor.Kind;
        node[HeightOffset] = (byte)descriptor.Height;
        WriteUInt16BigEndian(node[CountOffset..], (ushort)records.Count);
        var offset = NodeDescriptorLength;
        for (var i = 0; i < records.Count; i++)
        {
            WriteUInt16BigEndian(node[(NodeSize - (OffsetLength * (i + 1)))..], (ushort)offset);
            records[i].CopyTo(node[offset..]);
            offset += records[i].Length;
        }

        // The last offset marks the start of the free space.
        WriteUInt16BigEndian(node[(NodeSize - (OffsetLength * (records.Count + 1)))..], (ushort)offset);
    }

    /// <summary>Writes the data fork: each chunk zlib compressed when that is smaller, stored raw otherwise, and left out when all zero.</summary>
    /// <param name="handle">The .dmg.</param>
    /// <param name="image">The volume image.</param>
    /// <param name="mish">Receives the chunk table.</param>
    /// <param name="dataCrc">Receives the data fork bytes.</param>
    /// <returns>The data fork length.</returns>
    private static long WriteChunks(SafeFileHandle handle, byte[] image, byte[] mish, Crc32 dataCrc)
    {
        var sectors = image.Length / SectorSize;
        var buffer = ArrayPool<byte>.Shared.Rent((int)ZLibEncoder.GetMaxCompressedLength(ChunkSectors * SectorSize));
        try
        {
            using var encoder = new ZLibEncoder(new ZLibCompressionOptions { CompressionLevel = BestCompression });
            long offset = 0;
            var index = 0;
            for (var sector = 0; sector < sectors; sector += ChunkSectors)
            {
                var raw = image.AsSpan(sector * SectorSize, Math.Min(ChunkSectors, sectors - sector) * SectorSize);
                var compressed = raw.ContainsAnyExcept((byte)0) ? Compress(encoder, raw, buffer) : 0;
                var (type, data) = compressed switch
                {
                    0 => (ZeroChunk, ReadOnlyMemory<byte>.Empty),
                    _ when compressed < raw.Length => (ZlibChunk, buffer.AsMemory(0, compressed)),
                    _ => (RawChunk, image.AsMemory(sector * SectorSize, raw.Length)),
                };
                Chunk(mish.AsSpan(MishChunksOffset + (ChunkLength * index)), type, (sector, raw.Length / SectorSize), (offset, data.Length));
                RandomAccess.Write(handle, data.Span, offset);
                dataCrc.Append(data.Span);
                offset += data.Length;
                index++;
            }

            Chunk(mish.AsSpan(MishChunksOffset + (ChunkLength * index)), TerminatorChunk, (sectors, 0), (offset, 0));
            return offset;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Compresses one chunk as a whole zlib stream.</summary>
    /// <param name="encoder">The encoder, reset after each chunk.</param>
    /// <param name="raw">The chunk.</param>
    /// <param name="buffer">Receives the compressed chunk.</param>
    /// <returns>The compressed length.</returns>
    /// <exception cref="InvalidOperationException">The chunk did not compress in one call.</exception>
    private static int Compress(ZLibEncoder encoder, ReadOnlySpan<byte> raw, byte[] buffer)
    {
        var status = encoder.Compress(raw, buffer, out var consumed, out var written, isFinalBlock: true);
        encoder.Reset();
        if (status is not OperationStatus.Done || consumed != raw.Length)
        {
            throw new InvalidOperationException($"A chunk did not compress in one call: {status}.");
        }

        return written;
    }

    /// <summary>Writes a chunk entry: type, sectors and data location.</summary>
    /// <param name="chunk">The entry.</param>
    /// <param name="type">The chunk type.</param>
    /// <param name="sectors">The first sector and sector count.</param>
    /// <param name="data">The data offset and length.</param>
    private static void Chunk(Span<byte> chunk, uint type, (long First, long Count) sectors, (long Offset, long Length) data)
    {
        const int SectorOffset = 8;
        const int CountOffset = 16;
        const int DataOffset = 24;
        const int LengthOffset = 32;
        WriteUInt32BigEndian(chunk, type);
        WriteInt64BigEndian(chunk[SectorOffset..], sectors.First);
        WriteInt64BigEndian(chunk[CountOffset..], sectors.Count);
        WriteInt64BigEndian(chunk[DataOffset..], data.Offset);
        WriteInt64BigEndian(chunk[LengthOffset..], data.Length);
    }

    /// <summary>Writes the mish header: version 1, first sector 0, sector count, buffers needed and the uncompressed data's CRC32.</summary>
    /// <param name="mish">The mish block, chunk table already written.</param>
    /// <param name="sectors">The sector count.</param>
    /// <param name="chunkCount">The chunk count, terminator included.</param>
    /// <param name="imageCrc">The CRC32 of the volume image.</param>
    private static void Mish(Span<byte> mish, long sectors, int chunkCount, uint imageCrc)
    {
        const int VersionOffset = 4;
        const int SectorCountOffset = 16;
        const int BuffersOffset = 32;
        const int ExtraBuffers = 8;
        const int ChecksumOffset = 64;
        const int ChunkCountOffset = 200;
        "mish"u8.CopyTo(mish);
        WriteInt32BigEndian(mish[VersionOffset..], 1);
        WriteInt64BigEndian(mish[SectorCountOffset..], sectors);
        WriteInt32BigEndian(mish[BuffersOffset..], ChunkSectors + ExtraBuffers);
        Checksum(mish[ChecksumOffset..], imageCrc);
        WriteInt32BigEndian(mish[ChunkCountOffset..], chunkCount);
    }

    /// <summary>Builds the property list holding the single blkx entry.</summary>
    /// <param name="mish">The mish block.</param>
    /// <returns>The UTF-8 property list.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] Plist(byte[] mish) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        <key>resource-fork</key>
        <dict>
        <key>blkx</key>
        <array>
        <dict>
        <key>Attributes</key>
        <string>0x0050</string>
        <key>CFName</key>
        <string>whole disk (Apple_HFSX : 0)</string>
        <key>Data</key>
        <data>{Convert.ToBase64String(mish)}</data>
        <key>ID</key>
        <string>-1</string>
        <key>Name</key>
        <string>whole disk (Apple_HFSX : 0)</string>
        </dict>
        </array>
        </dict>
        </dict>
        </plist>

        """);

    /// <summary>Writes the koly trailer: version 4, flattened, data fork CRC32, XML location, master CRC32 over each blkx checksum, sector count.</summary>
    /// <param name="koly">The trailer.</param>
    /// <param name="dataLength">The data fork length.</param>
    /// <param name="xmlLength">The property list length.</param>
    /// <param name="crcs">The data fork and volume image CRC32s.</param>
    /// <param name="sectors">The sector count.</param>
    private static void Koly(Span<byte> koly, long dataLength, int xmlLength, (uint Data, uint Image) crcs, long sectors)
    {
        const int Version = 4;
        const int SegmentIdLength = 16;
        koly.Clear();
        "koly"u8.CopyTo(koly);
        WriteInt32BigEndian(koly[KolyOffsets.Version..], Version);
        WriteInt32BigEndian(koly[KolyOffsets.HeaderSize..], KolyLength);
        WriteInt32BigEndian(koly[KolyOffsets.Flags..], 1);
        WriteInt64BigEndian(koly[KolyOffsets.DataForkLength..], dataLength);
        WriteInt32BigEndian(koly[KolyOffsets.SegmentNumber..], 1);
        WriteInt32BigEndian(koly[KolyOffsets.SegmentCount..], 1);
        RandomNumberGenerator.Fill(koly.Slice(KolyOffsets.SegmentId, SegmentIdLength));
        Checksum(koly[KolyOffsets.DataChecksum..], crcs.Data);
        WriteInt64BigEndian(koly[KolyOffsets.XmlOffset..], dataLength);
        WriteInt64BigEndian(koly[KolyOffsets.XmlLength..], xmlLength);
        Span<byte> master = stackalloc byte[sizeof(uint)];
        WriteUInt32BigEndian(master, crcs.Image);
        Checksum(koly[KolyOffsets.MasterChecksum..], Crc32.HashToUInt32(master));
        WriteInt32BigEndian(koly[KolyOffsets.ImageVariant..], 1);
        WriteInt64BigEndian(koly[KolyOffsets.SectorCount..], sectors);
    }

    /// <summary>Writes a UDIF checksum: type CRC32, 32 bits, then the value.</summary>
    /// <param name="target">The checksum field.</param>
    /// <param name="crc">The CRC32.</param>
    private static void Checksum(Span<byte> target, uint crc)
    {
        const int Crc32Type = 2;
        const int Bits = 32;
        const int ValueOffset = 8;
        WriteInt32BigEndian(target, Crc32Type);
        WriteInt32BigEndian(target[sizeof(int)..], Bits);
        WriteUInt32BigEndian(target[ValueOffset..], crc);
    }

    /// <summary>An entry to add below a folder.</summary>
    /// <param name="Name">The name as on disk.</param>
    /// <param name="Kind">The entry kind.</param>
    /// <param name="Mode">The mode.</param>
    /// <param name="Path">The file to copy, for files.</param>
    /// <param name="Link">The link target, for links.</param>
    internal readonly record struct Entry(string Name, Kind Kind, int Mode, string? Path, byte[]? Link);

    /// <summary>Where the volume's parts go.</summary>
    /// <param name="BitmapBlocks">The allocation bitmap's blocks.</param>
    /// <param name="Total">The volume's blocks.</param>
    /// <param name="CatalogStart">The catalog's first block.</param>
    /// <param name="Next">The first block after the file data.</param>
    private readonly record struct Layout(long BitmapBlocks, long Total, long CatalogStart, long Next)
    {
        /// <summary>Gets the extents tree's block, after the bitmap.</summary>
        public long ExtentsStart => 1 + BitmapBlocks;
    }

    /// <summary>A catalog record with the parent and name it sorts by.</summary>
    /// <param name="Parent">The parent id in the key.</param>
    /// <param name="Name">The name in the key.</param>
    /// <param name="Key">The key.</param>
    /// <param name="Data">The record.</param>
    private readonly record struct CatalogRecord(uint Parent, string Name, byte[] Key, byte[] Data);

    /// <summary>A B-tree record.</summary>
    /// <param name="Key">The key.</param>
    /// <param name="Data">The data.</param>
    private readonly record struct TreeRecord(byte[] Key, byte[] Data);

    /// <summary>A B-tree node descriptor.</summary>
    /// <param name="Forward">The next node at this level.</param>
    /// <param name="Backward">The previous node at this level.</param>
    /// <param name="Kind">The node kind.</param>
    /// <param name="Height">The level, leaves at 1.</param>
    private readonly record struct NodeDescriptor(int Forward, int Backward, sbyte Kind, int Height);

    /// <summary>A catalog entry. Names are stored decomposed, with ':' as '/' as HFS Plus expects.</summary>
    internal sealed class Item
    {
        /// <summary>Initializes a new instance of the <see cref="Item"/> class.</summary>
        /// <param name="id">The catalog node id.</param>
        /// <param name="parent">The parent folder's id.</param>
        /// <param name="name">The name as on disk.</param>
        /// <param name="kind">The entry kind.</param>
        /// <param name="mode">The mode.</param>
        /// <param name="path">The file to copy, for files.</param>
        /// <param name="link">The link target, for links.</param>
        /// <exception cref="InvalidOperationException">The decomposed name is longer than 255 UTF-16 units.</exception>
        internal Item(uint id, uint parent, string name, Kind kind, int mode, string? path, byte[]? link)
        {
            var decomposed = name.Normalize(NormalizationForm.FormD).Replace(':', '/');
            if (decomposed.Length > MaxNameLength)
            {
                throw new InvalidOperationException($"The name '{name}' is longer than 255 UTF-16 units.");
            }

            Id = id;
            Parent = parent;
            Name = decomposed;
            Kind = kind;
            Mode = mode;
            Path = path;
            Link = link;
            Size = link?.Length ?? (path is null ? 0 : new FileInfo(path).Length);
        }

        /// <summary>Gets the catalog node id.</summary>
        public uint Id { get; }

        /// <summary>Gets the parent folder's id.</summary>
        public uint Parent { get; }

        /// <summary>Gets the HFS Plus name.</summary>
        public string Name { get; }

        /// <summary>Gets the entry kind.</summary>
        public Kind Kind { get; }

        /// <summary>Gets the mode.</summary>
        public int Mode { get; }

        /// <summary>Gets the file to copy, for files.</summary>
        public string? Path { get; }

        /// <summary>Gets the link target, for links.</summary>
        public byte[]? Link { get; }

        /// <summary>Gets the data length.</summary>
        public long Size { get; }

        /// <summary>Gets or sets the folder's item count.</summary>
        public uint Valence { get; set; }

        /// <summary>Gets or sets the folder's subfolder count.</summary>
        public uint Folders { get; set; }

        /// <summary>Gets or sets the first data block.</summary>
        public uint Start { get; set; }
    }

    /// <summary>Volume header field offsets from TN1150.</summary>
    private static class VolumeOffsets
    {
        /// <summary>The format version.</summary>
        internal const int Version = 2;

        /// <summary>The volume attributes.</summary>
        internal const int Attributes = 4;

        /// <summary>The version of the last implementation to mount the volume.</summary>
        internal const int LastMountedVersion = 8;

        /// <summary>The creation date.</summary>
        internal const int CreateDate = 16;

        /// <summary>The modification date.</summary>
        internal const int ModifyDate = 20;

        /// <summary>The last check date.</summary>
        internal const int CheckedDate = 28;

        /// <summary>The file count.</summary>
        internal const int FileCount = 32;

        /// <summary>The folder count, root excluded.</summary>
        internal const int FolderCount = 36;

        /// <summary>The allocation block size.</summary>
        internal const int AllocationBlockSize = 40;

        /// <summary>The total block count.</summary>
        internal const int TotalBlocks = 44;

        /// <summary>The free block count.</summary>
        internal const int FreeBlocks = 48;

        /// <summary>The block to start the next allocation search at.</summary>
        internal const int NextAllocation = 52;

        /// <summary>The default resource fork clump size.</summary>
        internal const int ResourceClumpSize = 56;

        /// <summary>The default data fork clump size.</summary>
        internal const int DataClumpSize = 60;

        /// <summary>The next unused catalog node id.</summary>
        internal const int NextCatalogId = 64;

        /// <summary>The write count.</summary>
        internal const int WriteCount = 68;

        /// <summary>The text encodings used in names.</summary>
        internal const int EncodingsBitmap = 72;

        /// <summary>The 64-bit volume id in the Finder info.</summary>
        internal const int VolumeId = 104;

        /// <summary>The allocation file fork.</summary>
        internal const int AllocationFile = 112;

        /// <summary>The extents file fork.</summary>
        internal const int ExtentsFile = 192;

        /// <summary>The catalog file fork.</summary>
        internal const int CatalogFile = 272;
    }

    /// <summary>Catalog folder and file record field offsets from TN1150.</summary>
    private static class RecordOffsets
    {
        /// <summary>The flags.</summary>
        internal const int Flags = 2;

        /// <summary>The folder's item count.</summary>
        internal const int Valence = 4;

        /// <summary>The catalog node id.</summary>
        internal const int Id = 8;

        /// <summary>The creation date.</summary>
        internal const int CreateDate = 12;

        /// <summary>The content modification date.</summary>
        internal const int ContentModifyDate = 16;

        /// <summary>The attribute modification date.</summary>
        internal const int AttributeModifyDate = 20;

        /// <summary>The access date.</summary>
        internal const int AccessDate = 24;

        /// <summary>The BSD owner id.</summary>
        internal const int Owner = 32;

        /// <summary>The BSD group id.</summary>
        internal const int Group = 36;

        /// <summary>The BSD mode.</summary>
        internal const int Mode = 42;

        /// <summary>The Finder info: file type and creator.</summary>
        internal const int FinderInfo = 48;

        /// <summary>The HFSX subfolder count.</summary>
        internal const int FolderCount = 84;

        /// <summary>The data fork.</summary>
        internal const int DataFork = 88;
    }

    /// <summary>B-tree header record field offsets from TN1150.</summary>
    private static class TreeHeaderOffsets
    {
        /// <summary>The root node number.</summary>
        internal const int RootNode = 2;

        /// <summary>The leaf record count.</summary>
        internal const int LeafRecords = 6;

        /// <summary>The first leaf node number.</summary>
        internal const int FirstLeaf = 10;

        /// <summary>The last leaf node number.</summary>
        internal const int LastLeaf = 14;

        /// <summary>The node size.</summary>
        internal const int TreeNodeSize = 18;

        /// <summary>The longest key.</summary>
        internal const int MaxKeyLength = 20;

        /// <summary>The total node count.</summary>
        internal const int TotalNodes = 22;

        /// <summary>The clump size.</summary>
        internal const int ClumpSize = 32;

        /// <summary>The key compare type.</summary>
        internal const int CompareType = 37;

        /// <summary>The tree attributes.</summary>
        internal const int Attributes = 38;
    }

    /// <summary>UDIF koly trailer field offsets.</summary>
    private static class KolyOffsets
    {
        /// <summary>The version.</summary>
        internal const int Version = 4;

        /// <summary>The trailer size.</summary>
        internal const int HeaderSize = 8;

        /// <summary>The flags; 1 means flattened.</summary>
        internal const int Flags = 12;

        /// <summary>The data fork length.</summary>
        internal const int DataForkLength = 32;

        /// <summary>The segment number.</summary>
        internal const int SegmentNumber = 56;

        /// <summary>The segment count.</summary>
        internal const int SegmentCount = 60;

        /// <summary>The segment id.</summary>
        internal const int SegmentId = 64;

        /// <summary>The data fork checksum.</summary>
        internal const int DataChecksum = 80;

        /// <summary>The property list offset.</summary>
        internal const int XmlOffset = 216;

        /// <summary>The property list length.</summary>
        internal const int XmlLength = 224;

        /// <summary>The checksum over each blkx checksum.</summary>
        internal const int MasterChecksum = 352;

        /// <summary>The image variant.</summary>
        internal const int ImageVariant = 488;

        /// <summary>The sector count.</summary>
        internal const int SectorCount = 492;
    }
}
