# 12 · Plan de desarrollo (S), riesgos y decisiones (T)

> Estado: **PROPUESTA — pendiente de aprobación**

## S. Plan de desarrollo por fases

> **Actualización Fase 2:** se agrega la fase **"Sincronización y portal web"** (nube multiempresa, sincronización en
> vivo, paquetes `.possync`, portal con consultas, edición de maestros y bandeja de conflictos), después de POS y ventas
> y junto a la infraestructura en la nube del servidor de licencias. Ediciones Caja Única / Multicaja (ADR-0015);
> facturación con Factus (ADR-0013).

Respeto tu lista de 15 fases, con **cuatro ajustes** que evitan retrabajo (marcados ⚠️):

| # | Fase | Contenido | Entregable verificable (sin UI) |
|---|---|---|---|
| 1 | **Arquitectura general** | Repositorio git, solución, `SharedKernel` (Money, Quantity, Result, IDs, reloj), infraestructura base, host como servicio, logs, health checks, pruebas de arquitectura, CI, ADRs | La solución compila, el servicio arranca, `/health` responde, pruebas de arquitectura en verde |
| 2 | **Base de datos** | PostgreSQL local, esquemas, migraciones, semillas, `Organization`, `Settings`, secuencias, outbox, idempotencia. ⚠️ **Infraestructura de auditoría aquí** (no en la fase 10): todo lo que se construya después debe auditar desde el primer día | BD creada desde cero con un comando; pruebas de integración contra Postgres real |
| 3 | **Autenticación y usuarios** | Identity completo: usuarios, empleados, roles, permisos, sesiones, PIN, autorización de supervisor. ⚠️ **Feature gates de licencia en modo stub** (todo habilitado) para que cada endpoint nazca protegido | Login, permisos y overrides probados vía API (`.http`/OpenAPI) |
| 4 | **Productos e inventario** | Catálogo completo (presentaciones, códigos, peso variable, impuestos, precios con vigencia), kardex, costo promedio, ajustes, conteos, traslados, importación | Escenario: cargar catálogo, saldo inicial, ajustes; kardex y saldo cuadran |
| 5 | **Compras** | ⚠️ **Terceros (`Parties`) base aquí**, proveedores, OC, compras, CxP, devoluciones a proveedor | Compra contabilizada actualiza stock y costo; devolución lo revierte |
| 6 | ⚠️ **Caja** (antes que POS) | Jornadas, movimientos, arqueo, cierres. **Se adelanta porque toda venta exige jornada abierta** (RN-SAL-01) | Abrir/cerrar jornada con movimientos manuales y diferencia calculada |
| 7 | **POS y ventas** | Venta persistida, motor de cálculo, pagos combinados, suspender/recuperar, anulación, devoluciones de clientes, documento fiscal interno + interfaz `IFiscalProvider`, agente de terminal con impresora ESC/POS y cajón | Escenario "día de operación": 500 ventas simuladas, cierre cuadra al centavo con kardex y caja |
| 8 | **Clientes y proveedores** | Clientes con historial, grupos, listas de precio; mejoras de proveedores; estructura de crédito/puntos preparada | Venta con cliente identificado; historial |
| 9 | **Reportes** | Motor de consultas, reportes básicos y avanzados, antifraude, exportación Excel/PDF | Reportes cuadran con los escenarios de prueba |
| 10 | **Auditoría** | Consultas, historial por entidad, verificación de cadena de hash, reportes de auditoría | Manipulación directa en BD detectada por el verificador |
| 11 | **Backups** | Programados, al cierre, cifrados, destinos, verificación, restauración | Restauración completa en otra máquina |
| 11-B | ⚠️ **Facturación electrónica** (nueva) | Adaptador con proveedor tecnológico elegido, contingencia, notas crédito, habilitación | Documentos aceptados en ambiente de habilitación del ente fiscal |
| 12 | **Licenciamiento** | License Server (MVP sin cobro), activación, token, heartbeat, estados locales, límites | Activar, vencer, gracia, restringir y reactivar en pruebas con reloj simulado |
| 13 | **Instalador** | MSI/bootstrapper, PostgreSQL empaquetado, servicios, asistente inicial, emparejamiento de cajas, desinstalación | Instalación limpia en Windows 10/11 sin conocimientos técnicos |
| 14 | **Pruebas** | *Las pruebas se escriben en todas las fases.* Esta fase es de **endurecimiento**: rendimiento con volumen real, pruebas de fallos (corte de luz, red), actualización/rollback. **El piloto en un supermercado real pasa a después de la Fase 15** (sin pantallas no se puede pilotear) | Informe de pruebas |
| 15 | **Diseño visual** | UI de caja (optimizada para teclado/escáner) y backoffice | Producto usable |

**Cómo validarás cada fase sin interfaz:** documentación OpenAPI navegable (Scalar/Swagger), colecciones `.http` con los flujos de la fase, y un **informe de pruebas** con los escenarios ejecutados y las reglas `RN-xxx` cubiertas.

Ciclo por fase (según pediste): analizar → proponer → explicar → **tu aprobación** → implementar → probar → documentar → siguiente.

---

## T. Riesgos técnicos y decisiones que debemos tomar antes de comenzar

### Decisiones que necesito que apruebes o me respondas

| # | Decisión | Mi recomendación | Por qué importa ahora |
|---|---|---|---|
| **D1** | **País / normativa fiscal objetivo** (¿Colombia?) | Confirmar país inicial | Impuestos, tipos de identificación, documentos fiscales, numeración y plazos legales dependen de ello (ver R-01). |
| **D2** | **Stack** | **.NET 10 + PostgreSQL**; UI web en fase 15 | Define todo el código. Si tu experiencia es solo JavaScript, dímelo: la alternativa Node/TypeScript es viable con las precauciones del doc 03. |
| **D3** | Motor de BD único | PostgreSQL en todos los planes | Evita mantener dos motores. |
| **D4** | ¿Caja autónoma (vender con el servidor caído) en v1? | **No en v1**; diseñada desde ya, implementada después del piloto. Clientes de 1 caja no la necesitan (todo en uno) | Añade complejidad considerable (sincronización). |
| **D5** | Idioma de código y BD | Inglés en código/BD, español en documentación, UI y mensajes | Facilita contratar desarrolladores y usar librerías; la UI será 100 % en español. |
| **D6** | Política al vencer la licencia | Gracia de 7 días ⚙️ → `RESTRICTED` (no abre nuevas jornadas; datos siempre accesibles) | Es decisión comercial y de relación con clientes. |
| **D7** | Método de costeo | Promedio ponderado **por bodega** | Es lo habitual en retail colombiano y lo más simple de auditar. Alternativa: promedio por empresa. |
| **D8** | Venta sin stock | Bloqueada por defecto, configurable | En supermercados con inventario desordenado, muchos clientes pedirán permitirla: por eso es configurable, con alertas. |
| **D9** | Precios con impuesto incluido | Sí por defecto | Así se exhiben al consumidor final. |
| **D10** | Facturación electrónica: ¿proveedor tecnológico o conexión directa con DIAN? | **Proveedor tecnológico autorizado** al inicio | La conexión directa exige certificados, habilitación y mantenimiento normativo continuo. |
| **D11** | Hardware objetivo del piloto | Impresora térmica ESC/POS 80 mm genérica + báscula de una marca común en tu zona | Los drivers se priorizan según el hardware real de tus clientes. |
| **D12** | Control de versiones | Inicializar **git** en la Fase 1 (hoy la carpeta no es un repositorio) + repositorio remoto privado | Historial, respaldo del código y trabajo por fases. |

### Riesgos técnicos

| # | Riesgo | Impacto | Mitigación |
|---|---|---|---|
| **R-01** | **Normativa fiscal (Colombia):** la DIAN exige que el tiquete POS sea *documento equivalente electrónico* (Resolución 000165 de 2023 y calendario de implementación). Un POS que solo imprima tiquetes internos **no es comercializable** para responsables de IVA. | Alto | Arquitectura `Billing` separada desde el inicio (ya diseñada); fase 11-B dedicada; elegir proveedor tecnológico temprano; validar la normativa vigente con un contador antes de la fase 7. *Debemos verificar el estado normativo actual al llegar a esa fase.* |
| R-02 | Alcance muy grande para un equipo pequeño | Alto | Fases con entregables verificables; MVP comercial = fases 1–7 + 11 + 11-B + 12 + 13; lo demás en versiones siguientes. |
| R-03 | Diversidad de hardware (impresoras, básculas con protocolos propietarios) | Medio | Capa de drivers + simuladores; lista de hardware certificado para vender junto al software. |
| R-04 | PostgreSQL en PCs de clientes (antivirus, puertos ocupados, permisos, apagones) | Medio | Binarios empaquetados, puerto no estándar, rutas en ProgramData, exclusiones de antivirus documentadas, `fsync` activo, UPS recomendada, verificación en el arranque. |
| R-05 | Caída de la LAN / servidor en tiendas multi-caja | Medio | UPS + switch de calidad; caja autónoma en fase posterior (D4); procedimiento de contingencia documentado. |
| R-06 | Migraciones que fallen en campo | Alto | Backup obligatorio previo, DDL transaccional, pruebas con copias reales anonimizadas, canal beta, rollback. |
| R-07 | Piratería / evasión de licencia | Medio | Token firmado, fingerprint, detección de reloj, ofuscación. Aceptar un riesgo residual; el valor está en actualizaciones, soporte y facturación electrónica (que requieren conexión legítima). |
| R-08 | PCs antiguos en tiendas pequeñas (4 GB RAM, HDD) | Medio | Backend eficiente; UI ligera (no Electron); requisitos mínimos publicados; pruebas en hardware modesto. |
| R-09 | Windows 10 fuera de soporte (octubre 2025) pero aún muy usado en comercios | Bajo-medio | Soportar Windows 10 22H2 y 11 mientras .NET y WebView2 lo permitan; comunicar requisitos. |
| R-10 | Protección de datos personales (Ley 1581/2012) | Medio | Minimización de datos, consentimiento, cifrado de backups, exportación/rectificación. |
| R-11 | Errores de cálculo en dinero/impuestos | Alto | `decimal`, `SaleCalculator` puro con cientos de casos de prueba, conciliación automática venta ↔ caja ↔ kardex ↔ fiscal. |
| R-12 | Costos no técnicos: certificado de firma de código, hosting del servidor de licencias, proveedor fiscal, soporte | Medio | Presupuestarlos antes de la fase 12–13. |
| R-13 | Concurrencia entre cajas (stock, secuencias) | Medio | Bloqueos ordenados, pruebas de carga concurrente en la fase 7. |

### Advertencias arquitectónicas (decisiones que serían costosas de cambiar después)

1. **IDs enteros autoincrementales** → imposibilitan la caja offline y la sincronización. Por eso UUID v7 desde el día 1.
2. **Carrito solo en memoria de la UI** → pérdida de ventas y agujeros antifraude. Por eso venta persistida.
3. **Stock como campo editable** → imposible auditar. Por eso kardex.
4. **Venta = factura** → reescritura al integrar facturación electrónica. Por eso `Billing` separado.
5. **Licencia con verificación online obligatoria** → bloqueo de ventas. Por eso token firmado offline.
6. **SQLite compartido por red** para multi-caja → corrupción de datos. Por eso PostgreSQL + API.
7. **`float` para dinero** → descuadres. Por eso `decimal`/`numeric`.
