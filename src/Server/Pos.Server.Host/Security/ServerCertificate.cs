using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pos.Application.Abstractions.Security;
using Pos.Server.Host.Configuration;
using Pos.Server.Host.Database;

namespace Pos.Server.Host.Security;

/// <summary>
/// Certificado TLS propio de la instalación (D3-05): ECDSA P-256 autofirmado, 10 años, con el nombre del equipo y sus
/// IP en la LAN. Se genera la primera vez y se guarda en <c>{DataRoot}\config\server-tls.pfx</c>; la contraseña del PFX
/// queda protegida con DPAPI. Los equipos fijan su huella SHA-256 al emparejarse.
/// </summary>
internal sealed class ServerCertificate : IServerIdentity
{
    private readonly Lazy<X509Certificate2> _certificate;
    private readonly bool _enabled;

    public ServerCertificate(ProductPaths paths, string edition)
    {
        _enabled = string.Equals(edition, "MULTI", StringComparison.OrdinalIgnoreCase);
        _certificate = new Lazy<X509Certificate2>(() => LoadOrCreate(paths.ConfigDirectory));
    }

    public X509Certificate2 Certificate => _certificate.Value;

    public string? CertificateFingerprint => _enabled ? Convert.ToHexStringLower(SHA256.HashData(Certificate.RawData)) : null;

    private static X509Certificate2 LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var pfxPath = Path.Combine(directory, "server-tls.pfx");
        var keyPath = Path.Combine(directory, "server-tls.key");
        if (File.Exists(pfxPath) && File.Exists(keyPath))
        {
            var stored = ProtectedSecret.Reveal(File.ReadAllText(keyPath).Trim())!;
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, stored, X509KeyStorageFlags.Exportable);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={Environment.MachineName} ({ProductInfo.Name})", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Environment.MachineName);
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        foreach (var address in LocalAddresses())
        {
            names.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(pfxPath, created.Export(X509ContentType.Pfx, password));
        File.WriteAllText(keyPath, OperatingSystem.IsWindows() ? ProtectedSecret.Protect(password) : password);
        return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password, X509KeyStorageFlags.Exportable);
    }

    private static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct();
}
