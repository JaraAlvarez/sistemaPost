using Pos.Installer.Setup.Logica;
using Pos.Licensing.Contracts;

namespace Pos.Installer.Setup.Tests;

/// <summary>
/// Espera de "Continuar con Google" en el instalador (como el cliente de OAuth 2.0 Device Authorization): intervalos, slow_down, sin
/// conexión, vencimiento, cancelación y el enlace al autorizar. Sin red ni reloj reales.
/// </summary>
public class EsperaAutorizacionTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero);

    private static OwnerLinkResponse Enlace => new("jws.de.prueba", "dueno@gmail.com", "Dueño", Inicio.AddHours(2));

    /// <summary>Reloj simulado: cada espera lo adelanta exactamente lo pedido.</summary>
    private sealed class Simulacion(params ResultadoConsulta[] respuestas)
    {
        private readonly Queue<ResultadoConsulta> _respuestas = new(respuestas);

        public DateTimeOffset Ahora { get; private set; } = Inicio;

        public List<TimeSpan> Esperas { get; } = [];

        public int Consultas { get; private set; }

        public Task<ResultadoConsulta> ConsultarAsync(CancellationToken cancellationToken)
        {
            Consultas++;
            return Task.FromResult(_respuestas.Count > 0 ? _respuestas.Dequeue() : new ResultadoConsulta(OwnerLinkRequestStatuses.Pending, 5));
        }

        public Task EsperarAsync(TimeSpan espera, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Esperas.Add(espera);
            Ahora += espera;
            return Task.CompletedTask;
        }

        public Task<EstadoEspera> EjecutarAsync(EsperaAutorizacion espera) =>
            espera.EjecutarAsync(ConsultarAsync, EsperarAsync, () => Ahora, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sigue_consultando_mientras_esta_pendiente_y_termina_con_el_enlace_al_autorizar()
    {
        var simulacion = new Simulacion(
            new ResultadoConsulta(OwnerLinkRequestStatuses.Pending, 5),
            new ResultadoConsulta(OwnerLinkRequestStatuses.Pending, 5),
            new ResultadoConsulta(OwnerLinkRequestStatuses.Approved, 5, Enlace));
        var espera = new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        (await simulacion.EjecutarAsync(espera)).ShouldBe(EstadoEspera.Autorizada);

        espera.Enlace.ShouldBe(Enlace);
        simulacion.Consultas.ShouldBe(3);
        simulacion.Esperas.ShouldAllBe(e => e == TimeSpan.FromSeconds(5), "Espera el intervalo ANTES de cada consulta.");
    }

    [Fact]
    public async Task Slow_down_suma_5_segundos_y_el_intervalo_del_servidor_nunca_baja()
    {
        var simulacion = new Simulacion(
            ResultadoConsulta.Fallo(LicenseErrorCodes.OwnerLinkSlowDown, "Demasiado seguido"),
            ResultadoConsulta.Fallo("SECURITY.TOO_MANY_REQUESTS", "Límite por IP"),
            new ResultadoConsulta(OwnerLinkRequestStatuses.Pending, 5),
            new ResultadoConsulta(OwnerLinkRequestStatuses.Pending, 20),
            new ResultadoConsulta(OwnerLinkRequestStatuses.Approved, 20, Enlace));
        var espera = new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        (await simulacion.EjecutarAsync(espera)).ShouldBe(EstadoEspera.Autorizada);

        simulacion.Esperas.Select(e => (int)e.TotalSeconds).ShouldBe([5, 10, 15, 15, 20]);
    }

    [Fact]
    public void El_intervalo_queda_entre_5_y_30_segundos()
    {
        new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1)).Intervalo.ShouldBe(TimeSpan.FromSeconds(5));
        var espera = new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(28));
        espera.Aplicar(ResultadoConsulta.Fallo(LicenseErrorCodes.OwnerLinkSlowDown, null));
        espera.Intervalo.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Sin_conexion_reintenta_y_vence_si_no_vuelve()
    {
        var simulacion = new Simulacion([.. Enumerable.Repeat(ResultadoConsulta.SinConexion, 500)]);
        var espera = new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        (await simulacion.EjecutarAsync(espera)).ShouldBe(EstadoEspera.Vencida);

        espera.FallasDeConexion.ShouldBeGreaterThan(100);
        espera.Mensaje!.ShouldContain("Continuar con Google");
        simulacion.Ahora.ShouldBeLessThanOrEqualTo(espera.Vence, "Nunca consulta después de vencer.");
    }

    [Fact]
    public async Task Un_error_sin_codigo_o_el_servidor_en_mantenimiento_se_reintentan()
    {
        var simulacion = new Simulacion(
            ResultadoConsulta.Fallo(null, "El servidor respondió 502"),
            ResultadoConsulta.Fallo(LicenseErrorCodes.SigningUnavailable, "Mantenimiento"),
            new ResultadoConsulta(OwnerLinkRequestStatuses.Approved, 5, Enlace));
        var espera = new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        (await simulacion.EjecutarAsync(espera)).ShouldBe(EstadoEspera.Autorizada);
        espera.FallasDeConexion.ShouldBe(0, "Se reinicia al volver la conexión.");
    }

    [Theory]
    [InlineData(OwnerLinkRequestStatuses.Denied, EstadoEspera.Rechazada)]
    [InlineData(OwnerLinkRequestStatuses.Expired, EstadoEspera.Vencida)]
    [InlineData("OTRO", EstadoEspera.Error)]
    public async Task Rechazada_vencida_o_desconocida_terminan_sin_enlace(string estado, EstadoEspera esperado)
    {
        var simulacion = new Simulacion(new ResultadoConsulta(OwnerLinkRequestStatuses.Pending, 5), new ResultadoConsulta(estado, 5));
        var espera = new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        (await simulacion.EjecutarAsync(espera)).ShouldBe(esperado);
        espera.Enlace.ShouldBeNull();
        espera.Mensaje.ShouldNotBeNullOrWhiteSpace();
        simulacion.Consultas.ShouldBe(2);
    }

    [Fact]
    public async Task Aprobada_sin_enlace_no_cuenta_como_autorizada()
    {
        var simulacion = new Simulacion(new ResultadoConsulta(OwnerLinkRequestStatuses.Approved, 5));
        (await simulacion.EjecutarAsync(new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5)))).ShouldBe(EstadoEspera.Error);
    }

    [Theory]
    [InlineData(LicenseErrorCodes.OwnerLinkRequestUsed)]
    [InlineData(LicenseErrorCodes.OwnerLinkRequestNotFound)]
    [InlineData(LicenseErrorCodes.KeyInvalid)]
    public async Task Un_error_definitivo_termina_con_el_mensaje_del_servidor(string codigo)
    {
        var simulacion = new Simulacion(ResultadoConsulta.Fallo(codigo, "Mensaje para el cliente"));
        var espera = new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

        (await simulacion.EjecutarAsync(espera)).ShouldBe(EstadoEspera.Error);
        espera.Mensaje.ShouldBe("Mensaje para el cliente");
    }

    [Fact]
    public async Task Cancelar_detiene_la_espera_sin_mensaje()
    {
        using var cancelacion = new CancellationTokenSource();
        var simulacion = new Simulacion();
        var espera = new EsperaAutorizacion(Inicio, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
        var cambios = 0;

        var estado = await espera.EjecutarAsync(
            async ct =>
            {
                if (++cambios == 3)
                {
                    await cancelacion.CancelAsync();
                }

                return await simulacion.ConsultarAsync(ct);
            },
            simulacion.EsperarAsync, () => simulacion.Ahora, null, cancelacion.Token);

        estado.ShouldBe(EstadoEspera.Cancelada);
        espera.Mensaje.ShouldBeNull();
        cambios.ShouldBe(3);

        // Una respuesta que llega tarde ya no cambia nada.
        espera.Aplicar(new ResultadoConsulta(OwnerLinkRequestStatuses.Approved, 5, Enlace)).ShouldBe(EstadoEspera.Cancelada);
        espera.Enlace.ShouldBeNull();
    }
}

/// <summary>Reglas del ingreso con Google que no dependen de Windows.</summary>
public class IngresoGoogleReglasTests
{
    private static readonly Uri Servidor = new("https://businesspost.tutiendanueva.com/");

    [Theory]
    [InlineData("https://businesspost.tutiendanueva.com/cuenta/vincular-instalador?r=0192f000-0000-7000-8000-000000000001", true)]
    [InlineData("http://businesspost.tutiendanueva.com/cuenta/vincular-instalador?r=1", false)]
    [InlineData("https://otro-sitio.com/cuenta/vincular-instalador?r=1", false)]
    [InlineData("https://businesspost.tutiendanueva.com.evil.co/cuenta/vincular-instalador?r=1", false)]
    [InlineData("https://usuario@businesspost.tutiendanueva.com/cuenta/vincular-instalador?r=1", false)]
    [InlineData("https://businesspost.tutiendanueva.com:8443/cuenta/vincular-instalador?r=1", false)]
    [InlineData("https://businesspost.tutiendanueva.com/cuenta/ingresar?returnUrl=https://otro.com", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("no es una dirección", false)]
    public void Solo_abre_en_el_navegador_la_pagina_HTTPS_del_mismo_servidor(string direccion, bool segura) =>
        IngresoGoogle.EsDireccionSegura(direccion, Servidor).ShouldBe(segura);

    [Fact]
    public void Bajo_una_ruta_exige_la_misma_ruta_y_en_desarrollo_admite_localhost()
    {
        var conRuta = new Uri("https://tutiendanueva.com/businesspost/");
        IngresoGoogle.EsDireccionSegura("https://tutiendanueva.com/businesspost/cuenta/vincular-instalador?r=1", conRuta).ShouldBeTrue();
        IngresoGoogle.EsDireccionSegura("https://tutiendanueva.com/cuenta/vincular-instalador?r=1", conRuta).ShouldBeFalse();
        IngresoGoogle.EsDireccionSegura("http://localhost:5000/cuenta/vincular-instalador?r=1", new Uri("http://localhost:5000/")).ShouldBeTrue();
        IngresoGoogle.EsDireccionSegura("https://businesspost.tutiendanueva.com/cuenta/vincular-instalador?r=1", null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Corta123", "Corta123", "La contraseña debe tener al menos 12 caracteres.")]
    [InlineData("SoloLetrasSinNumeros", "SoloLetrasSinNumeros", "Usa letras y números en la contraseña.")]
    [InlineData("123456789012", "123456789012", "Usa letras y números en la contraseña.")]
    [InlineData("dueno1234567", "dueno1234567", "Esa contraseña es muy fácil de adivinar. Usa una frase o mezcla letras y números.")]
    [InlineData("aaaa1111aaaa", "aaaa1111aaaa", "Esa contraseña es muy fácil de adivinar. Usa una frase o mezcla letras y números.")]
    [InlineData("Tienda-Centro-2026", "Tienda-Centro-2025", "Las dos contraseñas no coinciden.")]
    [InlineData("Tienda-Centro-2026", "Tienda-Centro-2026", null)]
    public void La_contrasena_local_cumple_la_politica_del_portal(string contrasena, string confirmacion, string? problema) =>
        IngresoGoogle.ValidarContrasenaLocal(contrasena, confirmacion, "dueno@gmail.com").ShouldBe(problema);
}
