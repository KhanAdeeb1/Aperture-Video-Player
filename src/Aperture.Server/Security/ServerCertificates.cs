using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;

namespace Aperture.Server.Security;

/// <summary>
/// The server's HTTPS certificate, issued at start for this computer's addresses and issued
/// again when they change (a new Wi-Fi network, a VPN connecting), so a phone's address always
/// matches the certificate.
/// </summary>
public sealed class ServerCertificates : IDisposable
{
    private readonly X509Certificate2 _authority;
    private readonly Func<IReadOnlyList<IPAddress>> _addresses;
    private readonly Lock _gate = new();
    private X509Certificate2 _current;
    private volatile bool _stale;

    public ServerCertificates(X509Certificate2 authority, Func<IReadOnlyList<IPAddress>>? addresses = null)
    {
        _authority = authority;
        _addresses = addresses ?? CertificateAuthority.LocalAddresses;
        _current = Issue();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public X509Certificate2 Authority => _authority;

    /// <summary>The certificate to present, renewed first if the computer's addresses changed.</summary>
    public X509Certificate2 Current
    {
        get
        {
            if (!_stale)
            {
                return _current;
            }

            lock (_gate)
            {
                if (_stale)
                {
                    _stale = false;
                    _current = Issue();
                }

                return _current;
            }
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _current.Dispose();
    }

    private X509Certificate2 Issue() => CertificateAuthority.IssueServerCertificate(_authority, _addresses(), Environment.MachineName);

    private void OnAddressChanged(object? sender, EventArgs e) => _stale = true;
}
