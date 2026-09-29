# Fase 7 · POS y ventas — Informe

> Estado: **IMPLEMENTADA — pendiente de tu validación** · 2026-09-29 · Rama `fase-7-ventas`
> Documentos: [propuesta aprobada](fase-07-propuesta.md) · ADR [0030](../adr/0030-venta-persistida-y-motor-de-calculo-puro.md) ·
> [0031](../adr/0031-existencias-sin-saldo-negativo-y-ajuste-rapido.md) · [0032](../adr/0032-pagos-redondeo-del-efectivo-y-cambio.md) ·
> [0033](../adr/0033-anulacion-y-cambios-sin-devolucion-de-dinero.md) · [0034](../adr/0034-promociones-una-por-linea-la-mas-favorable.md) ·
> [0035](../adr/0035-billing-comprobante-interno-y-proveedor-fiscal-nulo.md) · [0036](../adr/0036-agente-de-caja.md)

## 1. Resultado frente a los criterios de aceptación (§14)

| Criterio | Resultado | Evidencia |
|---|---|---|
| Venta completa por API: escaneo normal, presentación y báscula; promociones automáticas; descuentos solo con autorización; suspender/recuperar | ✅ | `SalesApiTests`: SKU dos veces (suma en la línea), código de barras, etiqueta de báscula por peso ($6.225 con IVA excluido); la cajera recibe `AUTH.AUTHORIZATION_REQUIRED` y el supervisor autoriza con código y PIN (un solo uso); `SalesFlowTests`: suspender con etiqueta, recuperar en la misma caja, cancelar con autorización sin consumir número |
| Sin existencias no se vende; ajuste rápido autorizado; lote vencido solo con autorización; tablero de próximos a vencer | ✅ | 51 arroces con 50 en existencia → `SALES.INSUFFICIENT_STOCK`; ajuste rápido autorizado y la venta sigue; lote vencido → `SALES.EXPIRED_LOT_REQUIRES_AUTHORIZATION` y con autorización queda marcado en la línea; FEFO descuenta primero el vencido; `GET /inventory/lots?expiring=true` |
| Pagos combinados con cambio solo en efectivo; referencia obligatoria; redondeo del efectivo | ✅ | Transferencia por encima del saldo → `SALES.NON_CASH_OVERPAYMENT`; sin referencia → `SALES.REFERENCE_REQUIRED`; $16.625 con transferencia $10.000 + efectivo $10.000 → redondeo +$25, cambio $3.350; `PaymentAllocatorTests` |
| Completar atómico e idempotente; número sin huecos por caja; kardex (FEFO) y caja por medio | ✅ | Misma `Idempotency-Key` devuelve la misma venta; otra clave → `SALES.ALREADY_COMPLETED`; costo de la línea del kardex; reporte X con el efectivo y la transferencia esperados |
| Anulación con la jornada abierta; cambios por igual o mayor valor, sin reintegro de dinero | ✅ | Anular con la jornada abierta revierte kardex, caja y comprobante; con la jornada cerrada → `SALES.VOID_NOT_ALLOWED`; cambio con crédito de $5.500 y diferencia de $2.000 en efectivo; llevar menos → `SALES.EXCHANGE_BELOW_CREDIT`; el medio `CAMBIO` no aparece en el arqueo; garantía solo del propietario (auditoría `CRITICAL`) |
| Comprobante interno por venta; `IFiscalProvider` listo para 11-B | ✅ | `INTERNAL_RECEIPT` / `NOT_REQUIRED` por venta; `FiscalDocumentTests`; `NullFiscalProvider` registrado |
| Cierre de caja bloqueado con ventas abiertas o suspendidas | ✅ | `start-closing` → `409 CASH.OPEN_SALES` hasta resolverlas |
| Agente de caja: tiquete ESC/POS, cajón y página de prueba | ✅ (transporte de archivo) | `Pos.Printing.UnitTests` (bytes ESC/POS exactos, líneas ≤ 42 columnas) y `Pos.Terminal.Agent.Tests` (API, transportes, protección local); impresora real cuando llegue el hardware |
| "Día de operación" de 500 ventas cuadra al centavo; metas de rendimiento | {{PRUEBAS}} | {{PRUEBAS}} |
| Permisos en todos los endpoints; auditoría; `build.ps1` en verde; cobertura de los dominios nuevos ≥ 90 % | {{PRUEBAS}} | Ver §3 |
| Docs 04, 05 y 08, ADRs e informe | ✅ | ADR-0030 a 0036; notas de implementación en 04, 05 y 08; `http/fase-07.http` |

## 2. Qué se construyó

```
src/Server/Pos.Server.Migrations   V016 promociones · V017 ventas (ventas, líneas, impuestos, pagos, descuentos, cambios) · V018 billing
                                   · V019 org.terminal_devices; permisos nuevos, tipo de documento PROMOTION, medio EXCHANGE_CREDIT,
                                   privilegios de los esquemas nuevos
src/BuildingBlocks/Pos.Printing    Tiquete neutro (JSON polimórfico), diseño 42/32 columnas y generador ESC/POS (PC850, CODE128, QR,
                                   corte, pulso del cajón)
src/Modules/Sales (nuevo)          Venta persistida, SaleCalculator, PaymentAllocator, existencias, cobro atómico e idempotente,
                                   anulación, cambios de mercancía, garantía, tiquete; IOpenSalesProbe para el cierre
src/Modules/Promotions (nuevo)     Cinco tipos, vigencia, días, horario y sucursales; borrador → activa → pausada → terminada;
                                   simulador y reporte; rol PROMOTIONS_MANAGER
src/Modules/Billing (nuevo)        Comprobante interno, eventos de solo inserción, IFiscalProvider con proveedor nulo, reintento
src/Modules/Cash                   ICashRegister.RecordSaleMovementsAsync y GetOpenSessionAsync; medio CAMBIO; CASH.OPEN_SALES
src/Modules/Inventory              Ajuste rápido autorizado (POST /inventory/quick-adjustments)
src/Modules/Catalog                Escaneo con precio abierto y presentación para la venta
src/Modules/Organization           Impresora de tiquetes por caja (GET/PUT /organization/terminals/{id}/receipt-printer)
src/Modules/Identity               Roles: cajero, supervisor de caja, encargado de promociones; administrador sin la garantía
src/Terminal/Pos.Terminal.Agent    Agente de caja (servicio de Windows, localhost:5490): /print, /drawer/open, /status, /test-page
src/Server/Pos.Server.Host         Consola de prueba en wwwroot/prueba (solo desarrollo)
http/fase-07.http
```

Roles: la **cajera** vende, elimina líneas, reimprime, consulta ventas (para buscar la de un cambio) y crea clientes; el **supervisor
de caja** además autoriza descuentos, precios abiertos, cancelaciones, anulaciones, lotes vencidos, cambios y ajustes rápidos; el
**encargado de promociones** administra promociones; el **administrador** tiene todo menos el reintegro por garantía, que es solo del
**propietario**.

## 3. Pruebas y cobertura

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| Pos.ArchitectureTests | {{PRUEBAS}} | R1–R8 con los ensamblados de ventas, promociones, facturación y `Pos.Printing` |
| Pos.Database.Tests | {{PRUEBAS}} | {{PRUEBAS}} |
| Pos.Modules.Sales.UnitTests *(nuevo)* | {{PRUEBAS}} | `SaleCalculator` (cada tipo de promoción, la más favorable sin acumular, descuentos, prorrateo, impuestos incluidos y fijos, propiedad Σ líneas = total), `PaymentAllocator`, estados de `Sale`, crédito de `CustomerReturn` |
| Pos.Modules.Promotions.UnitTests *(nuevo)* | {{PRUEBAS}} | Validación por tipo (combo sin productos repetidos), vigencia, días, horario nocturno, sucursales, transiciones |
| Pos.Modules.Billing.UnitTests *(nuevo)* | {{PRUEBAS}} | Comprobante interno, anulación, eventos, estados del documento electrónico |
| Pos.Printing.UnitTests *(nuevo)* | {{PRUEBAS}} | Bytes ESC/POS exactos, diseño a 42/32 columnas, JSON polimórfico del tiquete |
| Pos.Terminal.Agent.Tests *(nuevo)* | {{PRUEBAS}} | API local (loopback, `Host`, orígenes, CORS/PNA, límites), configuración de la impresora, transportes |
| Pos.Server.IntegrationTests | {{PRUEBAS}} | + escenarios de la Fase 7 (`Phase7/`: venta completa, existencias y vencidos, suspender/cancelar/cierre, anulación, cambio y garantía, promociones) |
| Demás proyectos | {{PRUEBAS}} | Sin cambios de la Fase 6 |
| **Total** | **{{PRUEBAS}}** | Fase 6: 481 |

Cobertura de líneas (combinada entre proyectos):

| Ensamblado | Cobertura | Mínimo |
|---|---|---|
| Pos.Modules.Sales.Domain | {{PRUEBAS}} | 90 % |
| Pos.Modules.Promotions.Domain | {{PRUEBAS}} | 90 % |
| Pos.Modules.Billing.Domain | {{PRUEBAS}} | 90 % |
| Pos.Printing | {{PRUEBAS}} | 90 % |

## 4. Decisiones y desviaciones respecto de la propuesta (y por qué)

| # | Propuesta | Implementado | Motivo |
|---|---|---|---|
| 1 | Una promoción **por unidad** (D7-16) | Una promoción **por línea**: la línea que participa en una promoción no recibe otra | Resultado más simple de explicar en el tiquete y determinista (gana la que más descuenta; empate, menor Id) |
| 2 | Tipo `BUY_X_PAY_Y` | `MULTI_BUY` en código y BD | El conversor de enumeraciones no separa bien `BuyXPayY` |
| 3 | Impuesto fijo por unidad (bolsa) | Con precio que incluye impuestos, el impuesto fijo **se limita a lo cobrado** | Con un descuento del 100 % la base gravable quedaría negativa; así nunca lo es |
| 4 | Combo con sus componentes | La validación **rechaza productos repetidos** (`PROMOTIONS.INVALID_RULE`); la cantidad va en el componente | Un producto repetido haría ambigua la asignación de las líneas |
| 5 | El agente lee su configuración del servidor | **No** la lee ni se autentica: la interfaz de caja le entrega `{ticket, printer}` / `{printer}` / `{printer, terminalName?}` (`printer` = `ReceiptPrinterDto`) | El agente no guarda credenciales ni depende del emparejamiento (ADR-0036) |
| 6 | Transporte de archivo a cualquier carpeta | Solo `Agent:FileOutputDirectory` y `Agent:AllowedFileDirectories` | El servicio corre con permisos amplios y no autentica a quien lo llama |
| 7 | "Escucha solo en localhost" | Loopback + `Host` local (anti DNS rebinding) + CORS solo para `Agent:AllowedOrigins` (por defecto `http://localhost:5480`) con Private Network Access | Una página web cualquiera no debe imprimir ni abrir el cajón |
| 8 | `GET /status` del agente | No comprueba la impresora (conexiones, puertos serie, último trabajo) | Comprobarla exigiría imprimir; para eso está la página de prueba |
| 9 | Prueba con la impresora "Genérico / Solo texto" de Windows | Sin ese modo: siempre ESC/POS en RAW; sin hardware se prueba con el transporte de archivo | Un solo formato; los bytes se comparan byte a byte |
| 10 | Tiquete sin límites | Límites del tiquete (2.000 elementos, textos de 2.000 caracteres, código de barras ≤ 80, QR ≤ 2.000 bytes) y cuerpo ≤ 256 KB | Evitar trabajos absurdos o datos que la impresora no representa |
| 11 | Puerto serie `COMx` | `COMx[:baudios]` (por defecto 9.600) | Las impresoras serie usan velocidades distintas |
| 12 | Servicio de Windows instalado | Aún **no** hay script de instalación del servicio | Llega con el instalador; hoy se registra a mano |
| 13 | Reintegro de dinero | Solo la excepción de garantía del propietario (`sales.refund.warranty`) | Resolución §15.2 |
| 14 | Periféricos por defecto al arrancar | Sin fila en `org.terminal_devices` la caja usa los valores por defecto (archivo, 80 mm, PC850) | No hace falta sembrar filas por caja |

## 5. Limitaciones conocidas y pendiente

1. **Documento equivalente electrónico** (Factus): Fase 11-B. Riesgo normativo R-01: confírmalo con tu contador antes de operar.
2. **Interfaz de caja** definitiva: Fase 15 (hoy, consola de prueba y API).
3. **Impresora real**: validar con la página de prueba cuando llegue el hardware; **script de instalación** del agente como servicio.
4. **Caja autónoma** sin servidor: fase posterior (el motor puro ya es reutilizable).
5. {{PRUEBAS}}

## 6. Cómo probarlo

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1                  # compila, pruebas y cobertura → BUILD OK
powershell -ExecutionPolicy Bypass -File .\tools\scripts\dev-db.ps1   # aplica las migraciones 016–019
dotnet run --project src\Server\Pos.Server.Host                      # http://localhost:5480
dotnet run --project src\Terminal\Pos.Terminal.Agent                 # agente de caja en http://localhost:5490
```

- **Consola de prueba** (solo en desarrollo): `http://localhost:5480/prueba/`.
- **API**: ejecuta `http/fase-07.http` en orden (jornada, venta, escaneo, descuento con `X-Authorization-Grant`, cobro con
  `Idempotency-Key`, suspender/recuperar, anular, cambio de mercancía, promociones, comprobantes, impresora por caja, ajuste rápido y
  agente). En Multicaja, empareja antes el equipo como caja (`http/fase-03.http`).
- **Agente sin impresora**: con la impresora de la caja en `File`, `POST /print` y `/test-page` dejan los bytes ESC/POS en
  `Agent:FileOutputDirectory` (en desarrollo, `.data/agent-output`).
