# ADR-0047 · Catálogo de acciones de auditoría en el código y en la BD

- **Estado:** Aceptada · 2026-09-29 · Fase 10 · Decisiones D10-01, D10-02 y D10-03

## Contexto
Cada módulo escribía sus acciones de auditoría como texto libre (`SALE_VOIDED`, `PRODUCT_UPDATED`…). Los reportes y consultas mostraban
códigos técnicos, y nada impedía que una acción nueva quedara sin nombre o que un evento prometido en el doc 06 no se registrara.

## Decisión
- **Catálogo en el código** (`Pos.Modules.Audit.Contracts.AuditActions`): código, módulo, nombre en español y severidad por defecto de
  cada acción explícita, y el nombre en español de cada entidad `[Audited]` (el interceptor genera `ENTIDAD_CREATED|UPDATED|DELETED`).
- **Misma lista en la BD** (`audit.action_types`, llenada por `R__audit__action_types.sql`); ambos archivos se generan desde la misma
  fuente. La vista `reporting.audit_log` resuelve el nombre; si falta, muestra el código.
- **Pruebas:** R9 (arquitectura) exige que toda entidad `[Audited]` y todo código escrito en `new AuditEntry(…)` o en los ayudantes
  `AuditAsync(usuario, "CÓDIGO"…)` esté en el catálogo; la prueba de API compara el catálogo del código con la BD.
- **Diferencias en español (D10-03):** las consultas devuelven, además del JSON crudo, la lista `campo · nombre · antes · después` con un
  diccionario común de nombres de columnas (`AuditFieldNames`).
- **Cobertura:** se agregaron `LOGIN_FAILED`, `PASSWORD_RESET`, `PIN_RESET`, `SALE_LINE_VOIDED`, `SERVER_STARTED`, `DATABASE_MIGRATED`,
  `CLOCK_JUMP_DETECTED` y los de integridad; los de backups y licencia quedan reservados en el catálogo (Fases 11 y 12). Al revisar el
  código se comprobó que retiros, cajón sin venta, cierres, cambios de contraseña/PIN y bloqueos ya se registraban.

## Consecuencias
- Un código de acción es para siempre (queda en filas selladas): renombrar significa agregar uno nuevo; el anterior no se borra de la BD.
- Agregar una acción exige tocar el catálogo (la prueba lo recuerda).
