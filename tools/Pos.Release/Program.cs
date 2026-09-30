using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Pos.Licensing.Contracts;
using Pos.Updates.Contracts;

// Uso:
//   Pos.Release new-key --out clave-actualizaciones.pem
//        Genera la clave de firma de actualizaciones. Guárdela FUERA del VPS (como la de licencias). Imprime kid y x para update-keys.json.
//   Pos.Release sign --package BusinessPost-1.4.0.zip --version 1.4.0 --key clave.pem --out stable.json
//                    [--channel stable] [--package-url BusinessPost-1.4.0.zip] [--min-terminal 1.4.0] [--notes "Novedades…"] [--product BusinessPost]
//        Firma el manifiesto de una versión (huella SHA-256 y tamaño del paquete).
//   Pos.Release verify --manifest stable.json --public-key <x> [--product BusinessPost]
// Códigos de salida: 0 = correcto, 1 = manifiesto inválido, 2 = uso incorrecto.
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
    }
}

string Required(string name) =>
    options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"Falta la opción --{name}.");

try
{
    switch (args.FirstOrDefault())
    {
        case "new-key":
        {
            var path = Required("out");
            if (File.Exists(path))
            {
                Console.Error.WriteLine($"{path} ya existe: no se sobrescribe una clave.");
                return 2;
            }

            using var key = LicenseSigningKey.Generate();
            await File.WriteAllTextAsync(path, key.ExportPem());
            Console.WriteLine($"Clave guardada en {path}. Cópiela a un lugar seguro FUERA del servidor.");
            Console.WriteLine($"Para src/Server/Pos.Server.Updater/update-keys.json: {{ \"kid\": \"{key.Kid}\", \"kty\": \"OKP\", \"crv\": \"Ed25519\", \"x\": \"{key.PublicKey.X}\", \"status\": \"ACTIVE\" }}");
            return 0;
        }

        case "sign":
        {
            var package = Required("package");
            var version = Required("version");
            if (!SemanticVersion.TryParse(version, out _))
            {
                Console.Error.WriteLine("La versión debe ser MAYOR.MENOR.PARCHE.");
                return 2;
            }

            var channel = options.GetValueOrDefault("channel", UpdateChannels.Stable);
            if (!UpdateChannels.IsKnown(channel))
            {
                Console.Error.WriteLine("Canal desconocido: stable, beta o internal.");
                return 2;
            }

            using var key = LicenseSigningKey.ImportPem(await File.ReadAllTextAsync(Required("key")));
            await using var stream = File.OpenRead(package);
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            var manifest = new UpdateManifest(
                options.GetValueOrDefault("product", "BusinessPost"), channel, version, options.GetValueOrDefault("package-url", Path.GetFileName(package)),
                sha, stream.Length, options.GetValueOrDefault("min-terminal", version), DateTimeOffset.UtcNow, options.GetValueOrDefault("notes", string.Empty));
            var signed = UpdateSigning.Sign(manifest, key);
            await File.WriteAllTextAsync(Required("out"), UpdateSigning.Serialize(signed));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Manifiesto {channel} {version} firmado con {key.Kid}: {Required("out")} (paquete {stream.Length / 1024.0 / 1024.0:0.0} MB, SHA-256 {sha[..12]}…)."));
            return 0;
        }

        case "verify":
        {
            var signed = UpdateSigning.Deserialize(await File.ReadAllTextAsync(Required("manifest")));
            if (!LicensePublicKey.TryParse(Required("public-key"), out var publicKey))
            {
                Console.Error.WriteLine("Clave pública inválida.");
                return 2;
            }

            var status = UpdateSigning.Verify(signed, new LicenseKeyRing([publicKey]), options.GetValueOrDefault("product", "BusinessPost"), out var manifest);
            Console.WriteLine(status == ManifestStatus.Valid
                ? $"Manifiesto válido: {JsonSerializer.Serialize(manifest)}"
                : $"MANIFIESTO INVÁLIDO: {status}");
            return status == ManifestStatus.Valid ? 0 : 1;
        }

        default:
            Console.Error.WriteLine("Comandos: new-key | sign | verify");
            return 2;
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException or FormatException or CryptographicException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
