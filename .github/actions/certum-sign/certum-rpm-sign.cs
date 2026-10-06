// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:package Pkcs11Interop

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using static System.Environment;

if (args is not [var packagesGlob])
{
    Console.WriteLine("::error::Expected the packages glob argument.");
    return Program.UsageError;
}

if (GetEnvironmentVariable("SS_PKCS11") is not { Length: > 0 } module)
{
    Console.WriteLine("::error::SS_PKCS11 is not set; run certum-connect first.");
    return 1;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

var packages = Program.Match(packagesGlob);

if (packages is [])
{
    Console.WriteLine($"::error::no packages matched: {packagesGlob}");
    return 1;
}

// Pkcs11Interop imports dlopen from "libdl"; glibc 2.34 and later only ship that name in its development package.
NativeLibrary.SetDllImportResolver(
    typeof(Pkcs11InteropFactories).Assembly,
    static (name, _, _) => string.Equals(name, "libdl", StringComparison.Ordinal) ? NativeLibrary.Load("libdl.so.2") : 0);

var factories = new Pkcs11InteropFactories();

using var library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, module, AppType.MultiThreaded);

using var session = library.GetSlotList(SlotsType.WithTokenPresent)[0].OpenSession(SessionType.ReadOnly);

Program.LogIn(session);

if (await Program.FindKeyAsync(session, CancellationToken.None).ConfigureAwait(false) is not { } key)
{
    Console.WriteLine("::error::the token exposed no private key");
    return 1;
}

var publicKey = session.GetAttributeValue(key, [CKA.CKA_MODULUS, CKA.CKA_PUBLIC_EXPONENT]);

using var rsa = RSA.Create(new RSAParameters { Modulus = publicKey[0].GetValueAsByteArray(), Exponent = publicKey[1].GetValueAsByteArray() });

var keyId = RpmIma.KeyId(rsa);

var signer = new TokenSigner(session, key, rsa);

foreach (var package in packages)
{
    Console.WriteLine($"::group::sign files in {package}");
    var signed = RpmIma.Sign(package, keyId, signer.Sign);
    Console.WriteLine($"signed {signed} files with key id {Convert.ToHexStringLower(keyId)}");
    Console.WriteLine("::endgroup::");
}

return 0;

/// <summary>Adds IMA file signatures to RPMs with the Certum token's key.</summary>
internal static partial class Program
{
    /// <summary>The exit code for bad arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The longest wait for the token to show its private key after login.</summary>
    private static readonly TimeSpan KeyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The interval between searches while the token shows no key.</summary>
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

    /// <summary>Logs in to the token with the empty password jsign also uses.</summary>
    /// <param name="session">The token session.</param>
    internal static void LogIn(ISession session)
    {
        try
        {
            session.Login(CKU.CKU_USER, string.Empty);
        }
        catch (Pkcs11Exception ex) when (ex.RV == CKR.CKR_USER_ALREADY_LOGGED_IN)
        {
            // An earlier step left the session logged in, which is what this needs.
        }
    }

    /// <summary>Waits for the token to list its private key, which can take a few seconds after login.</summary>
    /// <param name="session">The logged-in session.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The key, or null when the timeout passed first.</returns>
    internal static async Task<IObjectHandle?> FindKeyAsync(ISession session, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(KeyTimeout);
        using var timer = new PeriodicTimer(RetryInterval);
        List<IObjectAttribute> search = [session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY)];
        var attempt = 1;
        try
        {
            do
            {
                if (session.FindAllObjects(search) is [var key, ..])
                {
                    return key;
                }

                Console.WriteLine($"token key not available yet (attempt {attempt})");
                attempt++;
            }
            while (await timer.WaitForNextTickAsync(limit.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout passed with the token still showing no key.
        }

        return null;
    }

    /// <summary>Adds IMA file signatures to an RPM and recomputes its header digests.</summary>
    internal static class RpmIma
    {
        /// <summary>The length of the lead before the signature header.</summary>
        internal const int LeadLength = 96;

        /// <summary>The file digests tag.</summary>
        internal const int TagFileDigests = 1035;

        /// <summary>The IMA file signatures tag.</summary>
        internal const int TagFileSignatures = 5090;

        /// <summary>The tag holding the longest file signature's length.</summary>
        internal const int TagFileSignatureLength = 5091;

        /// <summary>The file digest algorithm tag.</summary>
        private const int TagFileDigestAlgo = 5011;

        /// <summary>The file digest algorithm value for SHA-256.</summary>
        private const int DigestAlgoSha256 = 8;

        /// <summary>The obsolete header image region tag.</summary>
        private const int RegionImage = 61;

        /// <summary>The signature header region tag.</summary>
        private const int RegionSignatures = 62;

        /// <summary>The main header region tag.</summary>
        private const int RegionImmutable = 63;

        /// <summary>The signature header tag for the header and payload size.</summary>
        private const int TagSize = 1000;

        /// <summary>The signature header tag for the legacy header and payload MD5.</summary>
        private const int TagMd5 = 1004;

        /// <summary>The signature header tag for the header SHA-1.</summary>
        private const int TagSha1 = 269;

        /// <summary>The signature header tag for the header SHA-256.</summary>
        private const int TagSha256 = 273;

        /// <summary>The signature header tag for an OpenPGP DSA header signature.</summary>
        private const int TagDsa = 267;

        /// <summary>The signature header tag for an OpenPGP RSA header signature.</summary>
        private const int TagRsa = 268;

        /// <summary>The signature header tag for a legacy PGP header and payload signature.</summary>
        private const int TagPgp = 1002;

        /// <summary>The signature header tag for a legacy GPG header and payload signature.</summary>
        private const int TagGpg = 1005;

        /// <summary>The char value type.</summary>
        private const int TypeChar = 1;

        /// <summary>The int8 value type.</summary>
        private const int TypeInt8 = 2;

        /// <summary>The int16 value type.</summary>
        private const int TypeInt16 = 3;

        /// <summary>The int32 value type.</summary>
        private const int TypeInt32 = 4;

        /// <summary>The int64 value type.</summary>
        private const int TypeInt64 = 5;

        /// <summary>The string value type.</summary>
        private const int TypeString = 6;

        /// <summary>The binary value type.</summary>
        private const int TypeBinary = 7;

        /// <summary>The string array value type.</summary>
        private const int TypeStringArray = 8;

        /// <summary>The length of a header index entry, and of the magic and counts before the index.</summary>
        private const int IndexEntryLength = 16;

        /// <summary>Where a header stores its entry count.</summary>
        private const int EntryCountOffset = 8;

        /// <summary>Where a header stores its data store length.</summary>
        private const int StoreLengthOffset = 12;

        /// <summary>Where an index entry stores its type.</summary>
        private const int TypeOffset = 4;

        /// <summary>Where an index entry stores its data offset.</summary>
        private const int DataOffset = 8;

        /// <summary>Where an index entry stores its value count.</summary>
        private const int CountOffset = 12;

        /// <summary>The signature header is padded to this alignment before the main header.</summary>
        private const int SignatureAlignment = 8;

        /// <summary>The bytes of the IMA key id: the tail of the Subject Key Identifier.</summary>
        private const int KeyIdLength = 4;

        /// <summary>The IMA xattr type for a digital signature.</summary>
        private const byte ImaSignatureType = 3;

        /// <summary>The IMA signature format version.</summary>
        private const byte ImaVersion = 2;

        /// <summary>The IMA hash algorithm number for SHA-256.</summary>
        private const byte ImaSha256 = 4;

        /// <summary>The bits in a byte, for the big-endian signature size.</summary>
        private const int BitsPerByte = 8;

        /// <summary>Gets the eight bytes that start every header: magic, version 1 and four reserved bytes.</summary>
        private static ReadOnlySpan<byte> HeaderMagic => [0x8E, 0xAD, 0xE8, 0x01, 0, 0, 0, 0];

        /// <summary>Gets the DigestInfo prefix for SHA-256, which PKCS#1 v1.5 signing places before the digest.</summary>
        private static ReadOnlySpan<byte> Sha256DigestInfo => [0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20];

        /// <summary>Gets the signature tags that sign the old header and would no longer verify: DSA, RSA, PGP and GPG.</summary>
        private static ReadOnlySpan<int> StaleSignatureTags => [TagDsa, TagRsa, TagPgp, TagGpg];

        /// <summary>Gets the IMA key id: the last four bytes of the certificate's Subject Key Identifier, the SHA-1 of its public key.</summary>
        /// <param name="rsa">The public key.</param>
        /// <returns>The key id.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte[] KeyId(RSA rsa) => Hash(HashAlgorithmName.SHA1, rsa.ExportRSAPublicKey())[^KeyIdLength..];

        /// <summary>Signs every regular file's digest and rewrites the package in place.</summary>
        /// <param name="path">The .rpm.</param>
        /// <param name="keyId">The IMA key id.</param>
        /// <param name="sign">Signs a SHA-256 DigestInfo with PKCS#1 v1.5 padding.</param>
        /// <returns>The number of files signed.</returns>
        /// <exception cref="InvalidDataException">The package does not record SHA-256 file digests.</exception>
        internal static int Sign(string path, byte[] keyId, Func<byte[], byte[]> sign)
        {
            var file = File.ReadAllBytes(path);
            var signatureLength = HeaderLength(file, LeadLength);
            var headerStart = LeadLength + signatureLength + Pad(signatureLength, SignatureAlignment);
            var headerLength = HeaderLength(file, headerStart);
            var payload = file.AsMemory(headerStart + headerLength);
            var signature = ReadHeader(file.AsSpan(LeadLength, signatureLength));
            var header = ReadHeader(file.AsSpan(headerStart, headerLength));
            if (!header.TryGetValue(TagFileDigestAlgo, out var algo) || BinaryPrimitives.ReadInt32BigEndian(algo.Data) != DigestAlgoSha256)
            {
                throw new InvalidDataException($"{path} does not record SHA-256 file digests.");
            }

            var digests = Strings(header[TagFileDigests]);
            var signatures = new string[digests.Length];
            var longest = 0;
            var count = 0;
            var digestInfo = new byte[Sha256DigestInfo.Length + SHA256.HashSizeInBytes];
            Sha256DigestInfo.CopyTo(digestInfo);
            for (var i = 0; i < digests.Length; i++)
            {
                if (digests[i].Length == 0)
                {
                    signatures[i] = string.Empty;
                    continue;
                }

                _ = Convert.FromHexString(digests[i], digestInfo.AsSpan(Sha256DigestInfo.Length), out _, out _);
                var value = sign(digestInfo);

                // IMA signature, version 2: xattr type, version, hash algorithm, key id, big-endian size, signature.
                byte[] ima = [ImaSignatureType, ImaVersion, ImaSha256, .. keyId, (byte)(value.Length >> BitsPerByte), (byte)value.Length, .. value];
                signatures[i] = Convert.ToHexStringLower(ima);
                longest = Math.Max(longest, ima.Length);
                count++;
            }

            header[TagFileSignatures] = StringArray(signatures);
            header[TagFileSignatureLength] = Int32(longest);
            var headerBytes = WriteHeader(header, RegionImmutable);

            // The header changed, so its digests and the size are recomputed; old OpenPGP signatures would no longer verify.
            foreach (var stale in StaleSignatureTags)
            {
                _ = signature.Remove(stale);
            }

            signature[TagSha256] = String(Convert.ToHexStringLower(SHA256.HashData(headerBytes)));
            if (signature.ContainsKey(TagSha1))
            {
                signature[TagSha1] = String(Convert.ToHexStringLower(Hash(HashAlgorithmName.SHA1, headerBytes)));
            }

            if (signature.ContainsKey(TagMd5))
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                md5.AppendData(headerBytes);
                md5.AppendData(payload.Span);
                signature[TagMd5] = new(TypeBinary, MD5.HashSizeInBytes, md5.GetHashAndReset());
            }

            signature[TagSize] = Int32(headerBytes.Length + payload.Length);
            var signatureBytes = WriteHeader(signature, RegionSignatures);

            Replace(path, [file.AsMemory(0, LeadLength), signatureBytes, new byte[Pad(signatureBytes.Length, SignatureAlignment)], headerBytes, payload]);
            return count;
        }

        /// <summary>Gets the length of the header at an offset: magic, counts, index and store.</summary>
        /// <param name="file">The package.</param>
        /// <param name="start">The header offset.</param>
        /// <returns>The header length.</returns>
        internal static int HeaderLength(byte[] file, int start) =>
            IndexEntryLength
            + (BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(start + EntryCountOffset)) * IndexEntryLength)
            + BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(start + StoreLengthOffset));

        /// <summary>Reads a header's entries, leaving out its region tag, which is written afresh.</summary>
        /// <param name="header">The header bytes.</param>
        /// <returns>The entries by tag.</returns>
        internal static SortedDictionary<int, Entry> ReadHeader(ReadOnlySpan<byte> header)
        {
            var count = BinaryPrimitives.ReadInt32BigEndian(header[EntryCountOffset..]);
            var store = header[(IndexEntryLength + (count * IndexEntryLength))..];
            var entries = new SortedDictionary<int, Entry>();
            for (var i = 0; i < count; i++)
            {
                var index = header.Slice(IndexEntryLength + (i * IndexEntryLength), IndexEntryLength);
                var tag = BinaryPrimitives.ReadInt32BigEndian(index);
                var type = BinaryPrimitives.ReadInt32BigEndian(index[TypeOffset..]);
                var offset = BinaryPrimitives.ReadInt32BigEndian(index[DataOffset..]);
                var values = BinaryPrimitives.ReadInt32BigEndian(index[CountOffset..]);
                if (tag is RegionImage or RegionSignatures or RegionImmutable)
                {
                    continue;
                }

                var data = store[offset..];
                var length = type switch
                {
                    TypeChar or TypeInt8 or TypeBinary => values,
                    TypeInt16 => values * sizeof(short),
                    TypeInt32 => values * sizeof(int),
                    TypeInt64 => values * sizeof(long),
                    TypeString => data.IndexOf((byte)0) + 1,
                    _ => StringsLength(data, values),
                };
                entries[tag] = new(type, values, data[..length].ToArray());
            }

            return entries;
        }

        /// <summary>Decodes a string array entry.</summary>
        /// <param name="entry">The entry.</param>
        /// <returns>The strings.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string[] Strings(Entry entry) => Encoding.UTF8.GetString(entry.Data).Split('\0')[..entry.Count];

        /// <summary>Hashes data with a named algorithm; SHA-1 here only fills the formats' legacy fields.</summary>
        /// <param name="algorithm">The algorithm.</param>
        /// <param name="data">The data.</param>
        /// <returns>The digest.</returns>
        private static byte[] Hash(HashAlgorithmName algorithm, ReadOnlySpan<byte> data)
        {
            using var hash = IncrementalHash.CreateHash(algorithm);
            hash.AppendData(data);
            return hash.GetHashAndReset();
        }

        /// <summary>Gets the padding that aligns a length.</summary>
        /// <param name="length">The length so far.</param>
        /// <param name="alignment">The alignment.</param>
        /// <returns>The number of zero bytes to add.</returns>
        private static int Pad(int length, int alignment) => (alignment - (length % alignment)) % alignment;

        /// <summary>Writes the new package beside the original, then moves it over the original so a failed write leaves the original intact.</summary>
        /// <param name="path">The package.</param>
        /// <param name="parts">The package's parts, in order.</param>
        private static void Replace(string path, IReadOnlyList<ReadOnlyMemory<byte>> parts)
        {
            var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var package = File.OpenHandle(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    RandomAccess.Write(package, parts, 0);
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                File.Delete(temporary);
                throw;
            }
        }

        /// <summary>Measures a run of NUL-terminated strings.</summary>
        /// <param name="data">The store from the first string.</param>
        /// <param name="count">The number of strings.</param>
        /// <returns>The length in bytes.</returns>
        private static int StringsLength(ReadOnlySpan<byte> data, int count)
        {
            var length = 0;
            for (var i = 0; i < count; i++)
            {
                length += data[length..].IndexOf((byte)0) + 1;
            }

            return length;
        }

        /// <summary>Makes a string entry.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The entry.</returns>
        private static Entry String(string value) => new(TypeString, 1, Encoding.UTF8.GetBytes($"{value}\0"));

        /// <summary>Makes a string array entry.</summary>
        /// <param name="values">The values.</param>
        /// <returns>The entry.</returns>
        private static Entry StringArray(string[] values)
        {
            var builder = new StringBuilder();
            foreach (var value in values)
            {
                _ = builder.Append(value).Append('\0');
            }

            return new(TypeStringArray, values.Length, Encoding.UTF8.GetBytes(builder.ToString()));
        }

        /// <summary>Makes a 32-bit integer entry.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The entry.</returns>
        private static Entry Int32(int value)
        {
            var data = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(data, value);
            return new(TypeInt32, 1, data);
        }

        /// <summary>Writes entries as a header whose region covers all of them.</summary>
        /// <param name="entries">The entries by tag.</param>
        /// <param name="region">The region tag.</param>
        /// <returns>The header bytes.</returns>
        private static byte[] WriteHeader(SortedDictionary<int, Entry> entries, int region)
        {
            var store = new ArrayBufferWriter<byte>();
            List<(int Tag, int Type, int Offset, int Count)> index = [];
            foreach (var (tag, entry) in entries)
            {
                var alignment = entry.Type switch { TypeInt16 => sizeof(short), TypeInt32 => sizeof(int), TypeInt64 => sizeof(long), _ => 1 };
                var padding = Pad(store.WrittenCount, alignment);
                store.GetSpan(padding)[..padding].Clear();
                store.Advance(padding);
                index.Add((tag, entry.Type, store.WrittenCount, entry.Count));
                store.Write(entry.Data);
            }

            var regionOffset = store.WrittenCount;
            var total = index.Count + 1;
            WriteEntry(store.GetSpan(IndexEntryLength), (region, TypeBinary, -(total * IndexEntryLength), IndexEntryLength));
            store.Advance(IndexEntryLength);

            var bytes = new byte[IndexEntryLength + (total * IndexEntryLength) + store.WrittenCount];
            HeaderMagic.CopyTo(bytes);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(EntryCountOffset), total);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(StoreLengthOffset), store.WrittenCount);
            WriteEntry(bytes.AsSpan(IndexEntryLength), (region, TypeBinary, regionOffset, IndexEntryLength));
            for (var i = 0; i < index.Count; i++)
            {
                WriteEntry(bytes.AsSpan(IndexEntryLength + ((i + 1) * IndexEntryLength)), index[i]);
            }

            store.WrittenSpan.CopyTo(bytes.AsSpan((total + 1) * IndexEntryLength));
            return bytes;
        }

        /// <summary>Writes one header index entry: tag, type, store offset and value count, all big-endian.</summary>
        /// <param name="target">Receives the entry.</param>
        /// <param name="entry">The entry.</param>
        private static void WriteEntry(Span<byte> target, (int Tag, int Type, int Offset, int Count) entry)
        {
            BinaryPrimitives.WriteInt32BigEndian(target, entry.Tag);
            BinaryPrimitives.WriteInt32BigEndian(target[TypeOffset..], entry.Type);
            BinaryPrimitives.WriteInt32BigEndian(target[DataOffset..], entry.Offset);
            BinaryPrimitives.WriteInt32BigEndian(target[CountOffset..], entry.Count);
        }

        /// <summary>A header entry's type, value count and data.</summary>
        /// <param name="Type">The value type.</param>
        /// <param name="Count">The value count.</param>
        /// <param name="Data">The data.</param>
        internal readonly record struct Entry(int Type, int Count, byte[] Data);
    }

    /// <summary>Signs DigestInfo blocks on the token and checks each signature against the key's public half.</summary>
    /// <param name="session">The logged-in session.</param>
    /// <param name="key">The private key.</param>
    /// <param name="publicKey">The key's public half.</param>
    internal sealed class TokenSigner(ISession session, IObjectHandle key, RSA publicKey)
    {
        /// <summary>The raw PKCS#1 v1.5 mechanism, which signs a caller-built DigestInfo.</summary>
        private readonly IMechanism _mechanism = session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS);

        /// <summary>Signs a SHA-256 DigestInfo.</summary>
        /// <param name="digestInfo">The DigestInfo, ending in the 32-byte digest.</param>
        /// <returns>The signature.</returns>
        /// <exception cref="CryptographicException">The token's signature does not verify.</exception>
        internal byte[] Sign(byte[] digestInfo)
        {
            var signature = session.Sign(_mechanism, key, digestInfo);
            if (!publicKey.VerifyHash(digestInfo.AsSpan(digestInfo.Length - SHA256.HashSizeInBytes), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                throw new CryptographicException("The token's signature does not verify against its public key.");
            }

            return signature;
        }
    }
}
