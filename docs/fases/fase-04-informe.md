# Fase 4 · Productos e inventario — Informe

> Estado: **IMPLEMENTADA — pendiente de tu validación** · 2026-09-28
> Documentos: [propuesta aprobada](fase-04-propuesta.md) · ADR [0019](../adr/0019-kardex-inmutable-y-costo-promedio.md) ·
> [0020](../adr/0020-precios-e-impuestos-con-vigencia.md) · [0021](../adr/0021-codigos-de-barras-y-codigos-internos.md) ·
> [0022](../adr/0022-cambios-por-campo-para-sincronizar.md)

## 1. Resultado frente a los criterios de aceptación (§14)

| Criterio | Resultado | Evidencia |
|---|---|---|
| Catálogo completo por API: categorías, marcas, productos, presentaciones, códigos, impuestos, listas y precios con vigencia e historial | ✅ | `CatalogApiTests` (6 escenarios) y 40 pruebas unitarias del dominio |
| Escaneo: código normal, UPC con y sin 0, presentación, etiqueta de peso y de precio, SKU; `sellable` con motivos | ✅ | `Productos_codigos_presentaciones_y_escaneo`, `Etiquetas_de_bascula_por_peso_y_por_precio`: 1,250 kg × $4.980 = $6.225 |
| Importación de productos, precios y saldo inicial con vista previa, errores por fila y aplicación atómica | ✅ | CSV y Excel; un archivo con una fila mala no aplica nada; al corregirlo se crean productos, categorías y marcas en una transacción |
| Kardex inmutable; saldo = Σ kardex y valor = Σ valores; `verify-stock` detecta una alteración directa | ✅ | Privilegios + disparador (ni el dueño de las tablas puede modificarlo); 10.000 movimientos aleatorios sin diferencia de valor; un saldo alterado en la BD se detecta, queda como incidente crítico y se reconstruye con motivo |
| Ajustes con motivo y aprobación por umbral (quien aprueba ≠ quien crea); conteo con varios contadores, ciego y sin cerrar la tienda; traslado con faltante | ✅ | `InventoryApiTests`: aprobación por otro administrador, `INVENTORY.SELF_APPROVAL`; conteo ciego con una avería durante el conteo (teórico 100 → esperado 98 → contado 99 → +1); traslado 10 → recibido 9 → pérdida 1 en tránsito |
| 20 publicaciones concurrentes sin *deadlocks*; rendimiento de escaneo y búsqueda | ✅ | 30 ajustes simultáneos (20 sobre el mismo producto y 10 cruzando dos productos en orden opuesto): todos publicados, saldo exacto. Con 50.000 productos y 100.000 códigos: **escaneo p95 10,7 ms** (meta < 20) y **búsqueda p95 52 ms** (meta < 100) |
| Cambios de maestros generan eventos `SYNC` por campo con versión base | ✅ | `Un_cambio_de_producto_viaja_a_la_sincronizacion_solo_con_los_campos_modificados`: `baseVersion` 1 → `version` 2, solo `name` y `search_text` |
| Permisos nuevos protegen todos los endpoints; costos ocultos sin `inventory.cost.view` | ✅ | Pruebas de 401/403 sobre todos los endpoints; la cajera ve existencias y kardex sin costos |
| `build.ps1` en verde; cobertura de los dominios de Catalog e Inventory ≥ 90 % | ✅ | Ver §3 |
| Docs 04, 05 y 07, ADRs e informe | ✅ | ADR-0019 a 0022; notas de implementación en 04, 05 y 07; licencias; `http/fase-04.http` |

## 2. Qué se construyó

```
src/Server/Pos.Server.Migrations   V007 número de nodo · V008 catálogo (12 tablas + unidades) · V009 inventario (13 tablas)
                                   R__ref__units_of_measure · permisos (15 nuevos) · privilegios (kardex solo SELECT/INSERT)
src/Server/Pos.Server.Migrator     + verify-stock · rebuild-stock
src/BuildingBlocks
  Pos.SharedKernel                 TextNormalization (búsqueda sin tildes)
  Pos.Application.Abstractions     ICompanyInitializer · ITabularFileReader · DecimalParsing ("4.980", "1,25")
  Pos.Api.Abstractions             UploadedFile (el archivo se lee después de verificar la sesión)
  Pos.Infrastructure               TabularFileReader (CSV UTF-8/Windows-1252 y .xlsx con MiniExcel) · cambios por campo → SYNC
                                   · inicializadores por empresa al arrancar
src/Modules/Catalog                Categorías (árbol de 4 niveles), marcas, impuestos con tarifas por fecha, productos,
                                   presentaciones, códigos (GTIN, internos 29…), reglas de báscula, listas y precios con
                                   vigencia, escaneo, búsqueda, importación con vista previa
src/Modules/Inventory              Kardex (IInventoryPosting), saldos con valor, motivos, ajustes con aprobación, saldo inicial
                                   desde archivo, conteos, traslados, políticas de reposición, verificación diaria y manual
src/Modules/Organization           Número de nodo; directorio de bodegas para otros módulos; el asistente ejecuta los
                                   inicializadores de los módulos
src/Modules/Identity               Los 7 roles de sistema reciben los permisos nuevos (el cajero consulta productos, existencias
                                   y registra conteos)
http/fase-04.http
```

Datos iniciales de cada empresa (también en las instalaciones existentes, al arrancar): categoría "General", lista GENERAL,
IVA 19 % / 5 % / exento / excluido con tarifa desde 2017-01-01, INC bolsa / IBUA / ICUI inactivos y sin tarifa, reglas de
báscula 20 (peso) y 23 (precio) inactivas y 9 motivos de ajuste.

## 3. Pruebas y cobertura

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| Pos.ArchitectureTests | 21 | R1–R7 + **R8**: solo el módulo Inventory escribe el kardex y los saldos |
| Pos.Database.Tests | 52 | + exclusión de vigencias de precios, códigos únicos, kardex de solo inserción (incluso para el dueño), dirección por tipo de movimiento |
| Pos.Infrastructure.UnitTests | 48 | + lector CSV/Excel (separadores, comillas, Windows-1252, .xlsx), números "4.980" y "1,25", normalización |
| Pos.Modules.Catalog.UnitTests *(nuevo)* | 40 | GTIN y dígitos de control, UPC→EAN-13, códigos internos, báscula, productos, categorías, impuestos, vigencias |
| Pos.Modules.Inventory.UnitTests *(nuevo)* | 25 | Costo promedio con valor (**10.000 movimientos aleatorios**), ajustes, aprobación, conteos, traslados |
| Pos.Modules.Identity.UnitTests | 33 | Sin cambios |
| Pos.Modules.Organization.UnitTests | 31 | Sin cambios |
| Pos.Server.IntegrationTests | 91 | + 15 escenarios de la Fase 4 (catálogo, inventario, concurrencia, rendimiento) |
| Pos.SharedKernel.UnitTests | 70 | Sin cambios |
| **Total** | **411** | Fase 3: 303 |

Cobertura de líneas (combinada entre proyectos):

| Ensamblado | Cobertura | Mínimo |
|---|---|---|
| Pos.Modules.Catalog.Domain | 99,2 % | 90 % ✅ |
| Pos.Modules.Inventory.Domain | 100 % | 90 % ✅ |
| Pos.Modules.Identity.Domain / Organization.Domain | 99,6 % / 99 % | 90 % ✅ |
| Pos.Infrastructure | 94,7 % | 85 % ✅ |
| Pos.Server.Migrations | 93,7 % | 85 % ✅ |
| Pos.SharedKernel | 99,6 % | 95 % ✅ |
| Catalog.Api / Catalog.Infrastructure | 98,4 % / 95,5 % | — |
| Inventory.Api / Inventory.Infrastructure | 99,2 % / 87,5 % | — |
| Catalog.Application / Inventory.Application | 75,4 % / 78,6 % | — (ramas de error poco frecuentes) |

## 4. Desviaciones respecto de la propuesta (y por qué)

| # | Propuesta | Implementado | Motivo |
|---|---|---|---|
| 1 | Importación de hasta 20.000 filas ⚙️ | **5.000 filas** por archivo (`catalog.import_max_rows`, 100–5.000) | La importación es atómica y el rol de la aplicación tiene un límite de 30 s por transacción (horizonte de sellado de la auditoría). 5.000 productos se aplican en ~11 s; archivos más grandes se dividen |
| 2 | Saldo inicial como ajuste con aprobación por umbral | Sin umbral, pero solo lo publica quien tiene `inventory.adjustment.approve` | Un saldo inicial casi siempre supera el umbral: exigir a otra persona sería fricción sin control adicional |
| 3 | Importación de saldo inicial con vista previa | Archivo → errores por fila o **ajuste en borrador** (que es la vista previa) | Reutiliza el flujo de ajustes: se revisa y se publica igual que cualquier ajuste |
| 4 | El conteo genera un ajuste | Publica sus diferencias directamente en el kardex (`COUNT_ADJUSTMENT_IN/OUT`, origen COUNT) | Mismo efecto y trazabilidad (el kardex enlaza al conteo) sin un documento intermedio |
| 5 | — | Productos de báscula solo en **kilogramos** | Las etiquetas traen el peso en gramos/kilos; la libra colombiana (500 g) se maneja con presentaciones |
| 6 | — | Políticas de reposición con el permiso `inventory.adjustment.manage` | No justificaba un permiso nuevo |
| 7 | — | El servicio de consultas de inventario usa la conexión de la petición | Dentro de un comando debe ver lo que ese comando ya cambió (p. ej. el saldo reconstruido) |
| 8 | — | Endpoints de archivo leen el formulario dentro del handler | Con el archivo como parámetro, el enrutador respondía 415 antes de verificar la sesión |
| 9 | — | Contenedor de pruebas con `max_connections=500` y pool de 40 por servidor | Con 15 servidores de prueba más en paralelo se agotaban las 100 conexiones por defecto (solo afecta a las pruebas) |

## 5. Limitaciones conocidas y pendiente

1. **Lotes y vencimientos** (RN-INV-08/09): tabla y columna listas; la gestión llega con las compras (Fase 5).
2. **Traslados entre sucursales**: con la sincronización (usarán la misma bodega de tránsito).
3. **Promociones, combos y kits**: Fases 7/8 (el tipo `KIT` quedó reservado).
4. **Impuestos saludables y bolsa**: modelados e inactivos; cada empresa carga sus valores con su contador. Los códigos DIAN
   de los tributos (salvo IVA `01`) se validarán con Factus en la Fase 11-B.
5. **Sincronización**: los hijos "owned" de Identity (roles de un usuario) aún no generan su propio evento por campo.
6. Pendientes externos: validación del Servicio de Windows de la Fase 1 y consulta a Factus antes de la Fase 7.

## 6. Cómo validarlo

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1          # compila, pruebas y cobertura → BUILD OK
powershell -ExecutionPolicy Bypass -File .\tools\scripts\dev-db.ps1   # aplica las migraciones 007–009
dotnet run --project src\Server\Pos.Server.Host              # http://localhost:5480
```

Luego ejecuta `http/fase-04.http` en orden. Verificación del inventario desde el servidor:

```powershell
dotnet run --project src\Server\Pos.Server.Migrator -- verify-stock --connection "Host=127.0.0.1;Port=5488;Database=pos;Username=pos_app;Password=pos-dev-app-password"
```
