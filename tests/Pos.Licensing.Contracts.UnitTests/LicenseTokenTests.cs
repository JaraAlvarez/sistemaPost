using System.Buffers.Text;
using System.Text;
using System.Text.Json.Nodes;

namespace Pos.Licensing.Contracts.UnitTests;

public sealed class LicenseTokenTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    private readonly LicenseSigningKey _current = LicenseSigningKey.Generate();
    private readonly LicenseSigningKey _next = LicenseSigningKey.Generate();

    public void Dispose()
    {
        _current.Dispose();
        _next.Dispose();
    }

    public static LicenseClaims Claims(string status = SubscriptionStatuses.Active) => new()
    {
        LicenseId = Guid.CreateVersion7(),
        OrganizationNit = "900123456-8",
        OrganizationName = "Supermercado La Economía S.A.S.",
        InstallationId = Guid.CreateVersion7(),
        DeviceFingerprint = DeviceFingerprint.FromHardware("a", "b", "c").ToString(),
        DeviceRole = DeviceRoles.StoreServer,
        Edition = LicenseEditions.MultiTerminal,
        SubscriptionStatus = status,
        IssuedAt = Now,
        ValidUntil = Now.AddDays(30),
        GraceDays = 7,
        RefreshAfter = Now.AddHours(24),
        Messages = [new LicenseMessage("SUBSCRIPTION_EXPIRING", "WARNING", "Su suscripción vence pronto.")],
    };

    [Fact]
    public void Un_token_firmado_se_verifica_con_la_clave_publica_y_conserva_su_contenido()
    {
        var claims = Claims();
        var token = LicenseToken.Sign(claims, _current);

        var result = LicenseToken.Verify(token, new LicenseKeyRing([_current.PublicKey]));

        result.Status.ShouldBe(LicenseTokenStatus.Valid);
        result.IsValid.ShouldBeTrue();
        result.Kid.ShouldBe(_current.Kid);
        result.Claims.ShouldNotBeNull();
        result.Claims.LicenseId.ShouldBe(claims.LicenseId);
        result.Claims.OrganizationNit.ShouldBe("900123456-8");
        result.Claims.ValidUntil.ShouldBe(claims.ValidUntil);
        result.Claims.Messages.Single().Code.ShouldBe("SUBSCRIPTION_EXPIRING");
        result.Claims.Version.ShouldBe(LicenseClaims.CurrentVersion);
    }

    [Fact]
    public void La_cabecera_es_un_JWS_EdDSA_con_kid()
    {
        var token = LicenseToken.Sign(Claims(), _current);
        var header = JsonNode.Parse(Base64Url.DecodeFromChars(token.Split('.')[0]))!;

        header["alg"]!.GetValue<string>().ShouldBe("EdDSA");
        header["kid"]!.GetValue<string>().ShouldBe(_current.Kid);
        header["typ"]!.GetValue<string>().ShouldBe(LicenseToken.Type);
        _current.Kid.ShouldStartWith("ed25519-");
    }

    [Fact]
    public void Cualquier_cambio_en_el_contenido_invalida_la_firma()
    {
        var token = LicenseToken.Sign(Claims(SubscriptionStatuses.Suspended), _current);
        var parts = token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1])).Replace("SUSPENDED", "ACTIVE", StringComparison.Ordinal);
        var tampered = $"{parts[0]}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}.{parts[2]}";

        LicenseToken.Verify(tampered, new LicenseKeyRing([_current.PublicKey])).Status.ShouldBe(LicenseTokenStatus.InvalidSignature);
    }

    [Fact]
    public void Una_clave_que_no_es_de_confianza_se_rechaza_por_su_kid()
    {
        var token = LicenseToken.Sign(Claims(), _next);

        var result = LicenseToken.Verify(token, new LicenseKeyRing([_current.PublicKey]));

        result.Status.ShouldBe(LicenseTokenStatus.UnknownKey);
        result.Kid.ShouldBe(_next.Kid);
    }

    [Fact]
    public void Una_firma_con_otra_clave_bajo_el_mismo_kid_no_es_valida()
    {
        var token = LicenseToken.Sign(Claims(), _next);
        var parts = token.Split('.');
        var forged = $"{LicenseToken.Sign(Claims(), _current).Split('.')[0]}.{parts[1]}.{parts[2]}";

        LicenseToken.Verify(forged, new LicenseKeyRing([_current.PublicKey])).Status.ShouldBe(LicenseTokenStatus.InvalidSignature);
    }

    [Fact]
    public void Rotacion_el_POS_con_las_dos_claves_embebidas_acepta_tokens_de_la_actual_y_de_la_siguiente()
    {
        var ring = new LicenseKeyRing([_current.PublicKey, _next.PublicKey]);

        var before = LicenseToken.Verify(LicenseToken.Sign(Claims(), _current), ring);
        var after = LicenseToken.Verify(LicenseToken.Sign(Claims(), _next), ring);

        before.IsValid.ShouldBeTrue();
        before.Kid.ShouldBe(_current.Kid);
        after.IsValid.ShouldBeTrue();
        after.Kid.ShouldBe(_next.Kid);
        ring.Kids.Count.ShouldBe(2);
    }

    [Fact]
    public void La_clave_privada_se_exporta_e_importa_en_PEM_y_conserva_el_kid()
    {
        var pem = _current.ExportPem();
        using var imported = LicenseSigningKey.ImportPem(pem);

        pem.ShouldStartWith("-----BEGIN PRIVATE KEY-----");
        imported.Kid.ShouldBe(_current.Kid);
        imported.PublicKey.X.ShouldBe(_current.PublicKey.X);
        LicenseToken.Verify(LicenseToken.Sign(Claims(), imported), new LicenseKeyRing([_current.PublicKey])).IsValid.ShouldBeTrue();
        Should.Throw<FormatException>(() => LicenseSigningKey.ImportPem("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----"));
    }

    [Fact]
    public void La_clave_publica_se_publica_en_base64url_y_se_vuelve_a_leer()
    {
        LicensePublicKey.TryParse(_current.PublicKey.X, out var parsed).ShouldBeTrue();
        parsed!.Kid.ShouldBe(_current.Kid);
        LicensePublicKey.TryParse("corta", out _).ShouldBeFalse();
        LicensePublicKey.TryParse(null, out _).ShouldBeFalse();
        LicensePublicKey.TryParse(new string('*', 43), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("***.***.***")]
    [InlineData("e30.e30.AAAA")]
    public void Textos_que_no_son_tokens_son_malformados(string? token) =>
        LicenseToken.Verify(token, new LicenseKeyRing([_current.PublicKey])).Status.ShouldBe(LicenseTokenStatus.Malformed);

    [Fact]
    public void Un_token_enorme_es_malformado() =>
        LicenseToken.Verify(new string('a', LicenseToken.MaxLength + 1), new LicenseKeyRing([])).Status.ShouldBe(LicenseTokenStatus.Malformed);

    [Fact]
    public void Una_version_de_contenido_futura_se_informa_para_actualizar_el_POS()
    {
        var token = SignRaw(new JsonObject { ["ver"] = LicenseClaims.CurrentVersion + 1, ["lic"] = Guid.NewGuid().ToString() });

        LicenseToken.Verify(token, new LicenseKeyRing([_current.PublicKey])).Status.ShouldBe(LicenseTokenStatus.UnsupportedVersion);
    }

    [Theory]
    [InlineData("{\"lic\":\"x\"}")]
    [InlineData("{\"ver\":0}")]
    [InlineData("{\"ver\":1,\"lic\":\"no-es-guid\"}")]
    [InlineData("{\"ver\":1}")]
    [InlineData("no-json")]
    public void Un_contenido_firmado_pero_incompleto_es_malformado(string payload)
    {
        var token = SignRawText(payload);

        LicenseToken.Verify(token, new LicenseKeyRing([_current.PublicKey])).Status.ShouldBe(LicenseTokenStatus.Malformed);
    }

    [Fact]
    public void Vigencia_y_gracia()
    {
        var claims = Claims();

        claims.GraceUntil.ShouldBe(Now.AddDays(37));
        claims.IsValidAt(Now.AddDays(30)).ShouldBeTrue();
        claims.IsInGraceAt(Now.AddDays(30)).ShouldBeFalse();
        claims.IsValidAt(Now.AddDays(31)).ShouldBeFalse();
        claims.IsInGraceAt(Now.AddDays(31)).ShouldBeTrue();
        claims.IsInGraceAt(Now.AddDays(38)).ShouldBeFalse();
    }

    [Fact]
    public void Argumentos_nulos()
    {
        Should.Throw<ArgumentNullException>(() => LicenseToken.Sign(null!, _current));
        Should.Throw<ArgumentNullException>(() => LicenseToken.Sign(Claims(), null!));
        Should.Throw<ArgumentNullException>(() => LicenseToken.Verify("x", null!));
        Should.Throw<ArgumentNullException>(() => new LicenseKeyRing(null!));
        Should.Throw<ArgumentException>(() => LicenseSigningKey.ImportPem(" "));
    }

    private string SignRaw(JsonObject payload) => SignRawText(payload.ToJsonString());

    private string SignRawText(string payload)
    {
        var header = $"{{\"alg\":\"EdDSA\",\"kid\":\"{_current.Kid}\",\"typ\":\"{LicenseToken.Type}\"}}";
        var input = $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header))}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}";
        return $"{input}.{Base64Url.EncodeToString(_current.Sign(Encoding.ASCII.GetBytes(input)))}";
    }
}
