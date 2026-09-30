# Fase 16 · Sincronización con la nube y portal del cliente — Informe

- **Estado:** Implementada — pendiente de tu validación · 2026-09-30
- **Propuesta:** [fase-16-propuesta.md](fase-16-propuesta.md) (aprobada por anticipado: pediste ejecutar 15, 16 y 17 sin confirmación)
- **Decisiones:** [ADR-0057](../adr/0057-subida-a-la-nube-por-cursores-con-acuse.md) · [ADR-0058](../adr/0058-paquete-possync-y-portal-del-cliente.md)

## 1. Qué se entregó

| Parte | Entregado |
|---|---|
| Tienda · módulo `Sync` | Lee por cursor las ventas (con líneas y pagos, también las anuladas), los cierres de caja, los saldos de inventario que cambiaron y los productos con su precio. Cada ⚙️ 60 s sube lo pendiente en lotes de 500; el cursor avanza **solo con el acuse** de la nube. Esquema `sync` (`cursors`, `batches`) en la migración `V2026.10.031` |
| Tienda · paquete `.possync` | `GET /api/v1/sync/export`: el mismo lote, **cifrado para la nube** (X25519 + ChaCha20-Poly1305). Lleva lo que no se confirmó en línea ni se exportó antes |
| Tienda · pantalla | **Sincronización** (`/admin/sincronizacion`): pendientes por tipo, último acuse, envíos recientes, "Sincronizar ahora" y "Exportar paquete .possync". Permisos `sync.sync.view` y `sync.sync.manage` |
| Contrato | `Pos.Sync.Contracts`: ruta `/v1/sync/batches`, lote, acuse y el paquete cifrado (compartido por la tienda y la nube) |
| Nube · recepción | `POST /v1/sync/batches` autenticado con `License <token>` + huella del equipo (sin credenciales nuevas). Guarda cada documento con su versión más reciente; **repetir un lote no lo reaplica**. Migración `V2026.10.007` (esquema `sync`) |
| Nube · portal | Página **Datos de las tiendas** (`/tiendas`): empresa, fechas, estado por sucursal (último lote, vía, conteos), ventas por día, ventas recientes, cierres de caja, existencias con búsqueda y **cargar paquete .possync** |
| Nube · portal del cliente | Rol **Cliente** (migración `V2026.10.006`): atado a una cuenta; solo ve y carga datos de las empresas de esa cuenta. Entra con contraseña + autenticador; va directo a `/tiendas`. Se crea en Usuarios |
| Despliegue | Comando `generate-sync-key`; secreto `sync_key` en `docker-compose.yml` (`SYNC_KEY_FILE`); [despliegue-nube.md §17](../despliegue-nube.md) |

## 2. Validación hecha

- Compilación sin errores ni advertencias de toda la solución.
- **Tienda** (`Phase16/SyncApiTests`, con licencia y nube simuladas):
  - el paquete solo se abre con la clave de la nube; alterado, con otra clave o sin formato → rechazado; el contenido va cifrado;
  - sin licencia activada no se sincroniza (y lo dice); la cajera no ve la pantalla;
  - sin Internet no avanza nada y queda el envío fallido;
  - con Internet sube la venta (líneas, total), saldos y productos, con el token y la huella; el segundo envío no manda nada;
  - el paquete lleva solo la venta no confirmada, un segundo paquete no la repite y el envío en línea la sube igual.
- **Nube** (`SyncTests`): sin token o con la huella de otro equipo → 401; el mismo lote dos veces → `duplicate`; una versión más vieja
  se ignora y la anulación (más nueva) reemplaza la venta; ventas por día y estado por sucursal; el usuario Cliente exige una cuenta.
- Arquitectura (23), arquitectura de la nube, base de datos (74), base de datos de la nube (26), conformidad de permisos y licencia: en verde.
- **Corregido en las pruebas:** PostgreSQL entrega las columnas `date` como `DateOnly` (las consultas las convierten).
- **No probado aquí:** las páginas del portal en el navegador y una tienda real contra la nube desplegada (ver §3).

## 3. Cómo probarlo tú

1. Nube local (despliegue §13): genere también `secrets/sync-key.txt` con `generate-sync-key` y anote la clave pública.
2. En la tienda de desarrollo configure `Pos:Licensing:ServerUrl` con la nube local y `Pos:Sync:DevelopmentCloudPublicKey` con esa
   clave pública; active la licencia (Fase 12-B).
3. Haga ventas; al minuto (o con "Sincronizar ahora") aparecen en el portal → **Datos de las tiendas**.
4. Apague la red de la nube, venda, **Exportar paquete** y cárguelo en el portal: las ventas aparecen con vía "Paquete".
5. Cree un usuario **Cliente** para la cuenta de esa empresa y entre con él: solo ve sus empresas.

## 4. Pendiente

- **Fase 18:** editar productos y precios en el portal y que bajen a las tiendas (cambios de otro nodo, conflictos y su bandeja).
- Antes de publicar para clientes: embeber la clave pública de sincronización de producción en `sync-key.json`.
