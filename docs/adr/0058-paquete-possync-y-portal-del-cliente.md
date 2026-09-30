# ADR-0058 · Paquete `.possync` cifrado para la nube y portal del cliente

- **Estado:** Aceptada · 2026-09-30 · Fase 16 · Decisiones D16-04 y D16-06 de la [propuesta](../fases/fase-16-propuesta.md)

## Decisión
1. **Paquete `.possync`** (`Pos.Sync.Contracts`): una línea de encabezado JSON legible (formato `POSSYNC1`, instalación, lote, fecha,
   cantidad, clave efímera, nonce) + el lote comprimido y cifrado con **ChaCha20-Poly1305**. La clave sale de **X25519** entre una clave
   efímera y la **clave pública de sincronización de la nube** (HKDF-SHA256 con el encabezado como sal). El encabezado va como dato asociado:
   cambiar un byte invalida el paquete.
2. La clave pública va **embebida** en el POS (`sync-key.json`); la privada solo en el VPS (`Sync:PrivateKeyPath`, secreto de Docker),
   generada con `generate-sync-key`. Si se pierde una USB con el paquete, nadie lee las ventas.
3. El paquete lleva lo que la nube aún no confirmó en línea y no se exportó antes (cursor de exportación propio); al cargarlo en el portal
   se aplica con la misma idempotencia que el envío en línea.
4. **Rol `CUSTOMER`** en el portal (misma seguridad: contraseña + TOTP), atado a una cuenta (columna `reseller_account_id`): solo ve y carga
   datos de las empresas de esa cuenta (`sync.data.view`, `sync.package.upload`). Superadministrador y Soporte ven todas.
5. En la tienda, la pantalla **Sincronización** (`/admin/sincronizacion`, permisos `sync.sync.view` y `.manage`) muestra lo pendiente,
   los envíos y permite sincronizar ahora y exportar el paquete.

## Consecuencias
- Perder la clave privada obliga a publicar una versión con la nueva pública; los paquetes viejos se vuelven a exportar.
- El portal del cliente de v1 consulta y carga paquetes; no vende ni edita maestros (Fase 18).
