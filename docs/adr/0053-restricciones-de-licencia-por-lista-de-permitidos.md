# ADR-0053 · Restricciones de la licencia en el backend por lista de permitidos

- **Estado:** Aceptada · 2026-09-29 · Fase 12-B · Decisiones D12B-02 y D12B-03 de la [propuesta](../fases/fase-12b-propuesta.md)
- **Reemplaza:** el `IFeatureGate` provisional de la Fase 1 (ya no hay *features* por plan, ADR-0015).

## Contexto
Con la licencia **restringida** (fin de la gracia, suscripción suspendida o cancelada, demostración vencida, reloj atrasado) el POS debe seguir
vendiendo y cerrando las jornadas abiertas (RN-LIC-01) y nunca bloquear consultas, reportes, exportación ni backups (RN-LIC-04), pero no
debe permitir abrir jornadas nuevas ni administrar.

## Decisión
- `ILicenseGate` (Application.Abstractions) expone el estado calculado. Un comportamiento del *pipeline*,
  `LicenseRestrictionBehavior`, rechaza con `LICENSE.RESTRICTED` (422) todo **comando** que no implemente la interfaz marcadora
  `IAllowedWhenRestricted`. Las **consultas** nunca pasan por el filtro.
- La marca está en: iniciar y cerrar sesión, PIN, autorización de supervisor y desbloqueo; toda la venta y el cobro; cambios y garantías;
  movimientos y cierre de jornada; clientes en la caja y habeas data; gastos de caja; backups; exportación de reportes; verificaciones
  de auditoría y del kardex; reintento de documentos fiscales; configuración inicial; y la propia licencia (activar, verificar, liberar,
  recalcular).
- La prueba de arquitectura **R10** fija la lista: agregar o quitar un comando permitido obliga a revisarla.
- Sin módulo de licencia (consola, pruebas de otros módulos) se registra `UnrestrictedLicenseGate`.

## Consecuencias
- Por defecto seguro: un comando nuevo nace bloqueado en `RESTRICTED`, y **nunca** afecta a `DEMO`, `VALID`, `VALID_OFFLINE` ni `GRACE`.
- Olvidar marcar un comando operativo lo bloquearía solo en `RESTRICTED`, un estado raro y avisado con 7 días. R10 obliga a pensarlo.
- La UI (Fase 15) solo oculta opciones; la regla vive en el backend.
