# Fase 14 · Endurecimiento y pruebas — Propuesta

> Estado: **APROBADA** (con las recomendaciones de la §9) · 2026-09-29 · [informe](fase-14-informe.md)
> Requisitos previos: Fases 1 a 13. Las pruebas unitarias, de integración y de arquitectura ya se escriben en cada fase; esta fase
> **no las repite**.
> Base:
> - plan [12 §S, fase 14](../12-plan-riesgos-decisiones.md): "endurecimiento: rendimiento con volumen real, pruebas de fallos (corte
>   de luz, red), actualización/rollback, piloto en un supermercado real";
> - riesgos del doc [01](../01-analisis-producto.md), en especial los n.º 13 y 14;
> - tu forma de trabajo: **tú haces las pruebas; yo construyo las herramientas y verifico la coherencia**.

**Convenciones:**
- 🔒 = decisión difícil de cambiar después.
- ⚙️ = configurable.
- **§** = "sección".

## 0. Objetivo y alcance

Después de esta fase sabemos, **con números y no con suposiciones**:
- cuánto aguanta el sistema: cuántos productos, cuántas cajas simultáneas y cuántos años de ventas;
- qué pasa cuando se va la luz, se cae la red o se llena el disco;
- que una actualización defectuosa nunca deja la tienda sin vender.

Lo que falle se **corrige en esta fase**.

**Entregable del plan:** informe de pruebas + piloto.

**Propuesta importante:** separar el **piloto** en un supermercado real y hacerlo **después de la Fase 15**. Sin pantallas, un cajero
no puede usar el sistema, así que el piloto hoy sería imposible. Esta fase queda como **endurecimiento técnico** (pregunta 1).

| Incluido | Excluido (fase) |
|---|---|
| **Generador de datos realistas**: ⚙️ 30.000 productos, 2 años de ventas, 5 cajas, clientes, compras, lotes | El piloto en una tienda real (después de la Fase 15, pregunta 1) |
| **Pruebas de carga** con metas medibles (§2) y **prueba de resistencia** de 72 h | Pruebas de usabilidad de las pantallas (Fase 15) |
| **Pruebas de fallos**: corte de luz del servidor y de PostgreSQL a mitad de una venta, caída de la LAN, sin Internet, disco lleno, reloj cambiado | Pruebas de penetración contratadas a un tercero (antes de vender, recomendado) |
| **Actualización y vuelta atrás** con una copia de BD grande y migraciones reales | |
| **Revisión de seguridad** con lista OWASP ASVS nivel 1, dependencias vulnerables, secretos en el repositorio y permisos de carpetas del instalador | |
| **Correcciones** de todo lo que falle y **ajustes** (índices, consultas, parámetros de PostgreSQL por tamaño de equipo) | Cambios de funcionalidad (van a su fase) |

Se entrega en **cuatro bloques** con una sola aprobación:
- **14.1** Datos y herramientas.
- **14.2** Rendimiento.
- **14.3** Fallos y actualización.
- **14.4** Seguridad, correcciones e informe.

---

## 1. Equipo de referencia 🔒

Las metas se miden en un **PC modesto de tienda**, no en el equipo de desarrollo:
- Windows 10/11;
- Intel Core i3 de 8.ª generación o equivalente;
- 8 GB de RAM;
- disco SSD SATA.

El mínimo que se publica es 4 GB, con metas más flojas (pregunta 2). Si no tienes uno así, se simula limitando los núcleos y la
memoria del contenedor de PostgreSQL. Esa cifra vale como indicio, no como garantía.

## 2. Metas de rendimiento (percentil 95, con 5 cajas vendiendo a la vez sobre 2 años de datos)

| Operación | Meta |
|---|---|
| Escanear un código y agregar la línea | < 100 ms |
| Buscar un producto por nombre (30.000 productos, trigramas) | < 200 ms |
| Finalizar la venta con pagos combinados | < 300 ms |
| Abrir o cerrar la jornada (cierre Z con 500 ventas) | < 2 s |
| Reporte de ventas de un mes (xlsx) | < 5 s |
| Backup de una BD de ⚙️ 5 GB (cifrado y verificado) | < 10 min, sin frenar las ventas más del 20 % |
| Verificación incremental de la auditoría | < 30 s |
| Arranque del servidor | < 15 s |

Si una meta no se cumple, se corrige con índices, consultas o caché, y se deja escrito en el informe.

## 3. Decisiones principales

| # | Decisión | Recomendación |
|---|---|---|
| D14-01 | Herramienta de carga | **Consola propia en C#** (`tools/Pos.LoadTest`): reutiliza los DTOs y el flujo real de caja (sesión, escaneo, pago), sin licencias de terceros. k6 es buena, pero obliga a reescribir los flujos en JavaScript. NBomber cobra el uso comercial |
| D14-02 | Generador de datos | `tools/Pos.DataGen`: crea los datos **por la API** para los maestros (así respetan todas las reglas) y **por SQL masivo** solo para el histórico de ventas. Luego verifica con `verify-stock` y `verify-audit` que todo cuadra |
| D14-03 | Pruebas de fallos | Guiones reproducibles (`tools/scripts/chaos-*.ps1`): matar el proceso del servidor o de PostgreSQL a mitad de una venta, desconectar el adaptador de red, llenar el disco con un archivo, adelantar o atrasar el reloj. Después de cada una se comprueba lo mismo: **ninguna venta a medias, stock y caja cuadran, la auditoría verifica** |
| D14-04 | Resistencia | 72 h de ventas continuas a ritmo realista (⚙️ 1 venta/min por caja) con los backups programados corriendo. Se vigila la memoria del servidor, las conexiones y el tamaño de la BD. **La ejecutas tú**; yo dejo el guion listo |
| D14-05 | Ajustes de PostgreSQL | Perfil por memoria del equipo (4, 8 o 16 GB): `shared_buffers`, `work_mem`, `effective_cache_size`, `autovacuum`. Lo aplica el instalador (Fase 13) |
| D14-06 | Seguridad | Lista ASVS nivel 1 aplicada a la API local, el portal 12-A y el instalador; `dotnet list package --vulnerable` en `build.ps1`; revisión de permisos de `ProgramData`; revisión de los registros para que no filtren datos personales (Fase 10, `[PersonalData]`) |

## 4. Escenarios de fallos (cada uno con resultado esperado)

| Escenario | Resultado esperado |
|---|---|
| Corte de luz del servidor a mitad de `finalizar venta` | La venta queda completa o no existe; nunca a medias. La caja la recupera al volver |
| PostgreSQL se detiene con 5 cajas vendiendo | Error claro en las cajas; al volver, cero duplicados (idempotencia) |
| Se cae la LAN en Multicaja | La caja avisa de inmediato; la venta en curso no se pierde |
| Sin Internet 10 días | Ventas normales; licencia `VALID_OFFLINE` → `GRACE` según el token; backups en la nube encolados y enviados al volver |
| Disco lleno | El backup falla con alerta; las ventas siguen mientras PostgreSQL tenga espacio; aviso temprano al ⚙️ 90 % |
| Reloj atrasado 2 días | `CLOCK_JUMP_DETECTED` y licencia `RESTRICTED` sin cortar la jornada abierta (12-B) |
| Actualización con una migración que falla | Vuelta atrás automática; la tienda sigue vendiendo en la versión anterior (13) |
| Restaurar un backup de 5 GB en otro PC | Tienda operando en menos de 1 h (11 y 13) |

## 5. Validación de la fase (según tu forma de trabajo)

- **Yo:**
  - construyo el generador, la consola de carga y los guiones de fallos;
  - corro **cada uno una vez en corto** (minutos, no horas) para comprobar que funcionan;
  - hago las correcciones que salgan.
- **Tú:**
  - corres las pruebas largas (carga completa, 72 h);
  - corres los fallos en un equipo de tienda real;
  - anotas los números en la plantilla del informe.
- Con tus resultados cierro el informe y decidimos juntos si algo se corrige antes de la Fase 15.

## 6. Riesgos

| Riesgo | Mitigación |
|---|---|
| Las metas no se cumplen en un PC modesto | Se detectan antes de vender; hay espacio para índices, caché del catálogo en el servidor y ajustes de PostgreSQL |
| Los datos generados no se parecen a los reales | Distribuciones realistas: pocos productos venden mucho, horas pico, canastas de 1 a 40 artículos. En el piloto (después de la Fase 15) se confirma con datos reales |
| Las pruebas de fallos dañan el equipo de desarrollo | Se corren en una máquina virtual o *Windows Sandbox* con la instalación de la Fase 13 |

## 7. Estructura

```
tools/Pos.DataGen          Generador de datos (API + SQL masivo) y verificación final
tools/Pos.LoadTest         Carga con el flujo real de caja; reporta p50/p95/p99 y errores en CSV
tools/scripts/chaos-*.ps1  Guiones de fallos con verificación posterior
docs/fases/fase-14-informe.md   Plantilla con metas, resultados, correcciones y pendientes
```

## 8. Criterios de aceptación

1. Las herramientas generan 2 años de datos de una tienda de 30.000 productos y la verificación del kardex, la caja y la auditoría
   queda en verde.
2. Todas las metas de la §2 se cumplen en el equipo de referencia, o quedan justificadas en el informe con su plan.
3. Todos los escenarios de la §4 dan el resultado esperado.
4. Sin dependencias con vulnerabilidades conocidas altas o críticas; lista ASVS nivel 1 completa, con sus excepciones justificadas.
5. Informe con números, correcciones hechas y pendientes antes del piloto.

## 9. Preguntas para ti (la opción recomendada va primero)

1. **Piloto:** ¿lo **separamos y lo hacemos después de la Fase 15** (**recomendado**: sin pantallas no se puede pilotear), o lo dejamos
   en esta fase y esperamos la interfaz?
2. **Equipo de referencia:** ¿tienes o puedes conseguir un **PC modesto de tienda** (i3, 8 GB, SSD) para las pruebas (**recomendado**),
   o simulamos con límites en el contenedor?
3. **Tamaño de referencia:** ¿**30.000 productos, 5 cajas, 2 años** (**recomendado**: un supermercado mediano), o me das las cifras
   de tu cliente objetivo?
4. **Prueba de penetración externa:** ¿la **contratamos antes de vender** (**recomendado**), o basta la revisión interna ASVS?
