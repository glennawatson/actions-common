// Copyright (c) 2026 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:package Pkcs11Interop
#:package OpenMcdf
#:package System.Security.Cryptography.Pkcs

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using OpenMcdf;
using static System.Environment;

if (args is not [var filesGlob, var certificateFrom])
{
    Console.WriteLine("::error::Expected the files glob and certificate-from arguments.");
    return Program.UsageError;
}

if (GetEnvironmentVariable("SS_PKCS11") is not { Length: > 0 } module)
{
    Console.WriteLine("::error::SS_PKCS11 is not set; run certum-connect first.");
    return 1;
}

var folder = Path.GetDirectoryName(filesGlob) is { Length: > 0 } directory ? directory : ".";

var files = Program.Match(folder, Path.GetFileName(filesGlob));

if (files is [])
{
    Console.WriteLine($"::error::no files matched: {filesGlob}");
    return 1;
}

var content = Program.Checksums(files);

// The token lists no certificate, so it is taken from a file jsign signed with it.
var signed = await Program.ReadSignatureAsync(certificateFrom, CancellationToken.None).ConfigureAwait(false);

var certificate = signed.SignerInfos[0].Certificate ?? throw new InvalidDataException($"{certificateFrom} has no signing certificate.");

if (!Program.MatchesFingerprint(certificate, GetEnvironmentVariable("CERTUM_CERT_FINGERPRINT")))
{
    Console.WriteLine($"::error::the certificate in {certificateFrom} differs from CERTUM_CERT_FINGERPRINT");
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

var publicKey = Program.PublicKey(session, key);

if (!Program.BelongsTo(publicKey, certificate))
{
    Console.WriteLine("::error::the token's key does not belong to the signing certificate");
    return 1;
}

using var tokenKey = new TokenRsa(session, key, publicKey);

var signature = Program.Sign(content, certificate, tokenKey, signed.Certificates);

File.WriteAllBytes(Path.Combine(folder, "SHA256SUMS"), content.Span);

File.WriteAllBytes(Path.Combine(folder, "SHA256SUMS.p7s"), signature);

Console.WriteLine($"Wrote SHA256SUMS for {files.Length} files and signed it as {certificate.Subject}.");

return 0;

/// <summary>Writes SHA256SUMS for release files and signs it as a detached CMS signature with the Certum token.</summary>
internal static partial class Program
{
    /// <summary>The exit code for bad arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The name every output file starts with, which the glob must not pick up.</summary>
    private const string ChecksumsName = "SHA256SUMS";

    /// <summary>The OID of SHA-256.</summary>
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    /// <summary>The length of the WIN_CERTIFICATE header before the PKCS#7 data.</summary>
    private const int WinCertificateHeaderLength = 8;

    /// <summary>The length of the "PKCX" magic before the PKCS#7 data in AppxSignature.p7x.</summary>
    private const int AppxSignatureMagicLength = 4;

    /// <summary>The two spaces sha256sum puts between the hash and the file name.</summary>
    private static readonly byte[] Separator = "  "u8.ToArray();

    /// <summary>The longest wait for the token to show its private key after login.</summary>
    private static readonly TimeSpan KeyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The interval between key searches.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(4);

    /// <summary>Lists the release files matching a file name pattern, leaving out earlier checksum output.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="pattern">The file name pattern.</param>
    /// <returns>The files, sorted ordinally.</returns>
    internal static string[] Match(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        List<string> files = [];
        foreach (var file in Directory.EnumerateFiles(folder, pattern))
        {
            if (!Path.GetFileName(file.AsSpan()).StartsWith(ChecksumsName, StringComparison.Ordinal))
            {
                files.Add(file);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return [.. files];
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

    /// <summary>Reads the Authenticode signature of a signed .exe or .dll, .msi, or .msix.</summary>
    /// <param name="path">The signed file.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The decoded signature.</returns>
    /// <exception cref="InvalidDataException">The file holds no signature.</exception>
    internal static async Task<SignedCms> ReadSignatureAsync(string path, CancellationToken cancellationToken)
    {
        var encoded = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".MSI" => MsiSignature(path),
            ".MSIX" or ".APPX" => (await AppxSignatureAsync(path, cancellationToken).ConfigureAwait(false))[AppxSignatureMagicLength..],
            _ => PortableExecutableSignature(path),
        };
        var cms = new SignedCms();
        cms.Decode(encoded);
        return cms;
    }

    /// <summary>Checks a certificate against the expected SHA-256 fingerprint, when one is set.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <param name="fingerprint">The hex fingerprint, with or without colons; empty or null skips the check.</param>
    /// <returns>True when no fingerprint is set or it matches.</returns>
    internal static bool MatchesFingerprint(X509Certificate2 certificate, string? fingerprint) =>
        fingerprint is not { Length: > 0 }
        || CryptographicOperations.FixedTimeEquals(Convert.FromHexString(fingerprint.Replace(":", string.Empty, StringComparison.Ordinal)), certificate.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>Reads the public half of the token's RSA key.</summary>
    /// <param name="session">The token session.</param>
    /// <param name="key">The private key.</param>
    /// <returns>The modulus and exponent.</returns>
    internal static RSAParameters PublicKey(ISession session, IObjectHandle key)
    {
        var values = session.GetAttributeValue(key, [CKA.CKA_MODULUS, CKA.CKA_PUBLIC_EXPONENT]);
        return new() { Modulus = values[0].GetValueAsByteArray(), Exponent = values[1].GetValueAsByteArray() };
    }

    /// <summary>Checks that a key is the one the certificate certifies.</summary>
    /// <param name="publicKey">The key's public half.</param>
    /// <param name="certificate">The certificate.</param>
    /// <returns>True when the moduli match.</returns>
    /// <exception cref="InvalidDataException">The certificate has no RSA key.</exception>
    internal static bool BelongsTo(RSAParameters publicKey, X509Certificate2 certificate)
    {
        using var certificateKey = certificate.GetRSAPublicKey() ?? throw new InvalidDataException("The signing certificate has no RSA key.");
        return certificateKey.ExportParameters(false).Modulus.AsSpan().SequenceEqual(publicKey.Modulus);
    }

    /// <summary>Logs in to the token with the empty PIN jsign also uses.</summary>
    /// <param name="session">The token session.</param>
    internal static void LogIn(ISession session)
    {
        try
        {
            session.Login(CKU.CKU_USER, string.Empty);
        }
        catch (Pkcs11Exception ex) when (ex.RV == CKR.CKR_USER_ALREADY_LOGGED_IN)
        {
            // certum-connect's session already holds the login.
        }
    }

    /// <summary>Waits for the token to show its private key, which can take a few seconds after login.</summary>
    /// <param name="session">The token session.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The key, or null when the timeout passed first.</returns>
    internal static async Task<IObjectHandle?> FindKeyAsync(ISession session, CancellationToken cancellationToken)
    {
        List<IObjectAttribute> search = [session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY)];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(KeyTimeout);
        using var timer = new PeriodicTimer(RetryInterval);
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
            // The timeout passed with no key on the token.
        }

        return null;
    }

    /// <summary>Signs content as a detached CMS signature and checks the result before it is published.</summary>
    /// <param name="content">The content.</param>
    /// <param name="certificate">The signing certificate.</param>
    /// <param name="key">The signing key.</param>
    /// <param name="chain">Certificates to include besides the signing certificate.</param>
    /// <returns>The encoded signature.</returns>
    internal static byte[] Sign(ReadOnlyMemory<byte> content, X509Certificate2 certificate, RSA key, X509Certificate2Collection chain)
    {
        var info = new ContentInfo(content.ToArray());
        var cms = new SignedCms(info, detached: true);
        CmsSigner signer = new(SubjectIdentifierType.IssuerAndSerialNumber, certificate, key) { DigestAlgorithm = new(Sha256Oid), IncludeOption = X509IncludeOption.EndCertOnly };
        _ = signer.SignedAttributes.Add(new Pkcs9SigningTime());
        foreach (var extra in chain)
        {
            if (!extra.RawData.AsSpan().SequenceEqual(certificate.RawData))
            {
                _ = signer.Certificates.Add(extra);
            }
        }

        cms.ComputeSignature(signer, silent: true);
        var signature = cms.Encode();
        var check = new SignedCms(info, detached: true);
        check.Decode(signature);
        check.CheckSignature(verifySignatureOnly: true);
        return signature;
    }

    /// <summary>Reads the \u0005DigitalSignature stream of an .msi.</summary>
    /// <param name="path">The .msi.</param>
    /// <returns>The PKCS#7 data.</returns>
    private static byte[] MsiSignature(string path)
    {
        using var root = RootStorage.Open(path, FileMode.Open, FileAccess.Read);
        using var stream = root.OpenStream("\u0005DigitalSignature");
        var encoded = new byte[stream.Length];
        stream.ReadExactly(encoded);
        return encoded;
    }

    /// <summary>Reads AppxSignature.p7x from an .msix or .appx.</summary>
    /// <param name="path">The package.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The p7x bytes, starting with its magic.</returns>
    /// <exception cref="InvalidDataException">The package is not signed.</exception>
    private static async Task<byte[]> AppxSignatureAsync(string path, CancellationToken cancellationToken)
    {
        var archive = await ZipFile.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        await using (archive.ConfigureAwait(false))
        {
            var entry = archive.GetEntry("AppxSignature.p7x") ?? throw new InvalidDataException($"{path} is not signed.");
            var encoded = new byte[entry.Length];
            var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                await stream.ReadExactlyAsync(encoded, cancellationToken).ConfigureAwait(false);
            }

            return encoded;
        }
    }

    /// <summary>Reads the certificate table of a signed .exe or .dll.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The PKCS#7 data.</returns>
    /// <exception cref="InvalidDataException">The file is not signed.</exception>
    private static byte[] PortableExecutableSignature(string path)
    {
        DirectoryEntry table;
        using (var reader = new PEReader(File.OpenRead(path)))
        {
            table = reader.PEHeaders.PEHeader?.CertificateTableDirectory ?? default;
        }

        if (table.Size <= WinCertificateHeaderLength)
        {
            throw new InvalidDataException($"{path} is not signed.");
        }

        // The certificate table's address is a file offset; WIN_CERTIFICATE starts with its own length.
        using var file = File.OpenHandle(path);
        Span<byte> length = stackalloc byte[sizeof(int)];
        _ = RandomAccess.Read(file, length, table.RelativeVirtualAddress);
        var encoded = new byte[BinaryPrimitives.ReadInt32LittleEndian(length) - WinCertificateHeaderLength];
        if (RandomAccess.Read(file, encoded, table.RelativeVirtualAddress + WinCertificateHeaderLength) != encoded.Length)
        {
            throw new InvalidDataException($"{path} has a truncated certificate table.");
        }

        return encoded;
    }

    /// <summary>An RSA key whose private operation runs on the token; SignedCms only asks it to sign a SHA-256 hash.</summary>
    /// <param name="session">The token session.</param>
    /// <param name="key">The token's private key.</param>
    /// <param name="publicKey">The key's public half.</param>
    internal sealed class TokenRsa(ISession session, IObjectHandle key, RSAParameters publicKey) : RSA
    {
        /// <summary>The bits in a byte.</summary>
        private const int BitsPerByte = 8;

        /// <summary>The DigestInfo prefix for SHA-256, which PKCS#1 v1.5 signing puts before the hash.</summary>
        private static readonly byte[] Sha256DigestInfo = Convert.FromHexString("3031300d060960864801650304020105000420");

        /// <inheritdoc/>
        public override int KeySize => publicKey.Modulus!.Length * BitsPerByte;

        /// <inheritdoc/>
        public override RSAParameters ExportParameters(bool includePrivateParameters) =>
            includePrivateParameters ? throw new CryptographicException("The private key stays on the token.") : publicKey;

        /// <inheritdoc/>
        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
        {
            if (hashAlgorithm != HashAlgorithmName.SHA256 || padding != RSASignaturePadding.Pkcs1)
            {
                throw new CryptographicException("Only SHA-256 with PKCS#1 v1.5 padding is supported.");
            }

            return session.Sign(session.Factories.MechanismFactory.Create(CKM.CKM_RSA_PKCS), key, [.. Sha256DigestInfo, .. hash]);
        }

        /// <inheritdoc/>
        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
        {
            using var rsa = Create(publicKey);
            return rsa.VerifyHash(hash, signature, hashAlgorithm, padding);
        }
    }
}
