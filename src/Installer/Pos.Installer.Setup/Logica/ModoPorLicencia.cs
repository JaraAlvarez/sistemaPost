namespace Pos.Installer.Setup.Logica;

/// <summary>
/// Modo con el que se instala (instalador 1.2.1). Ya no se pregunta "¿Cómo usarás este equipo?" ni la licencia: por defecto el equipo es
/// la única caja (sirve con cualquier licencia: Multicaja también admite un equipo que hace todo). La página solo decide la caja adicional
/// (enlace de la Bienvenida; no lleva licencia propia, se empareja con el principal) y, al recuperar un backup, si el equipo dañado era el
/// principal. Pasar un equipo de única caja a equipo principal al activar una licencia Multicaja queda pendiente (docs/manual-instalacion.md).
/// </summary>
public static class ModoPorLicencia
{
    public static ModoEquipo Elegir(ModoEquipo pedido, bool conBackup) =>
        pedido == ModoEquipo.Adicional || conBackup ? pedido : ModoEquipo.Unica;
}
