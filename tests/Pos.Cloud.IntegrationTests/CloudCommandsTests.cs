using Pos.Cloud.Host.Cli;

namespace Pos.Cloud.IntegrationTests;

/// <summary>
/// La consola comparte ejecutable con el servidor: un comando que falte en la lista de <see cref="CloudCommands.IsCommand"/> arranca
/// el servidor completo en vez del comando (pasó con <c>generate-sync-key</c> al instalar en el VPS).
/// </summary>
public class CloudCommandsTests
{
    [Theory]
    [InlineData("setup-database")]
    [InlineData("migrate")]
    [InlineData("status")]
    [InlineData("create-superadmin")]
    [InlineData("recover-user")]
    [InlineData("generate-signing-key")]
    [InlineData("generate-sync-key")]
    [InlineData("register-standby-key")]
    [InlineData("revoke-signing-key")]
    [InlineData("verify-audit")]
    [InlineData("healthcheck")]
    [InlineData("help")]
    public void Todo_comando_de_la_consola_se_reconoce(string command) => CloudCommands.IsCommand(command).ShouldBeTrue();

    [Fact]
    public async Task Las_claves_se_generan_sin_base_de_datos()
    {
        var folder = Directory.CreateTempSubdirectory("pos-cloud-keys-");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            (await CloudCommands.RunAsync(["generate-signing-key", "--out", Path.Combine(folder.FullName, "signing-key.pem")], output, error))
                .ShouldBe(0, error.ToString());
            (await CloudCommands.RunAsync(["generate-sync-key", "--out", Path.Combine(folder.FullName, "sync-key.txt")], output, error))
                .ShouldBe(0, error.ToString());

            File.Exists(Path.Combine(folder.FullName, "signing-key.pem")).ShouldBeTrue();
            File.Exists(Path.Combine(folder.FullName, "sync-key.txt")).ShouldBeTrue();
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
