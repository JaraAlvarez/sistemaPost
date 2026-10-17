namespace Pos.Cloud.PortalIdentity.Domain;

/// <summary>
/// Reglas para AUTORIZAR en el portal la instalación de BusinessPost ("Continuar con Google" del instalador, Fase 13-B parte E). Es
/// una acción de alto valor (convierte al usuario en el Propietario del POS): por eso, aunque el ingreso normal con Google no pide el
/// TOTP (ADR-0062), aquí se exige:
/// <list type="bullet">
/// <item>Un ingreso RECIENTE (contraseña o Google en los últimos <see cref="ReauthenticationWindow"/>): una sesión abierta hace horas
/// en un equipo compartido no basta.</item>
/// <item>El código TOTP si el usuario lo tiene activo o si el portal lo exige (<c>Portal:RequireTotp</c>), SIN IMPORTAR el método de
/// ingreso (también después de Google). Es la misma regla de <c>/v1/owner-links</c> (correo y contraseña en el instalador).</item>
/// </list>
/// </summary>
public static class OwnerLinkAuthorizationPolicy
{
    /// <summary>Minutos desde el ingreso (o el segundo factor) dentro de los cuales se puede autorizar sin volver a ingresar.</summary>
    public static readonly TimeSpan ReauthenticationWindow = TimeSpan.FromMinutes(10);

    /// <summary>¿Se pide el TOTP para autorizar? El método de la sesión (contraseña o Google) no cambia la respuesta.</summary>
    public static bool RequiresTotp(PortalUser user, bool requireTotpByPolicy)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.TotpEnabled || requireTotpByPolicy;
    }

    /// <summary>Momento en que el usuario se autenticó en esta sesión (el segundo factor si lo hubo; si no, el ingreso).</summary>
    public static DateTimeOffset AuthenticatedAt(PortalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.SecondFactorAt is { } at && at > session.CreatedAt ? at : session.CreatedAt;
    }

    /// <summary>¿La sesión es del usuario, sigue utilizable, está activa y se autenticó hace poco?</summary>
    public static bool IsRecent(PortalSession session, PortalUser user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(user);
        return session.Stage == SessionStage.Active && session.IsUsable(user, now) && now - AuthenticatedAt(session) <= ReauthenticationWindow;
    }
}
