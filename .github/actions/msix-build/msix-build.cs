#:package System.IO.Hashing

// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using static System.Environment;

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

return Program.Run();

/// <summary>Builds an unsigned full-trust desktop MSIX from an app folder.</summary>
internal static partial class Program
{
    /// <summary>Longest application Id.</summary>
    private const int MaximumAppIdLength = 64;

    /// <summary>Numbers in a package version.</summary>
    private const int VersionParts = 4;

    /// <summary>Package manifest part.</summary>
    private const string ManifestName = "AppxManifest.xml";

    /// <summary>Block map part.</summary>
    private const string BlockMapName = "AppxBlockMap.xml";

    /// <summary>Content types part.</summary>
    private const string ContentTypesName = "[Content_Types].xml";

    /// <summary>Signature part that signing adds later.</summary>
    private const string SignatureName = "AppxSignature.p7x";

    /// <summary>Manifest foundation namespace.</summary>
    private const string FoundationNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    /// <summary>Manifest universal app platform namespace.</summary>
    private const string UapNamespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    /// <summary>Manifest universal app platform namespace, version 3.</summary>
    private const string Uap3Namespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";

    /// <summary>Manifest restricted capabilities namespace.</summary>
    private const string RescapNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    /// <summary>Block map namespace.</summary>
    private const string BlockMapNamespace = "http://schemas.microsoft.com/appx/2010/blockmap";

    /// <summary>Content types namespace.</summary>
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>The Name attribute.</summary>
    private const string NameAttribute = "Name";

    /// <summary>The DisplayName element and attribute.</summary>
    private const string DisplayNameElement = "DisplayName";

    /// <summary>The Description element and attribute.</summary>
    private const string DescriptionElement = "Description";

    /// <summary>Capability order: foundation capabilities come first.</summary>
    private const int FoundationGroup = 0;

    /// <summary>Capability order: universal app platform capabilities follow.</summary>
    private const int UapGroup = 1;

    /// <summary>Capability order: restricted capabilities follow.</summary>
    private const int RestrictedGroup = 2;

    /// <summary>Capability order: device capabilities come last.</summary>
    private const int DeviceGroup = 3;

    /// <summary>Restricted capabilities known by name.</summary>
    private static readonly FrozenSet<string> RestrictedCapabilities = FrozenSet.Create(
        StringComparer.Ordinal,
        "runFullTrust",
        "broadFileSystemAccess",
        "allowElevation",
        "unvirtualizedResources",
        "packageManagement",
        "packageQuery",
        "localSystemServices",
        "confirmAppClose");

    /// <summary>Universal app platform capabilities known by name.</summary>
    private static readonly FrozenSet<string> UapCapabilities = FrozenSet.Create(
        StringComparer.Ordinal,
        "documentsLibrary",
        "picturesLibrary",
        "videosLibrary",
        "musicLibrary",
        "removableStorage",
        "enterpriseAuthentication",
        "sharedUserCertificates",
        "userAccountInformation",
        "appointments",
        "contacts",
        "phoneCall",
        "voipCall",
        "chat",
        "objects3D",
        "blockedChatMessages");

    /// <summary>Device capabilities known by name.</summary>
    private static readonly FrozenSet<string> DeviceCapabilities = FrozenSet.Create(
        StringComparer.Ordinal,
        "webcam",
        "microphone",
        "location",
        "proximity",
        "bluetooth",
        "serialcommunication",
        "usb",
        "humaninterfacedevice",
        "pointOfService",
        "lowLevel");

    /// <summary>Builds the package from the action inputs.</summary>
    /// <returns>The process exit code.</returns>
    internal static int Run()
    {
        try
        {
            var (output, count) = Build();
            Console.WriteLine($"Built {output}: {count} files.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or OverflowException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"::error::{ex.Message}");
            return 1;
        }
    }

    /// <summary>Checks the inputs, then writes the package.</summary>
    /// <returns>The package path and the number of payload files.</returns>
    private static (string Output, int Count) Build()
    {
        var source = Path.GetFullPath(Required("MSIX_SOURCE"));
        var output = Path.GetFullPath(Required("MSIX_OUTPUT"));
        Require(Directory.Exists(source), $"source folder not found: {source}");
        var identity = Required("MSIX_IDENTITY");
        Require(IdentityName().IsMatch(identity), $"identity-name must be 3 to 50 letters, digits, dots or dashes: {identity}");
        var publisher = Required("MSIX_PUBLISHER");
        Require(publisher.Contains('=', StringComparison.Ordinal), $"publisher must be a distinguished name such as CN=Name: {publisher}");
        var arch = Required("MSIX_ARCH");
        Require(arch is "x64" or "arm64" or "x86", "architecture must be x64, arm64 or x86");
        var appId = Required("MSIX_APP_ID");
        Require(IsApplicationId(appId), $"application-id must be dot-separated words that start with a letter: {appId}");

        var files = Payload(source);
        var displayName = Required("MSIX_DISPLAY_NAME");
        AppInfo app = new(
            identity,
            publisher,
            PackageVersion(Required("MSIX_VERSION")),
            arch,
            displayName,
            Required("MSIX_PUBLISHER_DISPLAY_NAME"),
            Fallback(Input("MSIX_DESCRIPTION"), displayName),
            appId,
            PayloadPath(files, "MSIX_EXECUTABLE"),
            PayloadPath(files, "MSIX_LOGO44"),
            PayloadPath(files, "MSIX_LOGO150"),
            PayloadPath(files, "MSIX_STORE_LOGO"));
        var fileTypeName = Fallback(Input("MSIX_FILE_TYPE_NAME"), $"{displayName} document");
        var manifest = Manifest(app, Required("MSIX_LANGUAGE"), Required("MSIX_MIN_VERSION"), Required("MSIX_MAX_TESTED"), Lines("MSIX_FILE_TYPES"), fileTypeName, Lines("MSIX_CAPABILITIES"));

        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            WritePackage(output, manifest, files, Timestamp());
        }
        catch
        {
            // A half-written package must not be mistaken for a build result.
            File.Delete(output);
            throw;
        }

        return (output, files.Count);
    }

    /// <summary>Reads an action input from its environment variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The trimmed value, or empty.</returns>
    private static string Input(string name) => GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

    /// <summary>Reads a required action input.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The trimmed value.</returns>
    private static string Required(string name)
    {
        var value = Input(name);
        Require(value.Length > 0, $"{name} is required");
        return value;
    }

    /// <summary>Reads a newline-separated action input.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The non-empty trimmed lines.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string[] Lines(string name) => Input(name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Returns a value, or a fallback when it is empty.</summary>
    /// <param name="value">The value.</param>
    /// <param name="fallback">The fallback.</param>
    /// <returns>The value or the fallback.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Fallback(string value, string fallback) => value.Length > 0 ? value : fallback;

    /// <summary>Fails with a message when a condition does not hold.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="message">The error message.</param>
    /// <exception cref="InvalidDataException">The condition is false.</exception>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    /// <summary>Gets the time stamped on every entry: SOURCE_DATE_EPOCH when set, so builds repeat, else now.</summary>
    /// <returns>The UTC time.</returns>
    private static DateTime Timestamp() =>
        long.TryParse(Input("SOURCE_DATE_EPOCH"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            ? DateTime.UnixEpoch.AddSeconds(epoch)
            : TimeProvider.System.GetUtcNow().UtcDateTime;

    /// <summary>Matches a package identity name: 3 to 50 ASCII letters, digits, dots or dashes.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("^[A-Za-z0-9.-]{3,50}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentityName();

    /// <summary>Checks an application Id: dot-separated ASCII words that start with a letter.</summary>
    /// <param name="id">The Id.</param>
    /// <returns>Whether the Id is valid.</returns>
    private static bool IsApplicationId(string id)
    {
        if (id.Length > MaximumAppIdLength)
        {
            return false;
        }

        foreach (var word in id.Split('.'))
        {
            if (word.Length == 0 || !char.IsAsciiLetter(word[0]))
            {
                return false;
            }

            foreach (var character in word)
            {
                if (!char.IsAsciiLetterOrDigit(character))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Lists the files to package in ordinal name order.</summary>
    /// <param name="source">The app folder.</param>
    /// <returns>Each file's path and package name.</returns>
    private static List<PayloadFile> Payload(string source)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        var files = new List<PayloadFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(source, "*", options))
        {
            var name = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (new FileInfo(path).LinkTarget is not null)
            {
                // Following a link could pull in a file from outside the source, such as an absolute path on the build machine.
                Console.WriteLine($"::warning::skipped symbolic link {name}; MSIX packages cannot hold links");
                continue;
            }

            Require(!IsFootprint(name), $"source must not contain {name}; the action writes it");

            // Package part names are case-insensitive.
            Require(seen.Add(name), $"two files differ only by case: {name}");
            files.Add(new(path, name));
        }

        Require(files.Count > 0, $"source folder is empty: {source}");
        files.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return files;
    }

    /// <summary>Checks whether a name is one of the package metadata parts.</summary>
    /// <param name="name">The part name.</param>
    /// <returns>Whether the action or signing writes this part.</returns>
    private static bool IsFootprint(string name) =>
        name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(BlockMapName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(ContentTypesName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(SignatureName, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("AppxMetadata/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves a path input to a packaged file.</summary>
    /// <param name="files">The packaged files.</param>
    /// <param name="input">The input variable.</param>
    /// <returns>The manifest path, with backslashes.</returns>
    /// <exception cref="InvalidDataException">The file is not packaged.</exception>
    private static string PayloadPath(List<PayloadFile> files, string input)
    {
        var relative = Required(input).Replace('\\', '/').TrimStart('/');
        foreach (var file in files)
        {
            if (file.Name.Equals(relative, StringComparison.OrdinalIgnoreCase))
            {
                return file.Name.Replace('/', '\\');
            }
        }

        throw new InvalidDataException($"{relative} is not in the source folder");
    }

    /// <summary>Converts a semantic version to the package version a.b.c.d.</summary>
    /// <param name="version">The semantic version; a pre-release or build suffix is dropped.</param>
    /// <returns>The package version.</returns>
    /// <exception cref="FormatException">The version has more than four numbers or one is above 65535.</exception>
    private static string PackageVersion(string version)
    {
        var parts = version.TrimStart('v', 'V').Split('-', '+')[0].Split('.');
        if (parts.Length > VersionParts)
        {
            throw new FormatException($"version must be major[.minor[.patch[.revision]]] with an optional suffix: {version}");
        }

        Span<ushort> numbers = stackalloc ushort[VersionParts];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                throw new FormatException($"version numbers must be whole numbers from 0 to 65535: {version}");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{numbers[0]}.{numbers[1]}.{numbers[2]}.{numbers[3]}");
    }

    /// <summary>Creates a full-trust desktop app manifest.</summary>
    /// <param name="app">The app identity and display values.</param>
    /// <param name="language">The resource language.</param>
    /// <param name="minVersion">The lowest Windows build.</param>
    /// <param name="maxTested">The highest Windows build tested.</param>
    /// <param name="fileTypes">The file extensions the app opens.</param>
    /// <param name="fileTypeName">The name shown for those files.</param>
    /// <param name="capabilities">The capabilities.</param>
    /// <returns>The manifest root.</returns>
    private static XElement Manifest(AppInfo app, string language, string minVersion, string maxTested, string[] fileTypes, string fileTypeName, string[] capabilities)
    {
        XNamespace foundation = FoundationNamespace;
        XNamespace uap = UapNamespace;
        var application = new XElement(
            foundation + "Application",
            new XAttribute("Id", app.AppId),
            new XAttribute("Executable", app.Executable),
            new XAttribute("EntryPoint", "Windows.FullTrustApplication"),
            new XElement(
                uap + "VisualElements",
                new XAttribute(DisplayNameElement, app.DisplayName),
                new XAttribute(DescriptionElement, app.Description),
                new XAttribute("BackgroundColor", "transparent"),
                new XAttribute("Square150x150Logo", app.Logo150),
                new XAttribute("Square44x44Logo", app.Logo44)));
        if (fileTypes.Length > 0)
        {
            application.Add(new XElement(foundation + "Extensions", FileTypeAssociation(fileTypes, fileTypeName)));
        }

        return new(
            foundation + "Package",
            new XAttribute("xmlns", FoundationNamespace),
            new XAttribute(XNamespace.Xmlns + "uap", UapNamespace),
            new XAttribute(XNamespace.Xmlns + "uap3", Uap3Namespace),
            new XAttribute(XNamespace.Xmlns + "rescap", RescapNamespace),
            new XAttribute("IgnorableNamespaces", "uap uap3 rescap"),
            new XElement(
                foundation + "Identity",
                new XAttribute(NameAttribute, app.Identity),
                new XAttribute("Publisher", app.Publisher),
                new XAttribute(nameof(System.Version), app.PackageVersion),
                new XAttribute("ProcessorArchitecture", app.Arch)),
            new XElement(
                foundation + "Properties",
                new XElement(foundation + DisplayNameElement, app.DisplayName),
                new XElement(foundation + "PublisherDisplayName", app.PublisherDisplayName),
                new XElement(foundation + DescriptionElement, app.Description),
                new XElement(foundation + "Logo", app.StoreLogo)),
            new XElement(foundation + "Resources", new XElement(foundation + "Resource", new XAttribute("Language", language))),
            new XElement(
                foundation + "Dependencies",
                new XElement(
                    foundation + "TargetDeviceFamily",
                    new XAttribute(NameAttribute, "Windows.Desktop"),
                    new XAttribute("MinVersion", minVersion),
                    new XAttribute("MaxVersionTested", maxTested))),
            new XElement(foundation + "Applications", application),
            new XElement(foundation + "Capabilities", Capabilities(capabilities)));
    }

    /// <summary>Creates the file type association extension.</summary>
    /// <param name="fileTypes">The file extensions.</param>
    /// <param name="fileTypeName">The name shown for the files.</param>
    /// <returns>The extension element.</returns>
    private static XElement FileTypeAssociation(string[] fileTypes, string fileTypeName)
    {
        XNamespace uap = UapNamespace;
        XNamespace uap3 = Uap3Namespace;
        var supported = new XElement(uap + "SupportedFileTypes");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = string.Empty;
        foreach (var type in fileTypes)
        {
            var extension = (type.StartsWith('.') ? type : $".{type}").ToLowerInvariant();
            Require(extension.Length > 1, $"file type is empty: {type}");
            if (!seen.Add(extension))
            {
                continue;
            }

            supported.Add(new XElement(uap + "FileType", extension));
            first = first.Length == 0 ? extension : first;
        }

        // The association name must be lower case letters, digits or dashes, so it is taken from the first extension.
        var associationName = string.Create(first.Length - 1, first, static (span, extension) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var character = extension[i + 1];
                span[i] = char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) ? character : '-';
            }
        });

        return new(
            uap + "Extension",
            new XAttribute("Category", "windows.fileTypeAssociation"),
            new XElement(
                uap3 + "FileTypeAssociation",
                new XAttribute(NameAttribute, associationName),
                new XAttribute("Parameters", "\"%1\""),
                new XElement(uap + DisplayNameElement, fileTypeName),
                supported));
    }

    /// <summary>Creates capability elements in the order the schema expects.</summary>
    /// <param name="names">The capabilities, each optionally prefixed with a namespace.</param>
    /// <returns>The capability elements.</returns>
    private static List<XElement> Capabilities(string[] names)
    {
        List<XElement>[] groups = [[], [], [], []];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in names)
        {
            if (!seen.Add(entry))
            {
                continue;
            }

            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            var (ns, group) = colon > 0 ? PrefixedCapability(entry[..colon]) : KnownCapability(entry);
            var elementName = group == DeviceGroup ? "DeviceCapability" : "Capability";
            groups[group].Add(new(XNamespace.Get(ns) + elementName, new XAttribute(NameAttribute, entry[(colon + 1)..])));
        }

        return [.. groups[FoundationGroup], .. groups[UapGroup], .. groups[RestrictedGroup], .. groups[DeviceGroup]];
    }

    /// <summary>Resolves a capability namespace prefix.</summary>
    /// <param name="prefix">The prefix, such as uap or rescap.</param>
    /// <returns>The namespace and capability group.</returns>
    /// <exception cref="InvalidDataException">The prefix is unknown.</exception>
    private static (string Namespace, int Group) PrefixedCapability(string prefix) => prefix switch
    {
        "uap" => (UapNamespace, UapGroup),
        "uap2" => ("http://schemas.microsoft.com/appx/manifest/uap/windows10/2", UapGroup),
        "uap3" => (Uap3Namespace, UapGroup),
        "uap4" => ("http://schemas.microsoft.com/appx/manifest/uap/windows10/4", UapGroup),
        "uap6" => ("http://schemas.microsoft.com/appx/manifest/uap/windows10/6", UapGroup),
        "uap7" => ("http://schemas.microsoft.com/appx/manifest/uap/windows10/7", UapGroup),
        "uap11" => ("http://schemas.microsoft.com/appx/manifest/uap/windows10/11", UapGroup),
        "rescap" => (RescapNamespace, RestrictedGroup),
        _ => throw new InvalidDataException($"unknown capability prefix: {prefix}"),
    };

    /// <summary>Finds the namespace of an unprefixed capability; unknown names are foundation capabilities.</summary>
    /// <param name="name">The capability name.</param>
    /// <returns>The namespace and capability group.</returns>
    private static (string Namespace, int Group) KnownCapability(string name)
    {
        if (RestrictedCapabilities.Contains(name))
        {
            return (RescapNamespace, RestrictedGroup);
        }

        if (UapCapabilities.Contains(name))
        {
            return (UapNamespace, UapGroup);
        }

        return DeviceCapabilities.Contains(name) ? (FoundationNamespace, DeviceGroup) : (FoundationNamespace, FoundationGroup);
    }

    /// <summary>Picks the content type recorded for a payload file.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The MIME type.</returns>
    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".ico" => "image/vnd.microsoft.icon",
        ".xml" => "application/xml",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".exe" or ".dll" => "application/x-msdownload",
        _ => "application/octet-stream",
    };

    /// <summary>Writes the manifest, the payload, the block map and the content types, in that order.</summary>
    /// <param name="output">The package path.</param>
    /// <param name="manifest">The manifest root.</param>
    /// <param name="files">The payload files.</param>
    /// <param name="modified">The time stamped on every entry.</param>
    private static void WritePackage(string output, XElement manifest, List<PayloadFile> files, DateTime modified)
    {
        XNamespace blockMap = BlockMapNamespace;

        // Windows reads HashMethod straight from the opening tag, so it follows the namespace as makeappx writes it.
        var map = new XElement(blockMap + "BlockMap", new XAttribute("xmlns", BlockMapNamespace), new XAttribute("HashMethod", "http://www.w3.org/2001/04/xmlenc#sha256"));
        var types = new XElement(XNamespace.Get(ContentTypesNamespace) + "Types");
        using var package = new PackageWriter(output, modified);
        using (var content = new ReadOnlyMemoryStream(Xml(manifest)))
        {
            AddPart(package, map, types, ManifestName, "application/vnd.ms-appx.manifest+xml", content);
        }

        foreach (var file in files)
        {
            using var content = new FileStream(file.Path, new FileStreamOptions { Options = FileOptions.SequentialScan, BufferSize = 0 });
            AddPart(package, map, types, file.Name, ContentTypeFor(file.Name), content);
        }

        types.Add(Override(types.Name.Namespace, BlockMapName, "application/vnd.ms-appx.blockmap+xml"));
        using (var content = new ReadOnlyMemoryStream(Xml(map)))
        {
            package.WriteEntry(BlockMapName, content, null);
        }

        using var contentTypes = new ReadOnlyMemoryStream(Xml(types));
        package.WriteEntry(ContentTypesName, contentTypes, null);
    }

    /// <summary>Writes a part and records it in the block map and the content types.</summary>
    /// <param name="package">The package writer.</param>
    /// <param name="map">The block map root.</param>
    /// <param name="types">The content types root.</param>
    /// <param name="name">The part name with forward slashes.</param>
    /// <param name="contentType">The part's MIME type.</param>
    /// <param name="content">The uncompressed content.</param>
    private static void AddPart(PackageWriter package, XElement map, XElement types, string name, string contentType, Stream content)
    {
        // Archive names are percent-encoded; the block map uses the plain name with backslashes.
        var zipName = EncodePartName(name);
        var file = new XElement(map.Name.Namespace + "File", new XAttribute(NameAttribute, name.Replace('/', '\\')));
        map.Add(file);
        package.WriteEntry(zipName, content, file);
        types.Add(Override(types.Name.Namespace, zipName, contentType));
    }

    /// <summary>Creates a content type override for one part.</summary>
    /// <param name="ns">The content types namespace.</param>
    /// <param name="zipName">The archive entry name.</param>
    /// <param name="contentType">The MIME type.</param>
    /// <returns>The override element.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static XElement Override(XNamespace ns, string zipName, string contentType) =>
        new(ns + "Override", new XAttribute("PartName", $"/{zipName}"), new XAttribute("ContentType", contentType));

    /// <summary>Percent-encodes each segment of a part name.</summary>
    /// <param name="name">The part name with forward slashes.</param>
    /// <returns>The encoded name.</returns>
    private static string EncodePartName(string name)
    {
        var segments = name.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join('/', segments);
    }

    /// <summary>Serialises an XML element as UTF-8 with a declaration and no byte order mark.</summary>
    /// <param name="root">The root element.</param>
    /// <returns>The document bytes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] Xml(XElement root) =>
        Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n{root.ToString(SaveOptions.DisableFormatting)}");

    /// <summary>Writes little-endian fields one after another into a span.</summary>
    /// <param name="destination">The span the fields fill.</param>
    private ref struct FieldWriter(Span<byte> destination)
    {
        /// <summary>The span the fields fill.</summary>
        private readonly Span<byte> _destination = destination;

        /// <summary>The offset of the next field.</summary>
        private int _offset;

        /// <summary>Writes a 16-bit field.</summary>
        /// <param name="value">The value.</param>
        internal void UInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_destination[_offset..], value);
            _offset += sizeof(ushort);
        }

        /// <summary>Writes a 32-bit field.</summary>
        /// <param name="value">The value.</param>
        internal void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_destination[_offset..], value);
            _offset += sizeof(uint);
        }

        /// <summary>Writes a 64-bit field.</summary>
        /// <param name="value">The value.</param>
        internal void UInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_destination[_offset..], value);
            _offset += sizeof(ulong);
        }
    }

    /// <summary>A file to package.</summary>
    /// <param name="Path">The file system path.</param>
    /// <param name="Name">The part name, relative to the source, with forward slashes.</param>
    private readonly record struct PayloadFile(string Path, string Name);

    /// <summary>Writes ZIP entries whose 64 KB blocks are deflated on their own, so the block map holds each block's exact compressed size.</summary>
    private sealed class PackageWriter : IDisposable
    {
        /// <summary>Uncompressed bytes in one block map block.</summary>
        private const int BlockLength = 65_536;

        /// <summary>Fixed part of a ZIP local file header.</summary>
        private const int LocalHeaderLength = 30;

        /// <summary>Fixed part of a ZIP central directory header.</summary>
        private const int CentralHeaderLength = 46;

        /// <summary>Offset of the CRC and sizes in a local file header.</summary>
        private const int LocalCrcOffset = 14;

        /// <summary>The CRC, compressed size and size fields of a local file header.</summary>
        private const int LocalSizesLength = 12;

        /// <summary>ZIP64 end record, locator and end record together.</summary>
        private const int TrailerLength = 98;

        /// <summary>ZIP64 end record size, excluding its signature and this field.</summary>
        private const ulong Zip64EndRecordLength = 44;

        /// <summary>Local file header signature.</summary>
        private const uint LocalSignature = 0x04034B50;

        /// <summary>Central directory header signature.</summary>
        private const uint CentralSignature = 0x02014B50;

        /// <summary>ZIP64 end of central directory signature.</summary>
        private const uint Zip64EndSignature = 0x06064B50;

        /// <summary>ZIP64 end of central directory locator signature.</summary>
        private const uint Zip64LocatorSignature = 0x07064B50;

        /// <summary>End of central directory signature.</summary>
        private const uint EndSignature = 0x06054B50;

        /// <summary>ZIP version needed for DEFLATE.</summary>
        private const ushort ZipVersion = 20;

        /// <summary>ZIP version written in the ZIP64 end record.</summary>
        private const ushort Zip64Version = 45;

        /// <summary>ZIP flag marking names as UTF-8.</summary>
        private const ushort Utf8Flag = 0x0800;

        /// <summary>ZIP raw DEFLATE method.</summary>
        private const ushort DeflateMethod = 8;

        /// <summary>DEFLATE level: the smallest output, since a package is built once and downloaded many times.</summary>
        private const int DeflateLevel = 9;

        /// <summary>First year a DOS timestamp holds.</summary>
        private const int DosFirstYear = 1980;

        /// <summary>Last year a DOS timestamp holds.</summary>
        private const int DosLastYear = 2107;

        /// <summary>The package file.</summary>
        private readonly SafeFileHandle _handle;

        /// <summary>The DOS time and date stamped on every entry.</summary>
        private readonly uint _dosTime;

        /// <summary>The encoder, reset before each block so every block inflates on its own.</summary>
        private readonly DeflateEncoder _encoder = new(new ZLibCompressionOptions { CompressionLevel = DeflateLevel });

        /// <summary>The uncompressed block buffer.</summary>
        private readonly byte[] _plain = ArrayPool<byte>.Shared.Rent(BlockLength);

        /// <summary>The compressed block buffer.</summary>
        private readonly byte[] _compressed = ArrayPool<byte>.Shared.Rent(checked((int)DeflateEncoder.GetMaxCompressedLength(BlockLength)));

        /// <summary>The finished entries, for the central directory.</summary>
        private readonly List<Entry> _entries = [];

        /// <summary>The next write offset.</summary>
        private long _position;

        /// <summary>Initializes a new instance of the <see cref="PackageWriter"/> class.</summary>
        /// <param name="path">The package path.</param>
        /// <param name="modified">The time stamped on every entry.</param>
        internal PackageWriter(string path, DateTime modified)
        {
            _handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write);
            _dosTime = DosTime(modified);
        }

        /// <summary>Writes the central directory and releases the file and buffers.</summary>
        public void Dispose()
        {
            try
            {
                WriteCentralDirectory();
            }
            finally
            {
                _handle.Dispose();
                _encoder.Dispose();
                ArrayPool<byte>.Shared.Return(_plain);
                ArrayPool<byte>.Shared.Return(_compressed);
            }
        }

        /// <summary>Writes a deflated entry and, when given a block map file element, its block hashes and sizes.</summary>
        /// <param name="zipName">The entry name.</param>
        /// <param name="source">The uncompressed content.</param>
        /// <param name="file">The block map file element, or null for a part the block map does not list.</param>
        internal void WriteEntry(string zipName, Stream source, XElement? file)
        {
            var name = Encoding.UTF8.GetBytes(zipName);
            var offset = _position;

            // The CRC and sizes are written over zeros once the data is compressed.
            Span<byte> header = stackalloc byte[LocalHeaderLength];
            var fields = new FieldWriter(header);
            fields.UInt32(LocalSignature);
            fields.UInt16(ZipVersion);
            fields.UInt16(Utf8Flag);
            fields.UInt16(DeflateMethod);
            fields.UInt32(_dosTime);
            fields.UInt32(0);
            fields.UInt32(0);
            fields.UInt32(0);
            fields.UInt16(checked((ushort)name.Length));
            fields.UInt16(0);
            Append(header);
            Append(name);

            var dataStart = _position;
            var crc = new Crc32();
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            long length = 0;
            int count;
            while ((count = source.ReadAtLeast(_plain.AsSpan(0, BlockLength), BlockLength, throwOnEndOfStream: false)) > 0)
            {
                var block = _plain.AsSpan(0, count);
                crc.Append(block);
                var compressed = Deflate(block);
                Append(compressed);
                length += count;
                if (file is null)
                {
                    continue;
                }

                _ = SHA256.HashData(block, hash);
                file.Add(new XElement(file.Name.Namespace + "Block", new XAttribute("Hash", Convert.ToBase64String(hash)), new XAttribute("Size", compressed.Length)));
            }

            // The blocks end with sync flushes, so an empty final block closes the stream outside every block's size.
            Append(Finish());
            file?.SetAttributeValue("Size", length);
            file?.SetAttributeValue("LfhSize", LocalHeaderLength + name.Length);

            var entry = new Entry(name, crc.GetCurrentHashAsUInt32(), checked((uint)(_position - dataStart)), checked((uint)length), checked((uint)offset));
            Span<byte> sizes = stackalloc byte[LocalSizesLength];
            var sizeFields = new FieldWriter(sizes);
            sizeFields.UInt32(entry.Crc);
            sizeFields.UInt32(entry.CompressedLength);
            sizeFields.UInt32(entry.Length);
            RandomAccess.Write(_handle, sizes, offset + LocalCrcOffset);
            _entries.Add(entry);
        }

        /// <summary>Packs a time into the DOS time and date fields.</summary>
        /// <param name="time">The UTC time.</param>
        /// <returns>The time in the low word and the date in the high word.</returns>
        private static uint DosTime(DateTime time)
        {
            const int SecondsPerStep = 2;
            const int HourShift = 11;
            const int MinuteShift = 5;
            const int YearShift = 9;
            const int MonthShift = 5;
            const int DateShift = 16;
            if (time.Year < DosFirstYear)
            {
                time = new(DosFirstYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }

            var year = Math.Min(time.Year, DosLastYear) - DosFirstYear;
            var dosTime = (uint)((time.Hour << HourShift) | (time.Minute << MinuteShift) | (time.Second / SecondsPerStep));
            var dosDate = (uint)((year << YearShift) | (time.Month << MonthShift) | time.Day);
            return dosTime | (dosDate << DateShift);
        }

        /// <summary>Compresses one block with a fresh dictionary and ends it with a sync flush, so it inflates on its own.</summary>
        /// <param name="block">The uncompressed block.</param>
        /// <returns>The compressed bytes.</returns>
        /// <exception cref="InvalidDataException">The encoder did not take the whole block.</exception>
        private ReadOnlySpan<byte> Deflate(ReadOnlySpan<byte> block)
        {
            _encoder.Reset();
            var status = _encoder.Compress(block, _compressed, out var consumed, out var written, isFinalBlock: false);
            Require(status == OperationStatus.Done && consumed == block.Length, $"DEFLATE stopped with {status}");
            status = _encoder.Flush(_compressed.AsSpan(written), out var flushed);
            Require(status == OperationStatus.Done, $"DEFLATE flush stopped with {status}");
            return _compressed.AsSpan(0, written + flushed);
        }

        /// <summary>Writes the empty final block that ends an entry's DEFLATE stream.</summary>
        /// <returns>The compressed bytes.</returns>
        private ReadOnlySpan<byte> Finish()
        {
            _encoder.Reset();
            var status = _encoder.Compress([], _compressed, out _, out var written, isFinalBlock: true);
            Require(status == OperationStatus.Done, $"DEFLATE end stopped with {status}");
            return _compressed.AsSpan(0, written);
        }

        /// <summary>Writes bytes at the current offset.</summary>
        /// <param name="bytes">The bytes.</param>
        private void Append(ReadOnlySpan<byte> bytes)
        {
            RandomAccess.Write(_handle, bytes, _position);
            _position += bytes.Length;
        }

        /// <summary>Writes the central directory and the ZIP64 end records Windows expects of signed packages of any size.</summary>
        private void WriteCentralDirectory()
        {
            var start = _position;
            Span<byte> header = stackalloc byte[CentralHeaderLength];
            foreach (var entry in _entries)
            {
                var fields = new FieldWriter(header);
                fields.UInt32(CentralSignature);
                fields.UInt16(ZipVersion);
                fields.UInt16(ZipVersion);
                fields.UInt16(Utf8Flag);
                fields.UInt16(DeflateMethod);
                fields.UInt32(_dosTime);
                fields.UInt32(entry.Crc);
                fields.UInt32(entry.CompressedLength);
                fields.UInt32(entry.Length);
                fields.UInt16((ushort)entry.Name.Length);

                // No extra field, comment, disk number, internal or external attributes.
                fields.UInt16(0);
                fields.UInt16(0);
                fields.UInt16(0);
                fields.UInt16(0);
                fields.UInt32(0);
                fields.UInt32(entry.Offset);
                Append(header);
                Append(entry.Name);
            }

            var directoryLength = _position - start;
            var zip64End = _position;
            Span<byte> trailer = stackalloc byte[TrailerLength];
            var trailerFields = new FieldWriter(trailer);
            trailerFields.UInt32(Zip64EndSignature);
            trailerFields.UInt64(Zip64EndRecordLength);
            trailerFields.UInt16(Zip64Version);
            trailerFields.UInt16(Zip64Version);
            trailerFields.UInt32(0);
            trailerFields.UInt32(0);
            trailerFields.UInt64((ulong)_entries.Count);
            trailerFields.UInt64((ulong)_entries.Count);
            trailerFields.UInt64((ulong)directoryLength);
            trailerFields.UInt64((ulong)start);

            // The locator points at the ZIP64 end record on disk 0 of 1.
            trailerFields.UInt32(Zip64LocatorSignature);
            trailerFields.UInt32(0);
            trailerFields.UInt64((ulong)zip64End);
            trailerFields.UInt32(1);

            // The classic end record defers its counts and offsets to the ZIP64 record.
            trailerFields.UInt32(EndSignature);
            trailerFields.UInt16(0);
            trailerFields.UInt16(0);
            trailerFields.UInt16(ushort.MaxValue);
            trailerFields.UInt16(ushort.MaxValue);
            trailerFields.UInt32(uint.MaxValue);
            trailerFields.UInt32(uint.MaxValue);
            trailerFields.UInt16(0);
            Append(trailer);
            RandomAccess.SetLength(_handle, _position);
        }

        /// <summary>The values the central directory repeats for one entry.</summary>
        /// <param name="Name">The UTF-8 entry name.</param>
        /// <param name="Crc">The CRC-32 of the uncompressed data.</param>
        /// <param name="CompressedLength">The compressed size.</param>
        /// <param name="Length">The uncompressed size.</param>
        /// <param name="Offset">The local header offset.</param>
        private readonly record struct Entry(byte[] Name, uint Crc, uint CompressedLength, uint Length, uint Offset);
    }

    /// <summary>The identity and display values of the app.</summary>
    /// <param name="Identity">The package identity name.</param>
    /// <param name="Publisher">The publisher distinguished name.</param>
    /// <param name="PackageVersion">The four-part package version.</param>
    /// <param name="Arch">The processor architecture.</param>
    /// <param name="DisplayName">The app name shown to people.</param>
    /// <param name="PublisherDisplayName">The publisher name shown to people.</param>
    /// <param name="Description">The app description.</param>
    /// <param name="AppId">The application Id.</param>
    /// <param name="Executable">The executable path with backslashes.</param>
    /// <param name="Logo44">The 44x44 logo path.</param>
    /// <param name="Logo150">The 150x150 logo path.</param>
    /// <param name="StoreLogo">The store logo path.</param>
    private sealed record AppInfo(
        string Identity,
        string Publisher,
        string PackageVersion,
        string Arch,
        string DisplayName,
        string PublisherDisplayName,
        string Description,
        string AppId,
        string Executable,
        string Logo44,
        string Logo150,
        string StoreLogo);
}
