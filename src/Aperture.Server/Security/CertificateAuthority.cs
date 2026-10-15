using System.Formats.Asn1;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aperture.Server.Security;

/// <summary>
/// Aperture's own small certificate authority, so the server can use HTTPS on the home network.
/// Each computer creates its own; its private key never leaves the computer. A phone that
/// installs the authority's certificate trusts the server without warnings, and iPhone video
/// playback (which ignores Safari's "visit anyway") works. The authority is name-constrained to
/// private addresses and local names, so even if its key leaked it could not impersonate a
/// public website on a device that honours name constraints.
/// </summary>
public sealed class CertificateAuthority
{
    private const string SubjectPrefix = "CN=Aperture Media Server";
    private static readonly TimeSpan AuthorityLifetime = TimeSpan.FromDays(3650);

    // Apple accepts at most 825 days for TLS certificates from user-installed roots; the
    // server issues a new one at every start, so a short life costs nothing.
    private static readonly TimeSpan ServerLifetime = TimeSpan.FromDays(397);

    private readonly string _pfxPath;
    private readonly bool _useWindowsStore;

    /// <param name="pfxPath">Where the authority is kept when not using the Windows store.</param>
    /// <param name="useWindowsStore">Keep it in the current user's certificate store (Windows), where Windows protects the key.</param>
    public CertificateAuthority(string pfxPath, bool useWindowsStore)
    {
        _pfxPath = pfxPath;
        _useWindowsStore = useWindowsStore && OperatingSystem.IsWindows();
    }

    /// <summary>Loads the authority, creating it the first time.</summary>
    public X509Certificate2 LoadOrCreate()
    {
        var existing = Load();
        if (existing is not null && existing.NotAfter > DateTime.Now.AddDays(30))
        {
            return existing;
        }

        existing?.Dispose();
        var created = Create(Environment.MachineName);
        Save(created);
        return created;
    }

    /// <summary>Forgets the authority. Phones that installed it must install the new one.</summary>
    public void Reset()
    {
        if (_useWindowsStore)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var cert in FindInStore(store))
            {
                store.Remove(cert);
            }
        }
        else if (File.Exists(_pfxPath))
        {
            File.Delete(_pfxPath);
        }
    }

    /// <summary>SHA-256 of the certificate, written as phones show it ("AB:CD:…").</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Join(':', certificate.GetCertHash(HashAlgorithmName.SHA256).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>A server certificate for this computer's current names and addresses, signed by the authority.</summary>
    public static X509Certificate2 IssueServerCertificate(X509Certificate2 authority, IEnumerable<IPAddress> addresses, string hostName)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(addresses);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={SafeName(hostName)}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        var host = SafeName(hostName).ToLowerInvariant();
        if (host.Length > 0)
        {
            names.AddDnsName(host);
            names.AddDnsName(host + ".local");
        }

        // Only addresses inside the authority's name constraints: a certificate naming any other
        // address would be refused outright by clients that enforce them. The server doesn't
        // answer public addresses anyway.
        foreach (var address in addresses.Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).Distinct()
            .Where(a => Core.Server.NetworkAccess.Classify(a) != Core.Server.NetworkZone.Public))
        {
            names.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(names.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        var now = DateTimeOffset.UtcNow;
        var notAfter = now + ServerLifetime < authority.NotAfter ? now + ServerLifetime : new DateTimeOffset(authority.NotAfter.ToUniversalTime());
        using var unsigned = request.Create(authority, now.AddMinutes(-5), notAfter, serial);
        using var withKey = unsigned.CopyWithPrivateKey(key);

        // A round trip through PKCS#12 gives the key a form Windows' TLS stack (SChannel) accepts.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>The addresses of this computer's network adapters that are up (not loopback).</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork || (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal))
                    {
                        list.Add(address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0 ? new IPAddress(address.GetAddressBytes()) : address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No adapter information: the certificate still covers loopback.
        }

        return list;
    }

    internal static X509Certificate2 Create(string machineName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"{SubjectPrefix} ({SafeName(machineName)})", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(NameConstraints(machineName));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now + AuthorityLifetime);
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// RFC 5280 name constraints: the authority may only vouch for private and link-local IP
    /// addresses, Tailscale's range, loopback, and local names (this computer's name, *.local,
    /// *.home.arpa, *.lan, localhost).
    /// </summary>
    internal static X509Extension NameConstraints(string machineName)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var dnsNames = new List<string> { "localhost", "local", "home.arpa", "lan" };
                var host = SafeName(machineName).ToLowerInvariant();
                if (host.Length > 0)
                {
                    dnsNames.Insert(0, host);
                }

                foreach (var name in dnsNames)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
                    }
                }

                (string Address, int Prefix)[] ranges =
                [
                    ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("169.254.0.0", 16),
                    ("100.64.0.0", 10), ("127.0.0.0", 8),
                    ("fc00::", 7), ("fe80::", 10), ("::1", 128),
                ];
                foreach (var (address, prefix) in ranges)
                {
                    var bytes = IPAddress.Parse(address).GetAddressBytes();
                    var mask = new byte[bytes.Length];
                    for (var i = 0; i < prefix; i++)
                    {
                        mask[i / 8] |= (byte)(0x80 >> (i % 8));
                    }

                    using (writer.PushSequence())
                    {
                        writer.WriteOctetString([.. bytes, .. mask], new Asn1Tag(TagClass.ContextSpecific, 7));
                    }
                }
            }
        }

        return new X509Extension("2.5.29.30", writer.Encode(), critical: false);
    }

    private X509Certificate2? Load()
    {
        if (_useWindowsStore)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return FindInStore(store).Where(c => c.HasPrivateKey).OrderByDescending(c => c.NotAfter).FirstOrDefault();
        }

        if (!File.Exists(_pfxPath))
        {
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, null, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            return null; // unreadable: a new authority is created
        }
    }

    private void Save(X509Certificate2 authority)
    {
        if (_useWindowsStore)
        {
            Reset();
            var persisted = X509CertificateLoader.LoadPkcs12(
                authority.Export(X509ContentType.Pkcs12),
                null,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
            if (OperatingSystem.IsWindows())
            {
                persisted.FriendlyName = "Aperture media server";
            }
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(persisted);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_pfxPath)!);
        var temp = _pfxPath + ".tmp";
        File.WriteAllBytes(temp, authority.Export(X509ContentType.Pkcs12));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, _pfxPath, overwrite: true);
    }

    private static IEnumerable<X509Certificate2> FindInStore(X509Store store) =>
        store.Certificates.Cast<X509Certificate2>().Where(c => c.Subject.StartsWith(SubjectPrefix, StringComparison.Ordinal));

    private static string SafeName(string name) =>
        new([.. (name ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')]);
}
