# ADR-0042 · Autorización de tratamiento de datos y derechos del titular

- **Estado:** Aceptada · 2026-09-29 · Fase 8 · Decisiones D8-05 a D8-08 (pregunta 5 del propietario) · Riesgo R-10

## Decisión
- **Libro de autorizaciones de solo inserción** (`customers.customer_consents`, disparador + privilegios): por finalidad
  (`SERVICE`, `MARKETING`, `LOYALTY`), otorgada o revocada, canal (`POS_VERBAL` por defecto en la caja, `POS_SIGNED`, `PAPER_FORM`…),
  canales de marketing, **versión de la política**, evidencia, usuario, sucursal, caja, nodo y hora. Revocar es otro registro. El estado
  vigente (último registro de cada finalidad) se guarda en el cliente. El marketing nunca viene autorizado por defecto.
- **Política versionada** con texto completo, aviso corto (el que se lee en la caja) y su huella SHA-256. Se siembra una **plantilla
  pendiente de revisión** que el propietario debe revisar con su asesor legal y activar.
- **Sin autorización SERVICE** el cliente se identifica para la factura pero no se muestra su historial (⚙️
  `customers.history_requires_consent`).
- **Snapshot fiscal completo** del comprador en la venta (`sales.customer_fiscal`, jsonb) al cobrar y marca **"pide factura
  electrónica"**, que exige correo, régimen y responsabilidades (y dirección y municipio si es persona jurídica).
- **Derechos del titular:** solicitudes con vencimiento en **días hábiles** (consulta 10, reclamo 15) calculados con los festivos de
  Colombia (Ley Emiliani y Pascua, sin tabla); exportación (JSON) y **supresión por anonimización** conservando la identificación, los
  documentos y los snapshots de las ventas.

## Consecuencias
- Validar con un abogado el texto de la política y el canal verbal; la inscripción ante la SIC es un trámite del propietario.
- La exportación en PDF queda pendiente (hoy JSON).
