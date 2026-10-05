#:package OpenMcdf

// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;
using static System.Environment;

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

return Program.Run();

/// <summary>Writes a Windows Installer package in managed code: tables, string pool, summary information and an MSZIP cabinet in a compound file.</summary>
internal static partial class Program
{
    /// <summary>Column type bit: the column is part of the primary key.</summary>
    private const int TypeKey = 0x2000;

    /// <summary>Bit position of <see cref="TypeKey"/>.</summary>
    private const int KeyShift = 13;

    /// <summary>Column type bit: the column accepts null.</summary>
    private const int TypeNullable = 0x1000;

    /// <summary>Column type for a string; the low byte holds the width.</summary>
    private const int TypeString = 0x0D00;

    /// <summary>Column type for a 16-bit integer.</summary>
    private const int TypeShort = 0x0502;

    /// <summary>Column type for a 32-bit integer.</summary>
    private const int TypeLong = 0x0104;

    /// <summary>Column type for a binary stream.</summary>
    private const int TypeBinary = 0x0900;

    /// <summary>Bit that marks string and binary column types.</summary>
    private const int TypeStringBit = 0x0800;

    /// <summary>Mask for the width or size in a column type.</summary>
    private const int TypeSizeMask = 0xFF;

    /// <summary>Stored offset that keeps 16-bit integers apart from null.</summary>
    private const int ShortBias = 0x8000;

    /// <summary>Stored offset that keeps 32-bit integers apart from null.</summary>
    private const uint LongBias = 0x80000000;

    /// <summary>Bytes in a string reference or 16-bit value.</summary>
    private const int ShortWidth = 2;

    /// <summary>Bytes in a 32-bit value.</summary>
    private const int LongWidth = 4;

    /// <summary>Code page of the string pool and summary information.</summary>
    private const int Codepage = 1252;

    /// <summary>Most strings a pool with 16-bit references can hold.</summary>
    private const int MaximumStrings = 0xFFFF;

    /// <summary>String pool entries taken by a string longer than 65535 bytes.</summary>
    private const int LongStringEntries = 2;

    /// <summary>Bit position of the high word of a long string length.</summary>
    private const int HighWordShift = 16;

    /// <summary>First character of a table stream name.</summary>
    private const char TableStreamPrefix = '䡀';

    /// <summary>Base of a stream-name character holding two encoded characters.</summary>
    private const int PairBase = 0x3800;

    /// <summary>Base of a stream-name character holding one encoded character.</summary>
    private const int SingleBase = 0x4800;

    /// <summary>Bits per encoded stream-name character.</summary>
    private const int NameBits = 6;

    /// <summary>Encoded value of '.' in stream names.</summary>
    private const int NameDot = 62;

    /// <summary>Encoded value of '_' in stream names.</summary>
    private const int NameUnderscore = 63;

    /// <summary>Encoded value of the first lower case letter in stream names.</summary>
    private const int NameLowerBase = 36;

    /// <summary>Encoded value of the first upper case letter in stream names.</summary>
    private const int NameUpperBase = 10;

    /// <summary>Longest compound file entry name, in UTF-16 characters.</summary>
    private const int MaximumEntryName = 31;

    /// <summary>Little-endian byte order mark of a property set.</summary>
    private const ushort ByteOrderMark = 0xFFFE;

    /// <summary>Property type: code page string.</summary>
    private const uint VtLpstr = 30;

    /// <summary>Property set header size: byte order, format, system, class Id, section count, format Id and section offset.</summary>
    private const int SummaryHeaderSize = 48;

    /// <summary>Section header size: the section size and the property count.</summary>
    private const int SectionHeaderSize = 8;

    /// <summary>Bytes per property in the section's Id and offset list.</summary>
    private const int PropertyEntrySize = 8;

    /// <summary>Windows version recorded in the summary information header.</summary>
    private const uint SummarySystem = 0x0002000A;

    /// <summary>Summary word count bit: files are compressed.</summary>
    private const int WordCompressed = 2;

    /// <summary>Summary word count bit: no elevation needed.</summary>
    private const int WordNoElevation = 8;

    /// <summary>Cabinet stream name inside the package.</summary>
    private const string CabinetName = "app.cab";

    /// <summary>Icon table key and stream suffix.</summary>
    private const string IconName = "AppIcon.ico";

    /// <summary>Install folder directory key.</summary>
    private const string InstallFolder = "INSTALLFOLDER";

    /// <summary>The single feature.</summary>
    private const string MainFeature = "Main";

    /// <summary>Directory table name.</summary>
    private const string DirectoryTable = "Directory";

    /// <summary>Component table name.</summary>
    private const string ComponentTable = "Component";

    /// <summary>Registry table name.</summary>
    private const string RegistryTable = "Registry";

    /// <summary>Property table name.</summary>
    private const string PropertyTable = "Property";

    /// <summary>Control table name.</summary>
    private const string ControlTable = "Control";

    /// <summary>Highest major or minor ProductVersion number.</summary>
    private const int MaximumMajor = 255;

    /// <summary>Highest build ProductVersion number.</summary>
    private const int MaximumBuild = 65_535;

    /// <summary>Parts read from a semantic version.</summary>
    private const int VersionParts = 3;

    /// <summary>Bytes in a GUID.</summary>
    private const int GuidLength = 16;

    /// <summary>GUID byte that holds the version nibble.</summary>
    private const int GuidVersionByte = 7;

    /// <summary>GUID byte that holds the variant bits.</summary>
    private const int GuidVariantByte = 8;

    /// <summary>CAB uncompressed data block size.</summary>
    private const int CabBlockLength = 32_768;

    /// <summary>CAB header size, before the folder record.</summary>
    private const int CabHeaderLength = 36;

    /// <summary>CAB folder record size.</summary>
    private const int CabFolderLength = 8;

    /// <summary>CAB file record size, before the name.</summary>
    private const int CabFileLength = 16;

    /// <summary>CAB data block header size.</summary>
    private const int CabDataHeaderLength = 8;

    /// <summary>CAB header field holding the cabinet size.</summary>
    private const int CabSizeOffset = 8;

    /// <summary>CAB header fields after the signature: reserved, the cabinet size, reserved.</summary>
    private const int CabSizeFieldsLength = 12;

    /// <summary>CAB header fields after the file count: flags, set Id and cabinet index.</summary>
    private const int CabSetFieldsLength = 6;

    /// <summary>CAB format version 1.3: the minor version byte, then the major.</summary>
    private const ushort CabVersion = 0x0103;

    /// <summary>CAB folder compression type: MSZIP.</summary>
    private const ushort CabMsZip = 1;

    /// <summary>Largest MSZIP block Windows accepts: the data plus 12 bytes, including the CK marker.</summary>
    private const int CabMaximumCompressed = CabBlockLength + 12;

    /// <summary>Bytes in the MSZIP CK marker.</summary>
    private const int CkLength = 2;

    /// <summary>Header of a stored DEFLATE block: the final-block bit, the stored type and the length and its complement.</summary>
    private const int StoredHeaderLength = 5;

    /// <summary>CAB file attributes: archive, with a UTF-8 name.</summary>
    private const ushort CabFileAttributes = 0xA0;

    /// <summary>DEFLATE level: the smallest output, since a package is built once and downloaded many times.</summary>
    private const int DeflateLevel = 9;

    /// <summary>Bits in a byte.</summary>
    private const int BitsPerByte = 8;

    /// <summary>First year a DOS timestamp holds.</summary>
    private const int DosFirstYear = 1980;

    /// <summary>Last year a DOS timestamp holds.</summary>
    private const int DosLastYear = 2107;

    /// <summary>The DOS date of 1 January 1980, used for earlier times.</summary>
    private const ushort DosFirstDate = 0x21;

    /// <summary>
    /// The sequence tables a row is in, by the letter in the sequence rows' last cell.
    /// E is InstallExecute, U InstallUI, A AdminUI, X AdminExecute and V AdvtExecute.
    /// </summary>
    private const string SequenceLetters = "EUAXV";

    /// <summary>
    /// The standard actions, one per line: action|condition|number|sequence letters.
    /// RemoveExistingProducts runs after InstallFinalize: costing skipped files the old product holds at the same version,
    /// so removing it first would delete them. With stable component Ids, only files the new version dropped are removed.
    /// </summary>
    private const string SequenceRows = """
        FindRelatedProducts||25|EU
        AppSearch||50|EU
        LaunchConditions||100|EU
        ValidateProductID||700|EU
        CostInitialize||800|EUAXV
        FileCost||900|EUAX
        CostFinalize||1000|EUAXV
        MigrateFeatureStates||1200|EU
        ExecuteAction||1300|UA
        InstallValidate||1400|EX
        InstallInitialize||1500|EX
        ProcessComponents||1600|E
        UnpublishFeatures||1800|E
        RemoveRegistryValues||2600|E
        RemoveShortcuts||3200|E
        RemoveFiles||3500|E
        InstallAdminPackage||3900|X
        InstallFiles||4000|EX
        CreateShortcuts||4500|E
        WriteRegistryValues||5000|E
        RegisterUser||6000|E
        RegisterProduct||6100|E
        PublishFeatures||6300|EV
        PublishProduct||6400|EV
        InstallFinalize||6600|EX
        RemoveExistingProducts|PREVIOUSVERSIONSINSTALLED|6700|E
        """;

    /// <summary>
    /// One modal dialog that shows the license and enables Install once it is accepted; the license text row is added in code.
    /// Dialog attributes 3 are visible and modal; control attributes 3 are visible and enabled.
    /// </summary>
    private const string LicenseRows = """
        Property|DefaultUIFont|DefaultFont
        TextStyle|DefaultFont|Segoe UI|9||
        Dialog|LicenseDlg|50|50|370|270|3|[ProductName] Setup|LicenseText|Install|Cancel
        Control|LicenseDlg|Heading|Text|15|10|340|15|3||Please read the license agreement.||
        Control|LicenseDlg|Accept|CheckBox|15|207|340|15|3|LicenseAccepted|I &accept the terms in the license agreement|Install|
        Control|LicenseDlg|Install|PushButton|220|240|66|17|3||&Install|Cancel|
        Control|LicenseDlg|Cancel|PushButton|290|240|66|17|3||Cancel|LicenseText|
        CheckBox|LicenseAccepted|1
        ControlEvent|LicenseDlg|Install|EndDialog|Return|LicenseAccepted = "1"|1
        ControlEvent|LicenseDlg|Cancel|EndDialog|Exit|1|1
        ControlCondition|LicenseDlg|Install|Disable|LicenseAccepted <> "1"
        ControlCondition|LicenseDlg|Install|Enable|LicenseAccepted = "1"
        InstallUISequence|LicenseDlg|NOT Installed|1250
        """;

    /// <summary>The sequence table for each letter in <see cref="SequenceLetters"/>.</summary>
    private static readonly string[] SequenceTables = ["InstallExecuteSequence", "InstallUISequence", "AdminUISequence", "AdminExecuteSequence", "AdvtExecuteSequence"];

    /// <summary>Class Id of a Windows Installer package; msi.dll refuses a root storage without it.</summary>
    private static readonly Guid InstallerPackageClass = new("000C1084-0000-0000-C000-000000000046");

    /// <summary>Format Id of the summary information property set.</summary>
    private static readonly Guid SummaryFormat = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

    /// <summary>Characters that would break a text row: the cell separator and line breaks.</summary>
    private static readonly SearchValues<char> RowBreaks = SearchValues.Create("|\r\n");

    /// <summary>Characters Windows does not allow in a folder name.</summary>
    private static readonly SearchValues<char> InvalidNameCharacters = SearchValues.Create("<>:\"/\\|?*");

    /// <summary>Builds the installer from the action inputs.</summary>
    /// <returns>The process exit code.</returns>
    internal static int Run()
    {
        try
        {
            Build();
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or OverflowException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"::error::{ex.Message}");
            return 1;
        }
    }

    /// <summary>Checks the inputs, then writes the package, or prints the tables for a dry run.</summary>
    private static void Build()
    {
        var source = Path.GetFullPath(Required("MSI_SOURCE"));
        var output = Path.GetFullPath(Required("MSI_OUTPUT"));
        Require(Directory.Exists(source), $"source folder not found: {source}");
        var files = Payload(source);
        var settings = ReadSettings(files);
        var tables = Populate(settings, files);
        if (string.Equals(Input("MSI_DRY_RUN"), "true", StringComparison.OrdinalIgnoreCase))
        {
            PrintTables(tables);
            Console.WriteLine($"Dry run: checked {files.Count} files; {output} was not written.");
            return;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            WritePackage(tables, files, settings, output);
        }
        catch
        {
            // A half-written package must not be mistaken for a build result.
            File.Delete(output);
            throw;
        }

        Console.WriteLine($"Built {output}: {files.Count} files.");
    }

    /// <summary>Reads an action input from its environment variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The trimmed value, or empty.</returns>
    private static string Input(string name) => GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

    /// <summary>Reads a required action input.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value.</returns>
    private static string Required(string name)
    {
        var value = Input(name);
        Require(value.Length > 0, $"{name} is required");
        return value;
    }

    /// <summary>Checks that a value can sit in a text row: no cell separator or line break.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The checked value.</returns>
    private static string Cell(string value)
    {
        Require(!value.AsSpan().ContainsAny(RowBreaks), $"value must not contain '|' or a line break: {value}");
        return value;
    }

    /// <summary>Returns a value, or a fallback when it is empty.</summary>
    /// <param name="value">The value.</param>
    /// <param name="fallback">The fallback.</param>
    /// <returns>The value or the fallback.</returns>
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

    /// <summary>Reads an optional file input.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The full path, or null when the input is empty.</returns>
    private static string? OptionalFile(string name)
    {
        var path = Input(name);
        if (path.Length == 0)
        {
            return null;
        }

        Require(File.Exists(path), $"file not found: {path}");
        return Path.GetFullPath(path);
    }

    /// <summary>Matches a ProgID: dot-separated letters and digits that start with a letter.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProgIdPattern();

    /// <summary>Reads and checks the package settings.</summary>
    /// <param name="files">The payload files.</param>
    /// <returns>The settings.</returns>
    private static Settings ReadSettings(List<PayloadFile> files)
    {
        var product = Cell(Required("MSI_PRODUCT"));
        Require(Guid.TryParse(Input("MSI_UPGRADE_CODE"), out var upgradeCode), "upgrade-code must be a GUID");
        var arch = Input("MSI_ARCH");
        Require(arch is "x64" or "arm64", "architecture must be x64 or arm64");
        var scope = Input("MSI_SCOPE");
        Require(scope is "perUser" or "perMachine", "install-scope must be perUser or perMachine");
        var fileTypes = Extensions(Input("MSI_FILE_TYPES"));
        var progId = Fallback(Input("MSI_PROG_ID"), $"{NotLetterOrDigit().Replace(product, string.Empty)}.Document");
        Require(fileTypes.Length == 0 || ProgIdPattern().IsMatch(progId), $"prog-id must be dot-separated letters and digits: {progId}");
        return new(
            product,
            Cell(Required("MSI_MANUFACTURER")),
            ProductVersion(Required("MSI_VERSION")),
            upgradeCode.ToString("B").ToUpperInvariant(),
            arch,
            scope == "perUser",
            FolderNames(Fallback(Input("MSI_FOLDER"), product)),
            FindFile(files, Required("MSI_EXECUTABLE")),
            Cell(Fallback(Input("MSI_SHORTCUT"), product)),
            OptionalFile("MSI_ICON"),
            Cell(Input("MSI_HOMEPAGE")),
            fileTypes,
            Cell(Fallback(Input("MSI_FILE_TYPE_NAME"), $"{product} document")),
            progId,
            OptionalFile("MSI_LICENSE"));
    }

    /// <summary>Matches every character that is not an ASCII letter or digit.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("[^A-Za-z0-9]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotLetterOrDigit();

    /// <summary>Splits and checks the install folder.</summary>
    /// <param name="folder">The folder, such as Company\App.</param>
    /// <returns>The folder names.</returns>
    private static string[] FolderNames(string folder)
    {
        var names = folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(names.Length > 0, "install-folder must name a folder");
        foreach (var name in names)
        {
            var valid = name is not ("." or "..") && !name.AsSpan().ContainsAny(InvalidNameCharacters) && !name.AsSpan().ContainsAnyInRange('\0', '\u001F');
            Require(valid, $"install-folder has an invalid folder name: {name}");
        }

        return names;
    }

    /// <summary>Normalises the file type extensions.</summary>
    /// <param name="input">Extensions, one per line.</param>
    /// <returns>Distinct lower case extensions with a leading dot.</returns>
    private static string[] Extensions(string input)
    {
        var extensions = new List<string>();
        foreach (var line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = (line.StartsWith('.') ? line : $".{line}").ToLowerInvariant();
            Require(extension.Length > 1 && !extension.AsSpan().ContainsAny(InvalidNameCharacters) && !extension.Contains(' ', StringComparison.Ordinal), $"invalid file type: {line}");
            if (!extensions.Contains(extension))
            {
                extensions.Add(extension);
            }
        }

        return [.. extensions];
    }

    /// <summary>Lists the files to install in ordinal name order.</summary>
    /// <param name="source">The app folder.</param>
    /// <returns>Each file's path, relative name and length.</returns>
    private static List<PayloadFile> Payload(string source)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        var files = new List<PayloadFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(source, "*", options))
        {
            var name = Path.GetRelativePath(source, path).Replace('\\', '/');
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                // Following a link could pull in a file from outside the source, such as an absolute path on the build machine.
                Console.WriteLine($"::warning::skipped symbolic link {name}; Windows Installer cannot hold links");
                continue;
            }

            // Windows paths are case-insensitive, so two such files would install over each other.
            Require(seen.Add(name), $"two files differ only by case: {name}");
            files.Add(new(path, name, info.Length));
        }

        Require(files.Count > 0, $"source folder is empty: {source}");
        files.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return files;
    }

    /// <summary>Finds a payload file by relative path.</summary>
    /// <param name="files">The payload files.</param>
    /// <param name="relative">The relative path.</param>
    /// <returns>The file's index.</returns>
    /// <exception cref="InvalidDataException">The file is not in the payload.</exception>
    private static int FindFile(List<PayloadFile> files, string relative)
    {
        var name = relative.Replace('\\', '/').TrimStart('/');
        for (var i = 0; i < files.Count; i++)
        {
            if (string.Equals(files[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidDataException($"{relative} is not in the source folder");
    }

    /// <summary>Converts a semantic version to the three-part ProductVersion a.b.c.</summary>
    /// <param name="version">The semantic version.</param>
    /// <returns>The ProductVersion.</returns>
    /// <exception cref="FormatException">The version is out of range or has too many numbers.</exception>
    private static string ProductVersion(string version)
    {
        var parts = version.TrimStart('v', 'V').Split('-', '+')[0].Split('.');
        if (parts.Length > VersionParts)
        {
            throw new FormatException($"version must be major[.minor[.patch]] with an optional suffix: {version}");
        }

        Span<int> numbers = stackalloc int[VersionParts];
        for (var i = 0; i < parts.Length; i++)
        {
            numbers[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (numbers[0] > MaximumMajor || numbers[1] > MaximumMajor || numbers[2] > MaximumBuild)
        {
            throw new FormatException($"Windows Installer versions are limited to 255.255.65535: {version}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{numbers[0]}.{numbers[1]}.{numbers[2]}");
    }

    /// <summary>Makes a name-based GUID, so the same name always gives the same Id.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The braced upper case GUID.</returns>
    private static string StableGuid(string name)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(Encoding.UTF8.GetBytes(name), hash);
        hash[GuidVersionByte] = (byte)((hash[GuidVersionByte] & 0x0F) | 0x50);
        hash[GuidVariantByte] = (byte)((hash[GuidVariantByte] & 0x3F) | 0x80);
        return new Guid(hash[..GuidLength]).ToString("B").ToUpperInvariant();
    }

    /// <summary>Fills the installer tables.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="files">The payload files.</param>
    /// <returns>The tables.</returns>
    private static List<Table> Populate(Settings settings, List<PayloadFile> files)
    {
        var tables = new List<Table>();
        var root = settings.PerUser ? "LocalAppDataFolder" : "ProgramFiles64Folder";
        var productCode = StableGuid($"product|{settings.UpgradeCode}|{settings.PerUser}|{settings.Arch}|{settings.Version}");
        var icon = settings.Icon is null ? string.Empty : IconName;

        // Upgrade attributes 1 migrate features from older versions; 258 only detect the same or a newer version.
        AddRows(tables, string.Create(CultureInfo.InvariantCulture, $$"""
            Directory|TARGETDIR||SourceDir
            Directory|{{root}}|TARGETDIR|.
            Directory|ProgramMenuFolder|TARGETDIR|.
            Media|1|{{files.Count}}||#{{CabinetName}}||
            Feature|{{MainFeature}}||{{settings.Product}}||1|1|{{InstallFolder}}|0
            Shortcut|AppShortcut|ProgramMenuFolder|{{settings.ShortcutName}}|c{{settings.ExeIndex}}|[#f{{settings.ExeIndex}}]||{{settings.Product}}||{{icon}}|||{{InstallFolder}}
            Property|ProductCode|{{productCode}}
            Property|ProductName|{{settings.Product}}
            Property|ProductVersion|{{settings.Version}}
            Property|Manufacturer|{{settings.Manufacturer}}
            Property|ProductLanguage|1033
            Property|UpgradeCode|{{settings.UpgradeCode}}
            Property|ALLUSERS|{{(settings.PerUser ? string.Empty : "1")}}
            Property|ARPPRODUCTICON|{{icon}}
            Property|ARPURLINFOABOUT|{{settings.Homepage}}
            Property|ARPNOMODIFY|1
            Property|SecureCustomProperties|NEWERPRODUCTFOUND;PREVIOUSVERSIONSINSTALLED
            Upgrade|{{settings.UpgradeCode}}||{{settings.Version}}||1||PREVIOUSVERSIONSINSTALLED
            Upgrade|{{settings.UpgradeCode}}|{{settings.Version}}|||258||NEWERPRODUCTFOUND
            LaunchCondition|Installed OR NOT NEWERPRODUCTFOUND|A newer version of [ProductName] is already installed.
            """));
        foreach (var line in SequenceRows.Split('\n', StringSplitOptions.TrimEntries))
        {
            var cells = line.Split('|');
            foreach (var letter in cells[3])
            {
                Insert(tables, SequenceTables[SequenceLetters.IndexOf(letter, StringComparison.Ordinal)], cells[0], cells[1], cells[2]);
            }
        }

        AddFiles(tables, settings, files, AddDirectories(tables, settings, files, root));
        if (settings.Icon is { } iconPath)
        {
            Insert(tables, "Icon", IconName, new FileInfo(iconPath));
        }

        AddFileTypes(tables, settings);
        if (settings.License is { } license)
        {
            AddRows(tables, LicenseRows);
            Insert(tables, ControlTable, "LicenseDlg", "LicenseText", "ScrollableText", "15", "30", "340", "170", "7", null, File.ReadAllText(license, Encoding.Latin1), "Accept", null);
        }

        return tables;
    }

    /// <summary>Adds rows written one per line as table|value|value; an empty value is null and a Property row without a value is left out.</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="rows">The rows.</param>
    private static void AddRows(List<Table> tables, string rows)
    {
        foreach (var line in rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cells = line.Split('|');
            if (cells[0] != PropertyTable || cells[2].Length > 0)
            {
                Insert(tables, cells[0], [.. cells.AsSpan(1)]);
            }
        }
    }

    /// <summary>Adds the install folder chain and every folder under the source.</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="files">The payload files.</param>
    /// <param name="root">The directory key the install folder chain starts from.</param>
    /// <returns>Directory keys by relative folder.</returns>
    private static Dictionary<string, string> AddDirectories(List<Table> tables, Settings settings, List<PayloadFile> files, string root)
    {
        var parent = root;
        if (settings.PerUser)
        {
            Insert(tables, DirectoryTable, "UserPrograms", root, "Programs");
            parent = "UserPrograms";
        }

        for (var i = 0; i < settings.Folder.Length; i++)
        {
            var key = i == settings.Folder.Length - 1 ? InstallFolder : $"i{i}";
            Insert(tables, DirectoryTable, key, parent, settings.Folder[i]);
            parent = key;
        }

        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = InstallFolder };
        foreach (var file in files)
        {
            var path = string.Empty;
            var segments = file.Name.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var child = path.Length == 0 ? segments[i] : $"{path}/{segments[i]}";
                if (directories.TryAdd(child, $"d{directories.Count}"))
                {
                    Insert(tables, DirectoryTable, directories[child], directories[path], segments[i]);
                }

                path = child;
            }
        }

        return directories;
    }

    /// <summary>Adds a component and a file row for every payload file.</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="files">The payload files.</param>
    /// <param name="directories">Directory keys by relative folder.</param>
    private static void AddFiles(List<Table> tables, Settings settings, List<PayloadFile> files, Dictionary<string, string> directories)
    {
        var location = $"{settings.UpgradeCode}|{settings.PerUser}|{string.Join('/', settings.Folder)}";
        for (var i = 0; i < files.Count; i++)
        {
            var name = files[i].Name;
            var slash = name.LastIndexOf('/');
            var folder = slash >= 0 ? name[..slash] : string.Empty;

            // Component Ids are stable per installed path, so an upgrade keeps the files both versions share.
            var componentId = StableGuid($"component|{location}|{name.ToUpperInvariant()}");
            Insert(tables, ComponentTable, $"c{i}", componentId, directories[folder], "256", null, $"f{i}");

            // File sequence numbers follow the cabinet order.
            Insert(tables, nameof(File), $"f{i}", $"c{i}", name[(slash + 1)..], checked((int)files[i].Length), null, null, 0, i + 1);
            Insert(tables, "FeatureComponents", MainFeature, $"c{i}");
        }
    }

    /// <summary>Registers the file types under a ProgID that opens them with the executable.</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="settings">The settings.</param>
    private static void AddFileTypes(List<Table> tables, Settings settings)
    {
        if (settings.FileTypes.Length == 0)
        {
            return;
        }

        // Root -1 writes under HKCU for a per-user install and HKLM for a per-machine one.
        var (classes, exe, component) = ($@"Software\Classes\{settings.ProgId}", $"[#f{settings.ExeIndex}]", $"c{settings.ExeIndex}");
        AddRows(tables, $"""
            Registry|ProgId|-1|{classes}||{settings.FileTypeName}|{component}
            Registry|ProgIdIcon|-1|{classes}\DefaultIcon||{exe},0|{component}
            Registry|ProgIdOpen|-1|{classes}\shell\open\command||"{exe}" "%1"|{component}
            """);
        for (var i = 0; i < settings.FileTypes.Length; i++)
        {
            // Windows reads only the value name under OpenWithProgids; an empty value cannot be stored, so the name is repeated.
            var key = $@"Software\Classes\{settings.FileTypes[i]}\OpenWithProgids";
            Insert(tables, RegistryTable, $"OpenWith{i}", "-1", key, settings.ProgId, settings.ProgId, component);
        }
    }

    /// <summary>Gives a table's columns in IDT notation: a star marks a key, upper case marks a nullable column.</summary>
    /// <param name="table">The table name.</param>
    /// <returns>The column list.</returns>
    /// <exception cref="InvalidOperationException">The table is unknown.</exception>
    private static string Schema(string table) => table switch
    {
        DirectoryTable => "*Directory s72, Directory_Parent S72, DefaultDir s255",
        ComponentTable => "*Component s72, ComponentId S38, Directory_ s72, Attributes i2, Condition S255, KeyPath S72",
        nameof(File) => "*File s72, Component_ s72, FileName s255, FileSize i4, Version S72, Language S20, Attributes I2, Sequence i4",
        "Feature" => "*Feature s38, Feature_Parent S38, Title S64, Description S255, Display I2, Level i2, Directory_ S72, Attributes i2",
        "FeatureComponents" => "*Feature_ s38, *Component_ s72",
        "Media" => "*DiskId i2, LastSequence i4, DiskPrompt S64, Cabinet S255, VolumeLabel S32, Source S72",
        PropertyTable => "*Property s72, Value s0",
        "Icon" => "*Name s72, Data v0",
        "Shortcut" => "*Shortcut s72, Directory_ s72, Name s128, Component_ s72, Target s72, Arguments S255, Description S255, Hotkey I2, Icon_ S72, IconIndex I2, ShowCmd I2, WkDir S72",
        RegistryTable => "*Registry s72, Root i2, Key s255, Name S255, Value S0, Component_ s72",
        "Upgrade" => "*UpgradeCode s38, *VersionMin S20, *VersionMax S20, *Language S255, *Attributes i4, Remove S255, ActionProperty s72",
        "LaunchCondition" => "*Condition s255, Description s255",
        "Dialog" => "*Dialog s72, HCentering i2, VCentering i2, Width i2, Height i2, Attributes I4, Title S128, Control_First s50, Control_Default S50, Control_Cancel S50",
        ControlTable => "*Dialog_ s72, *Control s50, Type s20, X i2, Y i2, Width i2, Height i2, Attributes I4, Property S72, Text S0, Control_Next S50, Help S50",
        "ControlEvent" => "*Dialog_ s72, *Control_ s50, *Event s50, *Argument s255, *Condition S255, Ordering I2",
        "ControlCondition" => "*Dialog_ s72, *Control_ s50, *Action s50, *Condition s255",
        "CheckBox" => "*Property s72, Value S64",
        "TextStyle" => "*TextStyle s72, FaceName s32, Size i2, Color I4, StyleBits I2",
        _ when table.EndsWith("Sequence", StringComparison.Ordinal) => "*Action s72, Condition S255, Sequence I2",
        _ => throw new InvalidOperationException($"no schema for {table}"),
    };

    /// <summary>Parses a column list in IDT notation.</summary>
    /// <param name="schema">The column list.</param>
    /// <returns>Each column's name and stored type.</returns>
    private static Column[] ParseColumns(string schema)
    {
        var parts = schema.Split(',', StringSplitOptions.TrimEntries);
        var columns = new Column[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var space = parts[i].IndexOf(' ', StringComparison.Ordinal);
            var name = parts[i][..space];
            var spec = parts[i].AsSpan(space + 1);
            var size = int.Parse(spec[1..], NumberStyles.None, CultureInfo.InvariantCulture);
            var type = char.ToLowerInvariant(spec[0]) switch
            {
                's' or 'l' => TypeString | size,
                'i' => size == ShortWidth ? TypeShort : TypeLong,
                _ => TypeBinary,
            };
            var flags = (name.StartsWith('*') ? TypeKey : 0) | (char.IsUpper(spec[0]) ? TypeNullable : 0);
            columns[i] = new(name.TrimStart('*'), type | flags);
        }

        return columns;
    }

    /// <summary>Adds a row, creating the table on first use.</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="tableName">The table.</param>
    /// <param name="values">The values in column order: strings, integers or integer text, a FileInfo for a stream, or null.</param>
    /// <exception cref="InvalidOperationException">The row does not fit the table.</exception>
    private static void Insert(List<Table> tables, string tableName, params object?[] values)
    {
        var table = FindTable(tables, tableName);
        if (table.Columns.Length != values.Length)
        {
            throw new InvalidOperationException($"{tableName} has {table.Columns.Length} columns but the row has {values.Length} values");
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = CellValue(table.Columns[i].Type, values[i]);
            if (values[i] is null && (table.Columns[i].Type & TypeNullable) == 0)
            {
                throw new InvalidOperationException($"{tableName}.{table.Columns[i].Name} must not be null");
            }
        }

        table.Rows.Add(values);
    }

    /// <summary>Normalises a cell: an empty string is null, as Windows Installer stores it, and integer text becomes an integer.</summary>
    /// <param name="type">The column type.</param>
    /// <param name="value">The value.</param>
    /// <returns>The stored value.</returns>
    private static object? CellValue(int type, object? value) => value switch
    {
        string { Length: 0 } => null,
        string text when (type & TypeStringBit) == 0 => int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => value,
    };

    /// <summary>Finds a table, creating it from its schema on first use.</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="tableName">The table name.</param>
    /// <returns>The table.</returns>
    private static Table FindTable(List<Table> tables, string tableName)
    {
        foreach (var table in tables)
        {
            if (table.Name == tableName)
            {
                return table;
            }
        }

        var created = new Table(tableName, ParseColumns(Schema(tableName)), []);
        tables.Add(created);
        return created;
    }

    /// <summary>Prints every table row for a dry run.</summary>
    /// <param name="tables">The tables.</param>
    private static void PrintTables(List<Table> tables)
    {
        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                // Null prints as an empty cell and a stream as its file path.
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{table.Name}|{string.Join('|', row)}"));
            }
        }
    }

    /// <summary>Gets the Windows-1252 encoding, failing on characters it cannot hold.</summary>
    /// <returns>The encoding.</returns>
    private static Encoding PoolEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(Codepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>Writes the summary information, the cabinet, the tables and the string pool as a compound file.</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="files">The payload files.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="output">The package path.</param>
    private static void WritePackage(List<Table> tables, List<PayloadFile> files, Settings settings, string output)
    {
        var encoding = PoolEncoding();
        var pool = new StringPool(encoding);
        using var root = RootStorage.Create(output);
        root.CLSID = InstallerPackageClass;
        WriteStream(root, "\u0005SummaryInformation", Summary(settings, encoding));
        using (var cabinet = root.CreateStream(EncodeName(CabinetName)))
        {
            WriteCabinet(files, cabinet);
        }

        var tableList = new Table("_Tables", ParseColumns("*Name s64"), []);
        var columnList = new Table("_Columns", ParseColumns("*Table s64, *Number i2, Name s64, Type i2"), []);
        foreach (var table in tables)
        {
            tableList.Rows.Add([table.Name]);
            for (var i = 0; i < table.Columns.Length; i++)
            {
                columnList.Rows.Add([table.Name, i + 1, table.Columns[i].Name, table.Columns[i].Type]);
            }
        }

        foreach (var table in (Table[])[tableList, columnList, .. tables])
        {
            WriteStream(root, TableStreamPrefix + EncodeName(table.Name), TableData(table, pool));
            WriteBinaryStreams(root, table);
        }

        pool.Write(root);
    }

    /// <summary>Writes a stream under the root storage.</summary>
    /// <param name="root">The root storage.</param>
    /// <param name="name">The stored stream name.</param>
    /// <param name="data">The contents.</param>
    private static void WriteStream(RootStorage root, string name, ReadOnlySpan<byte> data)
    {
        using var stream = root.CreateStream(name);
        stream.Write(data);
    }

    /// <summary>Writes the stream behind each binary value, named after the table and the row's key.</summary>
    /// <param name="root">The root storage.</param>
    /// <param name="table">The table.</param>
    private static void WriteBinaryStreams(RootStorage root, Table table)
    {
        foreach (var row in table.Rows)
        {
            foreach (var value in row)
            {
                if (value is not FileInfo file)
                {
                    continue;
                }

                var name = new StringBuilder(table.Name);
                for (var key = 0; key < table.Columns.Length && (table.Columns[key].Type & TypeKey) != 0; key++)
                {
                    _ = name.Append('.').Append(Convert.ToString(row[key], CultureInfo.InvariantCulture));
                }

                using var stream = root.CreateStream(EncodeName(name.ToString()));
                using var content = new FileStream(file.FullName, new FileStreamOptions { Options = FileOptions.SequentialScan });
                content.CopyTo(stream);
            }
        }
    }

    /// <summary>Writes the summary information property set.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="encoding">The code page encoding for strings.</param>
    /// <returns>The stream contents.</returns>
    private static byte[] Summary(Settings settings, Encoding encoding)
    {
        // One property per line: Id|type|value. Type 2 is a 16-bit integer, 3 a 32-bit integer and 30 a code page string.
        // Schema 500 is the lowest that supports Arm64. The package code (Id 9) names this exact file, so it changes with every build.
        var words = WordCompressed | (settings.PerUser ? WordNoElevation : 0);
        var lines = string.Create(CultureInfo.InvariantCulture, $$"""
            1|2|{{Codepage}}
            2|30|Installation Database
            3|30|{{settings.Product}}
            4|30|{{settings.Manufacturer}}
            5|30|Installer
            6|30|{{settings.Product}} {{settings.Version}} installer
            7|30|{{(settings.Arch == "arm64" ? "Arm64" : "x64")}};1033
            9|30|{{Guid.NewGuid().ToString("B").ToUpperInvariant()}}
            14|3|500
            15|3|{{words}}
            18|30|Windows Installer
            19|3|2
            """).Split('\n', StringSplitOptions.TrimEntries);
        var values = new byte[lines.Length][];
        var sectionLength = SectionHeaderSize + (lines.Length * PropertyEntrySize);
        for (var i = 0; i < lines.Length; i++)
        {
            // A value is its type word then a 4-byte integer, or a length and null-terminated text padded to four bytes.
            var cells = lines[i].Split('|');
            values[i] = uint.Parse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture) == VtLpstr ? Encode(encoding, $"{cells[2]}\0") : [];
            sectionLength += sizeof(uint) + sizeof(uint) + Align(values[i].Length);
        }

        var summary = new byte[SummaryHeaderSize + sectionLength];
        var fields = new FieldWriter(summary);
        fields.UInt16(ByteOrderMark);
        fields.Skip(sizeof(ushort));
        fields.UInt32(SummarySystem);
        fields.Skip(GuidLength);
        fields.UInt32(1);
        fields.Bytes(SummaryFormat.ToByteArray());
        fields.UInt32(SummaryHeaderSize);
        fields.UInt32((uint)sectionLength);
        fields.UInt32((uint)lines.Length);
        var offset = SectionHeaderSize + (lines.Length * PropertyEntrySize);
        for (var i = 0; i < lines.Length; i++)
        {
            fields.UInt32(uint.Parse(lines[i].AsSpan(0, lines[i].IndexOf('|', StringComparison.Ordinal)), NumberStyles.None, CultureInfo.InvariantCulture));
            fields.UInt32((uint)offset);
            offset += sizeof(uint) + sizeof(uint) + Align(values[i].Length);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            WriteProperty(ref fields, lines[i].Split('|'), values[i]);
        }

        return summary;
    }

    /// <summary>Writes one property value: its type, then the integer or the length and padded text.</summary>
    /// <param name="fields">The writer.</param>
    /// <param name="cells">The Id, type and value.</param>
    /// <param name="text">The encoded, null-terminated text of a string value.</param>
    private static void WriteProperty(ref FieldWriter fields, string[] cells, byte[] text)
    {
        var type = uint.Parse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture);
        fields.UInt32(type);
        if (type != VtLpstr)
        {
            fields.UInt32(uint.Parse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture));
            return;
        }

        fields.UInt32((uint)text.Length);
        fields.Bytes(text);
        fields.Skip(Align(text.Length) - text.Length);
    }

    /// <summary>Encodes text in the package code page.</summary>
    /// <param name="encoding">The code page encoding.</param>
    /// <param name="text">The text.</param>
    /// <returns>The encoded bytes.</returns>
    /// <exception cref="InvalidDataException">The text has a character the code page cannot hold.</exception>
    private static byte[] Encode(Encoding encoding, string text)
    {
        try
        {
            return encoding.GetBytes(text);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException($"Windows Installer text must use Windows-1252 characters: {text.TrimEnd('\0')}", ex);
        }
    }

    /// <summary>Rounds a length up to a multiple of four.</summary>
    /// <param name="length">The length.</param>
    /// <returns>The aligned length.</returns>
    private static int Align(int length) => (length + sizeof(uint) - 1) & ~(sizeof(uint) - 1);

    /// <summary>Gives the stored width of a column: four bytes for 32-bit integers, else two.</summary>
    /// <param name="type">The column type.</param>
    /// <returns>The width in bytes.</returns>
    private static int ColumnWidth(int type) => (type & TypeStringBit) == 0 && (type & TypeSizeMask) == LongWidth ? LongWidth : ShortWidth;

    /// <summary>Serialises a table in column-major order, with rows sorted by their stored key values.</summary>
    /// <param name="table">The table.</param>
    /// <param name="pool">The string pool.</param>
    /// <returns>The table stream.</returns>
    private static byte[] TableData(Table table, StringPool pool)
    {
        // Key columns come first, so counting them gives the key prefix.
        var keys = 0;
        var rowWidth = 0;
        foreach (var column in table.Columns)
        {
            keys += (column.Type & TypeKey) >> KeyShift;
            rowWidth += ColumnWidth(column.Type);
        }

        var rows = new List<uint[]>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var stored = new uint[row.Length + 1];
            for (var i = 0; i < row.Length; i++)
            {
                stored[i] = StoredValue(table.Columns[i].Type, row[i], pool);
            }

            // The last slot carries the key count, so the sort comparison needs no captured state.
            stored[row.Length] = (uint)keys;
            rows.Add(stored);
        }

        rows.Sort(CompareKeys);
        var data = new byte[rowWidth * rows.Count];
        var offset = 0;
        for (var column = 0; column < table.Columns.Length; column++)
        {
            var width = ColumnWidth(table.Columns[column].Type);
            foreach (var stored in rows)
            {
                if (width == LongWidth)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), stored[column]);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), checked((ushort)stored[column]));
                }

                offset += width;
            }
        }

        return data;
    }

    /// <summary>Converts a value to its stored form: a string Id, a biased integer, or 1 for a stream.</summary>
    /// <param name="type">The column type.</param>
    /// <param name="value">The value.</param>
    /// <param name="pool">The string pool.</param>
    /// <returns>The stored value; 0 means null.</returns>
    /// <exception cref="InvalidOperationException">The value is not a string, an integer or a stream.</exception>
    private static uint StoredValue(int type, object? value, StringPool pool) => value switch
    {
        null => 0,
        FileInfo => 1,
        string text => (uint)pool.Intern(text),
        int number when (type & TypeSizeMask) == LongWidth => unchecked((uint)number ^ LongBias),
        int number => checked((uint)(number + ShortBias)),
        _ => throw new InvalidOperationException($"unsupported value {value}"),
    };

    /// <summary>Orders rows by their stored key values.</summary>
    /// <param name="left">A row, with the key count in its last slot.</param>
    /// <param name="right">Another row.</param>
    /// <returns>The comparison result.</returns>
    private static int CompareKeys(uint[] left, uint[] right)
    {
        var keys = (int)left[^1];
        for (var i = 0; i < keys; i++)
        {
            var result = left[i].CompareTo(right[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    /// <summary>Compresses a stream name the way Windows Installer does: two name characters per UTF-16 character.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The stored name.</returns>
    /// <exception cref="InvalidDataException">The stored name is too long.</exception>
    private static string EncodeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var first = NameValue(name[i]);
            var second = first >= 0 && i + 1 < name.Length ? NameValue(name[i + 1]) : -1;
            if (first < 0)
            {
                _ = builder.Append(name[i]);
            }
            else if (second < 0)
            {
                _ = builder.Append((char)(SingleBase + first));
            }
            else
            {
                _ = builder.Append((char)(PairBase + first + (second << NameBits)));
                i++;
            }
        }

        // Table streams gain a one-character prefix.
        Require(builder.Length < MaximumEntryName, $"stream name is too long: {name}");
        return builder.ToString();
    }

    /// <summary>Maps a character to its 6-bit stream name value.</summary>
    /// <param name="character">The character.</param>
    /// <returns>The value, or -1 when the character is stored as is.</returns>
    private static int NameValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'A' and <= 'Z' => character - 'A' + NameUpperBase,
        >= 'a' and <= 'z' => character - 'a' + NameLowerBase,
        '.' => NameDot,
        '_' => NameUnderscore,
        _ => -1,
    };

    /// <summary>Writes a single-folder MSZIP cabinet whose entries are named by File table key.</summary>
    /// <param name="files">The payload files, in File table sequence order.</param>
    /// <param name="cabinet">The cabinet stream.</param>
    private static void WriteCabinet(List<PayloadFile> files, Stream cabinet)
    {
        var headerLength = CabHeaderLength + CabFolderLength;
        long total = 0;
        var names = new byte[files.Count][];
        for (var i = 0; i < files.Count; i++)
        {
            // Entries are named by File table key, with a null terminator.
            names[i] = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"f{i}\0"));
            total += files[i].Length;
            headerLength += CabFileLength + names[i].Length;
        }

        Require(total <= uint.MaxValue, "the files are too large for one cabinet");
        var blocks = checked((ushort)((total + CabBlockLength - 1) / CabBlockLength));
        var header = new byte[headerLength];
        var fields = new FieldWriter(header);

        // The cabinet size, between two reserved fields, is written over once the blocks are compressed.
        fields.Bytes("MSCF"u8);
        fields.Skip(CabSizeFieldsLength);
        fields.UInt32(CabHeaderLength + CabFolderLength);
        fields.Skip(sizeof(uint));
        fields.UInt16(CabVersion);
        fields.UInt16(1);
        fields.UInt16(checked((ushort)files.Count));
        fields.Skip(CabSetFieldsLength);
        fields.UInt32((uint)headerLength);
        fields.UInt16(blocks);
        fields.UInt16(CabMsZip);
        uint offset = 0;
        var epoch = SourceDateEpoch();
        for (var i = 0; i < files.Count; i++)
        {
            fields.UInt32((uint)files[i].Length);
            fields.UInt32(offset);
            fields.UInt16(0);
            var (date, time) = DosDateTime(epoch ?? File.GetLastWriteTimeUtc(files[i].Path));
            fields.UInt16(date);
            fields.UInt16(time);
            fields.UInt16(CabFileAttributes);
            fields.Bytes(names[i]);
            offset += (uint)files[i].Length;
        }

        cabinet.Write(header);
        var written = WriteCabinetData(files, cabinet);
        Require(written == blocks, $"wrote {written} cabinet blocks but expected {blocks}");
        var length = cabinet.Position;
        Span<byte> size = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)length));
        cabinet.Position = CabSizeOffset;
        cabinet.Write(size);
        cabinet.Position = length;
    }

    /// <summary>Reads SOURCE_DATE_EPOCH, so repeated builds stamp the same file times.</summary>
    /// <returns>The UTC time, or null when the variable is not set.</returns>
    private static DateTime? SourceDateEpoch() =>
        long.TryParse(Input("SOURCE_DATE_EPOCH"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ? DateTime.UnixEpoch.AddSeconds(epoch) : null;

    /// <summary>Packs a time into DOS date and time fields.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The date and the time.</returns>
    private static (ushort Date, ushort Time) DosDateTime(DateTime time)
    {
        const int secondsPerStep = 2;
        const int yearShift = 9;
        const int monthShift = 5;
        const int hourShift = 11;
        const int minuteShift = 5;
        if (time.Year < DosFirstYear)
        {
            return (DosFirstDate, 0);
        }

        var year = Math.Min(time.Year, DosLastYear) - DosFirstYear;
        return ((ushort)((year << yearShift) | (time.Month << monthShift) | time.Day), (ushort)((time.Hour << hourShift) | (time.Minute << minuteShift) | (time.Second / secondsPerStep)));
    }

    /// <summary>Compresses the concatenated files into MSZIP blocks.</summary>
    /// <param name="files">The payload files.</param>
    /// <param name="cabinet">The cabinet stream.</param>
    /// <returns>The block count.</returns>
    private static int WriteCabinetData(List<PayloadFile> files, Stream cabinet)
    {
        var plain = ArrayPool<byte>.Shared.Rent(CabBlockLength);
        var block = ArrayPool<byte>.Shared.Rent(CabDataHeaderLength + CkLength + (int)DeflateEncoder.GetMaxCompressedLength(CabBlockLength));
        using var encoder = new DeflateEncoder(new ZLibCompressionOptions { CompressionLevel = DeflateLevel });
        try
        {
            var used = 0;
            var blocks = 0;
            foreach (var file in files)
            {
                using var handle = File.OpenHandle(file.Path, options: FileOptions.SequentialScan);
                long position = 0;
                int count;
                while ((count = RandomAccess.Read(handle, plain.AsSpan(used, CabBlockLength - used), position)) > 0)
                {
                    position += count;
                    used += count;
                    if (used < CabBlockLength)
                    {
                        continue;
                    }

                    cabinet.Write(CabinetBlock(encoder, plain.AsSpan(0, used), block));
                    blocks++;
                    used = 0;
                }

                Require(position == file.Length, $"{file.Path} changed while the cabinet was written");
            }

            if (used > 0)
            {
                cabinet.Write(CabinetBlock(encoder, plain.AsSpan(0, used), block));
                blocks++;
            }

            return blocks;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plain);
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    /// <summary>Builds one CAB data block: a header, the CK marker and a DEFLATE stream, or a stored block for data that does not shrink.</summary>
    /// <param name="encoder">The encoder, reset so each block inflates on its own.</param>
    /// <param name="data">The uncompressed block.</param>
    /// <param name="block">The output buffer.</param>
    /// <returns>The data block.</returns>
    private static ReadOnlySpan<byte> CabinetBlock(DeflateEncoder encoder, ReadOnlySpan<byte> data, byte[] block)
    {
        var payload = block.AsSpan(CabDataHeaderLength);
        "CK"u8.CopyTo(payload);
        encoder.Reset();
        var status = encoder.Compress(data, payload[CkLength..], out var consumed, out var written, isFinalBlock: true);
        Require(status == OperationStatus.Done && consumed == data.Length, $"DEFLATE stopped with {status}");
        var length = CkLength + written;
        if (length > CabMaximumCompressed)
        {
            // A final stored block: the type byte, then the length and its complement.
            var stored = new FieldWriter(payload[CkLength..]);
            stored.Bytes([1]);
            stored.UInt16((ushort)data.Length);
            stored.UInt16((ushort)~data.Length);
            stored.Bytes(data);
            length = CkLength + StoredHeaderLength + data.Length;
        }

        var sizes = block.AsSpan(sizeof(uint), sizeof(uint));
        BinaryPrimitives.WriteUInt16LittleEndian(sizes, (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(sizes[sizeof(ushort)..], (ushort)data.Length);

        // The checksum covers the data, then the two size fields.
        BinaryPrimitives.WriteUInt32LittleEndian(block, CabChecksum(sizes, CabChecksum(payload[..length], 0)));
        return block.AsSpan(0, CabDataHeaderLength + length);
    }

    /// <summary>Computes the CAB checksum: XOR of little-endian words, with trailing bytes taken in reverse order.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="seed">The running checksum.</param>
    /// <returns>The checksum.</returns>
    private static uint CabChecksum(ReadOnlySpan<byte> bytes, uint seed)
    {
        var checksum = seed;
        while (bytes.Length >= sizeof(uint))
        {
            checksum ^= BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            bytes = bytes[sizeof(uint)..];
        }

        uint remainder = 0;
        foreach (var value in bytes)
        {
            remainder = (remainder << BitsPerByte) | value;
        }

        return checksum ^ remainder;
    }

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

        /// <summary>Copies bytes.</summary>
        /// <param name="bytes">The bytes.</param>
        internal void Bytes(scoped ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_destination[_offset..]);
            _offset += bytes.Length;
        }

        /// <summary>Leaves bytes as zero.</summary>
        /// <param name="count">The byte count.</param>
        internal void Skip(int count) => _offset += count;
    }

    /// <summary>A file to install.</summary>
    /// <param name="Path">The file system path.</param>
    /// <param name="Name">The path relative to the source, with forward slashes.</param>
    /// <param name="Length">The file length.</param>
    private readonly record struct PayloadFile(string Path, string Name, long Length);

    /// <summary>A table column.</summary>
    /// <param name="Name">The column name.</param>
    /// <param name="Type">The stored column type.</param>
    private readonly record struct Column(string Name, int Type);

    /// <summary>The string pool: each distinct string once, with how many table cells refer to it.</summary>
    /// <param name="encoding">The code page encoding.</param>
    private sealed class StringPool(Encoding encoding)
    {
        /// <summary>String Ids by value.</summary>
        private readonly Dictionary<string, int> _ids = [with(StringComparer.Ordinal)];

        /// <summary>The encoded strings; Id 0 is null.</summary>
        private readonly List<byte[]> _values = [[]];

        /// <summary>The reference count of each string.</summary>
        private readonly List<int> _references = [0];

        /// <summary>Adds a string, or counts another reference to it.</summary>
        /// <param name="value">The string.</param>
        /// <returns>The string's Id.</returns>
        internal int Intern(string value)
        {
            if (!_ids.TryGetValue(value, out var id))
            {
                id = _values.Count;
                Require(id <= MaximumStrings, "the package has too many distinct strings");
                _ids[value] = id;
                _values.Add(Encode(encoding, value));
                _references.Add(0);
            }

            _references[id]++;
            return id;
        }

        /// <summary>Writes the _StringPool stream of lengths and reference counts and the _StringData stream of string bytes.</summary>
        /// <param name="root">The root storage.</param>
        internal void Write(RootStorage root)
        {
            var entries = 1;
            var dataLength = 0;
            for (var id = 1; id < _values.Count; id++)
            {
                // A string longer than 65535 bytes takes a second entry for its length.
                entries += _values[id].Length > ushort.MaxValue ? LongStringEntries : 1;
                dataLength += _values[id].Length;
            }

            var pool = new byte[entries * sizeof(uint)];
            var data = new byte[dataLength];
            var fields = new FieldWriter(pool);
            fields.UInt16(Codepage);
            fields.Skip(sizeof(ushort));
            var offset = 0;
            for (var id = 1; id < _values.Count; id++)
            {
                var bytes = _values[id];
                var references = (ushort)Math.Min(_references[id], ushort.MaxValue);
                var isLong = bytes.Length > ushort.MaxValue;

                // A zero length marks a long string; the next entry holds the low and high words of its length.
                fields.UInt16(isLong ? (ushort)0 : (ushort)bytes.Length);
                fields.UInt16(references);
                if (isLong)
                {
                    fields.UInt16((ushort)bytes.Length);
                    fields.UInt16((ushort)(bytes.Length >> HighWordShift));
                }

                bytes.CopyTo(data, offset);
                offset += bytes.Length;
            }

            WriteStream(root, TableStreamPrefix + EncodeName("_StringPool"), pool);
            WriteStream(root, TableStreamPrefix + EncodeName("_StringData"), data);
        }
    }

    /// <summary>A table and its rows.</summary>
    /// <param name="Name">The table name.</param>
    /// <param name="Columns">The columns.</param>
    /// <param name="Rows">The rows: strings, integers, a FileInfo for a stream, or null.</param>
    private sealed record Table(string Name, Column[] Columns, List<object?[]> Rows);

    /// <summary>The package settings read from the inputs.</summary>
    /// <param name="Product">The product name.</param>
    /// <param name="Manufacturer">The manufacturer.</param>
    /// <param name="Version">The three-part ProductVersion.</param>
    /// <param name="UpgradeCode">The braced upper case upgrade code.</param>
    /// <param name="Arch">The architecture.</param>
    /// <param name="PerUser">Whether the install is per user.</param>
    /// <param name="Folder">The install folder names.</param>
    /// <param name="ExeIndex">The executable's payload index.</param>
    /// <param name="ShortcutName">The Start menu shortcut name.</param>
    /// <param name="Icon">The icon path, or null.</param>
    /// <param name="Homepage">The homepage, or empty.</param>
    /// <param name="FileTypes">The file extensions.</param>
    /// <param name="FileTypeName">The name shown for the file types.</param>
    /// <param name="ProgId">The ProgID.</param>
    /// <param name="License">The RTF license path, or null.</param>
    private sealed record Settings(
        string Product,
        string Manufacturer,
        string Version,
        string UpgradeCode,
        string Arch,
        bool PerUser,
        string[] Folder,
        int ExeIndex,
        string ShortcutName,
        string? Icon,
        string Homepage,
        string[] FileTypes,
        string FileTypeName,
        string ProgId,
        string? License);
}
