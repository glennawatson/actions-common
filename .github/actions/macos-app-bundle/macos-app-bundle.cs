// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

return Program.Run();

/// <summary>Lays out an unsigned macOS .app bundle from a published app folder.</summary>
internal static partial class Program
{
    /// <summary>The property list document type and root element name.</summary>
    private const string PlistName = "plist";

    /// <summary>The public identifier of Apple's property list DTD.</summary>
    private const string PlistPublicId = "-//Apple//DTD PLIST 1.0//EN";

    /// <summary>The system identifier of Apple's property list DTD.</summary>
    private const string PlistSystemId = "http://www.apple.com/DTDs/PropertyList-1.0.dtd";

    /// <summary>The plist format version.</summary>
    private const string PlistVersion = "1.0";

    /// <summary>The plist key element.</summary>
    private const string KeyElement = "key";

    /// <summary>The plist string element.</summary>
    private const string StringElement = "string";

    /// <summary>The plist array element.</summary>
    private const string ArrayElement = "array";

    /// <summary>The plist dictionary element.</summary>
    private const string DictElement = "dict";

    /// <summary>The plist true element.</summary>
    private const string TrueElement = "true";

    /// <summary>The plist false element.</summary>
    private const string FalseElement = "false";

    /// <summary>The bundle folder that holds the executable code.</summary>
    private const string CodeFolder = "MacOS";

    /// <summary>The bundle folder that holds resources such as the icon.</summary>
    private const string ResourcesFolder = "Resources";

    /// <summary>The input that holds the bundle identifier.</summary>
    private const string IdentifierInput = "MACOS_BUNDLE_IDENTIFIER";

    /// <summary>The icon file name, without its extension, in Contents/Resources.</summary>
    private const string IconName = "AppIcon";

    /// <summary>The length of the PNG file signature.</summary>
    private const int PngSignatureLength = 8;

    /// <summary>The length of the type and length header of an icns chunk.</summary>
    private const int ChunkHeaderLength = 8;

    /// <summary>The offset of the IHDR width in a PNG file.</summary>
    private const int PngWidthOffset = 16;

    /// <summary>The bytes of a PNG file up to the end of the IHDR width and height.</summary>
    private const int PngHeaderLength = PngWidthOffset + sizeof(ulong);

    /// <summary>The execute bits for user, group and others.</summary>
    private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>The PNG file signature followed by the IHDR chunk length and type.</summary>
    private static readonly byte[] PngStart = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    /// <summary>The value elements a property list may hold.</summary>
    private static readonly HashSet<string> PlistValues = [StringElement, "integer", "real", TrueElement, FalseElement, "date", "data", ArrayElement, DictElement];

    /// <summary>The document roles macOS accepts.</summary>
    private static readonly HashSet<string> Roles = ["Editor", "Viewer", "Shell", "None"];

    /// <summary>The PNG icns types for each pixel size: the 1x type, then the 2x type of half the size.</summary>
    private static readonly Dictionary<uint, string[]> IcnsTypes = new()
    {
        [16] = ["icp4"],
        [32] = ["icp5", "ic11"],
        [64] = ["icp6", "ic12"],
        [128] = ["ic07"],
        [256] = ["ic08", "ic13"],
        [512] = ["ic09", "ic14"],
        [1024] = ["ic10"],
    };

    /// <summary>Gets the icns file magic.</summary>
    private static ReadOnlySpan<byte> IcnsMagic => "icns"u8;

    /// <summary>Lays out the bundle from the MACOS_* environment variables.</summary>
    /// <returns>The exit code.</returns>
    internal static int Run()
    {
        var source = Path.GetFullPath(Input("MACOS_SOURCE"));
        var app = Path.GetFullPath(Input("MACOS_OUTPUT")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var executable = Input("MACOS_EXECUTABLE");
        var icons = ReadIcons();
        var documentTypes = DocumentTypes(out var problem);
        if ((problem ?? CheckFiles(source, app, executable) ?? CheckIcons(icons) ?? CheckValues()) is { } message)
        {
            Console.WriteLine($"::error::{message}");
            return 1;
        }

        if (Directory.Exists(app))
        {
            Directory.Delete(app, recursive: true);
        }

        var contents = Path.Combine(app, "Contents");
        Copy(source, Path.Combine(contents, CodeFolder));
        var program = Path.Combine(contents, CodeFolder, executable);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(program, File.GetUnixFileMode(program) | ExecuteBits);
        }

        var types = WriteIcon(contents, icons);
        File.WriteAllText(Path.Combine(contents, "PkgInfo"), "APPL????", Encoding.ASCII);

        var plist = Path.Combine(contents, "Info.plist");
        WritePlist(plist, executable, documentTypes);
        if ((CheckPlist(plist, executable) ?? (types is null ? "The written icns file is malformed." : null)) is { } invalid)
        {
            Console.WriteLine($"::error::{invalid}");
            return 1;
        }

        Console.WriteLine($"Laid out {app}; the icon holds {string.Join(", ", types!)}.");
        return 0;
    }

    /// <summary>Reads an input from its environment variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The trimmed value, or empty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Input(string name) => (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();

    /// <summary>Gets the bundle version: the numeric core of a semantic version, which is all macOS accepts.</summary>
    /// <returns>The version, such as 1.2.3.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string BundleVersion() => Input("MACOS_VERSION").Split('-', '+')[0];

    /// <summary>Checks the files named by the inputs.</summary>
    /// <param name="source">The published app folder.</param>
    /// <param name="app">The .app path.</param>
    /// <param name="executable">The executable file name.</param>
    /// <returns>The problem, or null.</returns>
    private static string? CheckFiles(string source, string app, string executable)
    {
        if (!app.EndsWith(".app", StringComparison.Ordinal))
        {
            return $"output must end with .app: {app}";
        }

        return File.Exists(Path.Combine(source, executable)) ? null : $"{source} has no executable named '{executable}'.";
    }

    /// <summary>Reads the icon files, one path per line; a missing file reads as empty.</summary>
    /// <returns>The icon file contents.</returns>
    private static List<byte[]> ReadIcons()
    {
        List<byte[]> icons = [];
        foreach (var path in Input("MACOS_ICON").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            icons.Add(File.Exists(path) ? File.ReadAllBytes(path) : []);
        }

        return icons;
    }

    /// <summary>Checks the icons: one .icns file, or square PNGs of distinct supported sizes.</summary>
    /// <param name="icons">The icon file contents.</param>
    /// <returns>The problem, or null.</returns>
    private static string? CheckIcons(List<byte[]> icons)
    {
        if (icons is [var single] && single.AsSpan().StartsWith(IcnsMagic))
        {
            return IcnsEntries(single) is null ? "icon is an .icns file whose entry lengths do not fill it." : null;
        }

        HashSet<uint> sizes = [];
        foreach (var icon in icons)
        {
            if (!IcnsTypes.ContainsKey(PngSize(icon)) || !sizes.Add(PngSize(icon)))
            {
                break;
            }
        }

        return icons.Count > 0 && sizes.Count == icons.Count
            ? null
            : "icon must be one .icns file, or square PNGs of 16, 32, 64, 128, 256, 512 or 1024 pixels with one file per size.";
    }

    /// <summary>Checks the identifier, versions and category inputs.</summary>
    /// <returns>The problem, or null.</returns>
    private static string? CheckValues()
    {
        if (!BundleIdentifier().IsMatch(Input(IdentifierInput)))
        {
            return $"'{Input(IdentifierInput)}' is not a reverse-DNS bundle identifier.";
        }

        if (!BundleVersionPattern().IsMatch(BundleVersion()) || !BundleVersionPattern().IsMatch(Input("MACOS_MINIMUM_SYSTEM_VERSION")))
        {
            return "version (before any -pre-release or +build suffix) and minimum-system-version must be one to three dot-separated numbers.";
        }

        return Input("MACOS_CATEGORY") is { Length: > 0 } category && !category.StartsWith("public.app-category.", StringComparison.Ordinal)
            ? $"category '{category}' must be a public.app-category.* type."
            : null;
    }

    /// <summary>Parses the document-types input: lines such as "pdf com.adobe.pdf: Viewer: PDF Document".</summary>
    /// <param name="problem">The problem, or null.</param>
    /// <returns>The document types.</returns>
    private static List<DocumentType> DocumentTypes(out string? problem)
    {
        List<DocumentType> types = [];
        problem = null;
        foreach (var line in Input("MACOS_DOCUMENT_TYPES").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(':', StringSplitOptions.TrimEntries);
            var words = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || words.Length == 0 || !Roles.Contains(parts[1]))
            {
                problem = $"document-types line '{line}' needs 'extensions or types: Editor|Viewer|Shell|None[: name]'.";
                return types;
            }

            // Words with a dot are uniform type identifiers; the rest are file extensions.
            List<string> extensions = [];
            List<string> contentTypes = [];
            foreach (var word in words)
            {
                (word.Contains('.', StringComparison.Ordinal) ? contentTypes : extensions).Add(word);
            }

            types.Add(new(parts.Length > 2 ? parts[2] : $"{words[0].ToUpperInvariant()} document", parts[1], extensions, contentTypes));
        }

        return types;
    }

    /// <summary>Copies a folder tree, keeping symlinks and, where the file system has them, Unix modes.</summary>
    /// <param name="source">The source folder.</param>
    /// <param name="target">The target folder.</param>
    private static void Copy(string source, string target)
    {
        _ = Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(path));
            var info = new FileInfo(path);
            if (info.LinkTarget is { } link)
            {
                _ = File.CreateSymbolicLink(destination, link);
                continue;
            }

            if (Directory.Exists(path))
            {
                Copy(path, destination);
            }
            else
            {
                _ = info.CopyTo(destination);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, File.GetUnixFileMode(path));
            }
        }
    }

    /// <summary>Gets the side of a square PNG.</summary>
    /// <param name="png">The PNG bytes.</param>
    /// <returns>The side in pixels, or 0 when the image is not a square PNG.</returns>
    private static uint PngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < PngHeaderLength || !png[..PngStart.Length].SequenceEqual(PngStart))
        {
            return 0;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png[PngWidthOffset..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[(PngWidthOffset + sizeof(uint))..]);
        return width == height ? width : 0;
    }

    /// <summary>Writes Contents/Resources/AppIcon.icns: an icns icon as given, or the PNGs wrapped in icns entries.</summary>
    /// <param name="contents">The bundle's Contents folder.</param>
    /// <param name="icons">The icon file contents.</param>
    /// <returns>The entry types read back from the written file, or null when it is malformed.</returns>
    private static List<string>? WriteIcon(string contents, List<byte[]> icons)
    {
        var icns = Path.Combine(contents, ResourcesFolder, $"{IconName}.icns");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(icns)!);
        File.WriteAllBytes(icns, icons[0].AsSpan().StartsWith(IcnsMagic) ? icons[0] : Icns(icons));
        return IcnsEntries(File.ReadAllBytes(icns));
    }

    /// <summary>Writes an icns file with each PNG under the entry types of its pixel size.</summary>
    /// <param name="pngs">The square PNGs, one per size.</param>
    /// <returns>The icns bytes.</returns>
    private static byte[] Icns(List<byte[]> pngs)
    {
        var length = ChunkHeaderLength;
        foreach (var png in pngs)
        {
            length += IcnsTypes[PngSize(png)].Length * (ChunkHeaderLength + png.Length);
        }

        var icns = new byte[length];
        IcnsMagic.CopyTo(icns);
        BinaryPrimitives.WriteUInt32BigEndian(icns.AsSpan(IcnsMagic.Length), (uint)length);
        var offset = ChunkHeaderLength;
        foreach (var png in pngs)
        {
            foreach (var type in IcnsTypes[PngSize(png)])
            {
                _ = Encoding.ASCII.GetBytes(type, icns.AsSpan(offset));
                BinaryPrimitives.WriteUInt32BigEndian(icns.AsSpan(offset + type.Length), (uint)(ChunkHeaderLength + png.Length));
                png.CopyTo(icns, offset + ChunkHeaderLength);
                offset += ChunkHeaderLength + png.Length;
            }
        }

        return icns;
    }

    /// <summary>Streams the Info.plist to disk.</summary>
    /// <param name="path">The Info.plist path.</param>
    /// <param name="executable">The executable file name.</param>
    /// <param name="documentTypes">The document types.</param>
    private static void WritePlist(string path, string executable, List<DocumentType> documentTypes)
    {
        var name = Input("MACOS_NAME");
        using var writer = XmlWriter.Create(path, new() { Indent = true, IndentChars = "\t", NewLineChars = "\n", Encoding = new UTF8Encoding(false) });
        writer.WriteStartDocument();
        writer.WriteDocType(PlistName, PlistPublicId, PlistSystemId, null);
        writer.WriteStartElement(PlistName);
        writer.WriteAttributeString("version", PlistVersion);
        writer.WriteStartElement(DictElement);
        WriteString(writer, "CFBundleDevelopmentRegion", "en");
        WriteString(writer, "CFBundleDisplayName", name);
        WriteString(writer, "CFBundleExecutable", executable);
        WriteString(writer, "CFBundleIconFile", IconName);
        WriteString(writer, "CFBundleIdentifier", Input(IdentifierInput));
        WriteString(writer, "CFBundleInfoDictionaryVersion", "6.0");
        WriteString(writer, "CFBundleName", name);
        WriteString(writer, "CFBundlePackageType", "APPL");
        WriteString(writer, "CFBundleShortVersionString", BundleVersion());
        WriteString(writer, "CFBundleSignature", "????");
        WriteString(writer, "CFBundleVersion", BundleVersion());
        if (Input("MACOS_CATEGORY") is { Length: > 0 } category)
        {
            WriteString(writer, "LSApplicationCategoryType", category);
        }

        WriteString(writer, "LSMinimumSystemVersion", Input("MACOS_MINIMUM_SYSTEM_VERSION"));
        if (Input("MACOS_COPYRIGHT") is { Length: > 0 } copyright)
        {
            WriteString(writer, "NSHumanReadableCopyright", copyright);
        }

        writer.WriteElementString(KeyElement, "NSHighResolutionCapable");
        writer.WriteElementString(TrueElement, null);
        WriteDocumentTypes(writer, documentTypes);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteWhitespace("\n");
        writer.WriteEndDocument();
    }

    /// <summary>Writes CFBundleDocumentTypes when there are any.</summary>
    /// <param name="writer">The plist writer.</param>
    /// <param name="documentTypes">The document types.</param>
    private static void WriteDocumentTypes(XmlWriter writer, List<DocumentType> documentTypes)
    {
        if (documentTypes.Count == 0)
        {
            return;
        }

        writer.WriteElementString(KeyElement, "CFBundleDocumentTypes");
        writer.WriteStartElement(ArrayElement);
        foreach (var type in documentTypes)
        {
            writer.WriteStartElement(DictElement);
            WriteString(writer, "CFBundleTypeName", type.Name);
            WriteString(writer, "CFBundleTypeRole", type.Role);
            WriteString(writer, "LSHandlerRank", "Alternate");
            WriteStrings(writer, "CFBundleTypeExtensions", type.Extensions);
            WriteStrings(writer, "LSItemContentTypes", type.ContentTypes);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>Writes a string entry.</summary>
    /// <param name="writer">The plist writer.</param>
    /// <param name="name">The key.</param>
    /// <param name="value">The string value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteString(XmlWriter writer, string name, string value)
    {
        writer.WriteElementString(KeyElement, name);
        writer.WriteElementString(StringElement, value);
    }

    /// <summary>Writes an array of strings when it has items.</summary>
    /// <param name="writer">The plist writer.</param>
    /// <param name="name">The key.</param>
    /// <param name="values">The strings.</param>
    private static void WriteStrings(XmlWriter writer, string name, List<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        writer.WriteElementString(KeyElement, name);
        writer.WriteStartElement(ArrayElement);
        foreach (var value in values)
        {
            writer.WriteElementString(StringElement, value);
        }

        writer.WriteEndElement();
    }

    /// <summary>Parses the written Info.plist back and checks it against the plist DOCTYPE layout and the bundle.</summary>
    /// <param name="path">The Info.plist path.</param>
    /// <param name="executable">The executable file name.</param>
    /// <returns>The problem, or null.</returns>
    private static string? CheckPlist(string path, string executable)
    {
        // The DOCTYPE is parsed but the DTD is never fetched.
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(path, new() { DtdProcessing = DtdProcessing.Parse, XmlResolver = null, IgnoreWhitespace = true, IgnoreComments = true }))
        {
            document.Load(reader);
        }

        if (document.DocumentType is not { Name: PlistName, PublicId: PlistPublicId, SystemId: PlistSystemId, InternalSubset: null or "" })
        {
            return "Info.plist does not have the Apple plist DOCTYPE.";
        }

        var root = document.DocumentElement;
        return root is not { Name: PlistName, FirstChild: XmlElement { Name: DictElement, NextSibling: null } dict } || root.GetAttribute("version") != PlistVersion
            ? "Info.plist must be <plist version=\"1.0\"> holding one <dict>."
            : CheckValue(dict) ?? CheckBundle(Entries(dict), Path.GetDirectoryName(path)!, executable);
    }

    /// <summary>Checks that the Info.plist names the files the bundle holds.</summary>
    /// <param name="entries">The string entries of the Info.plist.</param>
    /// <param name="contents">The bundle's Contents folder.</param>
    /// <param name="executable">The executable file name.</param>
    /// <returns>The problem, or null.</returns>
    private static string? CheckBundle(Dictionary<string, string> entries, string contents, string executable)
    {
        if (entries.GetValueOrDefault("CFBundleExecutable") != executable || !File.Exists(Path.Combine(contents, CodeFolder, executable)))
        {
            return $"CFBundleExecutable does not name Contents/MacOS/{executable}.";
        }

        return File.Exists(Path.Combine(contents, ResourcesFolder, $"{entries.GetValueOrDefault("CFBundleIconFile")}.icns"))
            ? null
            : "CFBundleIconFile does not name an .icns in Contents/Resources.";
    }

    /// <summary>Checks a plist value element and everything inside it.</summary>
    /// <param name="value">The value element.</param>
    /// <returns>The problem, or null.</returns>
    private static string? CheckValue(XmlElement value)
    {
        var name = value.Name;
        if (!PlistValues.Contains(name))
        {
            return $"Info.plist has an invalid value element <{name}>.";
        }

        return name switch
        {
            DictElement => CheckDict(value),
            ArrayElement => CheckArray(value),
            TrueElement or FalseElement => value.HasChildNodes ? $"Info.plist has <{name}> with content." : null,
            _ => value.FirstChild is null or { NodeType: XmlNodeType.Text, NextSibling: null } ? null : $"Info.plist has <{name}> with child elements.",
        };
    }

    /// <summary>Checks that a dict holds unique keys, each followed by one valid value.</summary>
    /// <param name="dict">The dict element.</param>
    /// <returns>The problem, or null.</returns>
    private static string? CheckDict(XmlElement dict)
    {
        HashSet<string> keys = [];
        var expectKey = true;
        for (var child = dict.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is not XmlElement element)
            {
                return "Info.plist has text directly inside a <dict>.";
            }

            if (expectKey && (element.Name != KeyElement || !keys.Add(element.InnerText)))
            {
                return $"Info.plist has a <dict> whose keys are missing or repeated, near '{element.InnerText}'.";
            }

            if (!expectKey && CheckValue(element) is { } problem)
            {
                return problem;
            }

            expectKey = !expectKey;
        }

        return expectKey ? null : "Info.plist has a <dict> key without a value.";
    }

    /// <summary>Checks that every item of an array is a valid value.</summary>
    /// <param name="items">The array element.</param>
    /// <returns>The problem, or null.</returns>
    private static string? CheckArray(XmlElement items)
    {
        for (var child = items.FirstChild; child is not null; child = child.NextSibling)
        {
            if ((child is XmlElement element ? CheckValue(element) : "Info.plist has text directly inside an <array>.") is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Gets the string entries of a dict.</summary>
    /// <param name="dict">The dict element.</param>
    /// <returns>The values of the keys followed by a string.</returns>
    private static Dictionary<string, string> Entries(XmlElement dict)
    {
        Dictionary<string, string> entries = [];
        for (var child = dict.FirstChild; child?.NextSibling is { } value; child = value.NextSibling)
        {
            if (value.Name == StringElement)
            {
                entries[child.InnerText] = value.InnerText;
            }
        }

        return entries;
    }

    /// <summary>Walks the entries of an icns file, checking its lengths and any PNG entries.</summary>
    /// <param name="icns">The icns bytes.</param>
    /// <returns>The entry types, or null when the file is malformed.</returns>
    private static List<string>? IcnsEntries(ReadOnlySpan<byte> icns)
    {
        if (icns.Length < ChunkHeaderLength || !icns.StartsWith(IcnsMagic) || BinaryPrimitives.ReadUInt32BigEndian(icns[IcnsMagic.Length..]) != icns.Length)
        {
            return null;
        }

        List<string> types = [];
        for (var offset = ChunkHeaderLength; offset < icns.Length;)
        {
            var length = ChunkLength(icns, offset);
            if (length == 0)
            {
                return null;
            }

            types.Add(Encoding.ASCII.GetString(icns.Slice(offset, IcnsMagic.Length)));
            offset += length;
        }

        return types.Count > 0 ? types : null;
    }

    /// <summary>Gets the length of one icns entry, checking it fits the file and that a PNG entry is a square PNG.</summary>
    /// <param name="icns">The icns bytes.</param>
    /// <param name="offset">The entry offset.</param>
    /// <returns>The entry length with its header, or 0 when the entry is malformed.</returns>
    private static int ChunkLength(ReadOnlySpan<byte> icns, int offset)
    {
        var length = offset + ChunkHeaderLength <= icns.Length ? BinaryPrimitives.ReadUInt32BigEndian(icns[(offset + IcnsMagic.Length)..]) : 0;
        if (length < ChunkHeaderLength || length > icns.Length - offset)
        {
            return 0;
        }

        var data = icns.Slice(offset + ChunkHeaderLength, (int)length - ChunkHeaderLength);
        return data.StartsWith(PngStart.AsSpan(0, PngSignatureLength)) && PngSize(data) == 0 ? 0 : (int)length;
    }

    /// <summary>Matches a reverse-DNS bundle identifier.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")]
    private static partial Regex BundleIdentifier();

    /// <summary>Matches a bundle or system version: one to three dot-separated numbers.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex(@"^[0-9]+(\.[0-9]+){0,2}$")]
    private static partial Regex BundleVersionPattern();

    /// <summary>A file association.</summary>
    /// <param name="Name">The document type name.</param>
    /// <param name="Role">The app's role: Editor, Viewer, Shell or None.</param>
    /// <param name="Extensions">The file extensions.</param>
    /// <param name="ContentTypes">The uniform type identifiers.</param>
    private sealed record DocumentType(string Name, string Role, List<string> Extensions, List<string> ContentTypes);
}
