# Fase 14 · Endurecimiento y pruebas — Informe

- **Estado:** Herramientas implementadas — **las corridas largas te tocan a ti** (plantilla de resultados en la §3) · 2026-09-30
- **Propuesta:** [fase-14-propuesta.md](fase-14-propuesta.md) (aprobada con las recomendaciones de la §9: el **piloto pasa a después de la
  Fase 15**, equipo de referencia modesto, 30.000 productos / 5 cajas, prueba de penetración externa antes de vender)
- **Revisión de seguridad:** [seguridad-revision.md](../seguridad-revision.md)

## 1. Qué se entregó

| Bloque | Entregado |
|---|---|
| 14.1 Datos y herramientas | `tools/Pos.LoadTest`: `seed` prepara una tienda de tamaño real **solo por la API** (configuración inicial, productos con EAN-13, existencias iniciales según Zipf, cajas, cajeros y equipos emparejados en Multicaja); `run` pone a vender todas las cajas a la vez (escanear, agregar, cobrar en efectivo o débito, buscar, cierre con arqueo) y al final pide los reportes del mes en Excel; imprime p50/p95/p99 frente a las metas y escribe un CSV. Las 2 primeras ventas de cada caja no se miden (calentamiento) |
| 14.2 Rendimiento | Metas del p95 incorporadas a la herramienta (escanear 100 ms, buscar 200 ms, cobrar 300 ms, cerrar jornada 2 s, reporte del mes 5 s). Perfil de PostgreSQL según la memoria del equipo (4, 8 o 16 GB o más) escrito por el instalador |
| 14.3 Fallos | `tools/scripts/chaos.ps1`: matar el servidor o PostgreSQL con cajas vendiendo, atrasar el reloj, cortar la red de una caja y llenar el disco. Después de cada uno ejecuta `Pos.Server.Migrator verify-consistency` (nuevo): ventas cobradas con su salida de inventario y su movimiento de caja, pagos que suman el total, ninguna venta abierta en una jornada cerrada, kardex y auditoría |
| 14.4 Seguridad y correcciones | Revisión ASVS nivel 1; `build.ps1` falla si hay dependencias con vulnerabilidades altas o críticas (hoy ninguna); **corrección:** permisos de la carpeta de datos solo para SYSTEM y Administradores |

## 2. Cómo correr las pruebas largas (tú)

1. Instale la versión en el equipo de referencia (i3, 8 GB, SSD) o en una máquina virtual, en modo **Servidor (Multicaja)**.
2. Prepare la tienda (tarda ~10 min con 30.000 productos):
   `dotnet run --project tools/Pos.LoadTest -c Release -- seed --products 30000 --terminals 5 --owner dueno --password <clave>`.
3. Carga de 30 minutos: `dotnet run --project tools/Pos.LoadTest -c Release -- run --minutes 30 --close --csv carga-30min.csv`.
4. Volumen: el mismo `run` durante la noche (`--minutes 480`) acumula del orden de 100.000 ventas. Las fechas quedan en los días de la
   corrida: los reportes de un rango incluyen todo el volumen (el peor caso).
5. Resistencia de 72 h: `run --minutes 4320 --think-ms 12000` (≈ 1 venta por minuto por caja). Vigile la memoria del servicio y el
   tamaño de la BD.
6. Fallos: `tools/scripts/chaos.ps1 -Scenario KillServer` (y los demás escenarios), como Administrador y en la máquina de prueba.
7. Anote los números en la §3.

## 3. Resultados

### Corrida corta hecha aquí (coherencia, no rendimiento)
Servidor en memoria, 300 productos, 3 cajas Multicaja con equipo emparejado, 15 ventas por caja y cierre. Es la prueba
`LoadToolTests`.

| Operación | Cant. | Errores | p50 ms | p95 ms | Meta p95 |
|---|---|---|---|---|---|
| escanear | 201 | 0 | 22 | 24 | 100 ✓ |
| cobrar | 39 | 0 | 50 | 81 | 300 ✓ |
| cerrar jornada | 3 | 0 | 22 | 71 | 2.000 ✓ |
| reporte del mes (xlsx) | 1 | 0 | 81 | 81 | 5.000 ✓ |

Después: `ConsistencyChecks` sin hallazgos, kardex = saldos, auditoría íntegra. Primera corrida sin descontar el calentamiento: "cobrar"
dio p95 de 1.247 ms por las 3 primeras ventas (una por caja, carga del código). Por eso la herramienta descarta las 2 primeras ventas
de cada caja.

### Equipo de referencia (pendiente, tú)

| Prueba | Resultado |
|---|---|
| Carga 30 min, 5 cajas, 30.000 productos | _p95 por operación_ |
| 8 h de volumen | _ventas totales · tamaño de la BD · reporte del mes_ |
| Resistencia 72 h | _memoria inicial/final del servicio · errores_ |
| Backup de la BD grande | _minutos · efecto en las ventas_ |
| KillServer · KillDatabase · Clock · Network · DiskFull | _consistencia OK / hallazgos_ |

## 4. Validación hecha

- Compilación sin errores ni advertencias; `LoadToolTests` ✅ (seed + run + cierre + consistencia); `dotnet list package --vulnerable`
  sin hallazgos; sintaxis de `chaos.ps1` y `build.ps1` verificada.
- **No ejecutado aquí:** los escenarios de `chaos.ps1` (necesitan una instalación real como Administrador en una máquina de prueba) y
  las corridas largas.

## 5. Cambios frente a la propuesta

- El histórico se genera **por la API real** (`run`), no con SQL masivo: así cuadran siempre el kardex, la caja, la numeración y la auditoría.
  Las fechas quedan en los días de la corrida; las metas de los reportes se miden con todo el volumen en el rango (el peor caso).
- Los clientes no se generan: las ventas son a consumidor final (los clientes no cambian la carga de las operaciones medidas).
- **Piloto** en un supermercado real: después de la Fase 15 (aprobado).
