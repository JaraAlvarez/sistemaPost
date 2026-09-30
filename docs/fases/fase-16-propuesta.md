# Fase 16 · Sincronización con la nube y portal del cliente — Propuesta

> Estado: **APROBADA POR ANTICIPADO** · 2026-09-30 (pediste ejecutar 15, 16 y 17 sin confirmación; se aplican estas recomendaciones).
> Base: tu visión del producto (revisión de la Fase 2 §9–§10), ADR-0014 (nodos y convenciones de sincronización), Fase 12-A (nube y portal),
> Fase 12-B (token de licencia del POS).

## 0. Objetivo y alcance

Después de esta fase **cada venta, cada cierre de caja y las existencias de cada tienda llegan a la nube** en cuanto hay Internet. Si un
día no hubo Internet, la tienda **exporta un paquete `.possync`** cifrado y alguien lo **carga en el portal** desde otro equipo. El
**propietario del supermercado** entra a su portal y consulta las ventas, los cierres y el inventario de todas sus sucursales.

| Incluido | Excluido (fase) |
|---|---|
| **Subida tienda → nube** de ventas (con líneas y pagos), anulaciones, cierres de caja, existencias por bodega y productos con su precio | **Edición de maestros en el portal que baja a las tiendas** (productos y precios en los dos sentidos, bandeja de conflictos): Fase 18, ver §3 |
| Transporte en línea (cada ⚙️ 60 s) y **paquete `.possync`** cifrado para la nube: el mismo lote, dos transportes | Caja autónoma |
| La nube guarda lo recibido de forma **idempotente** (subir dos veces, o por Internet y por archivo, no duplica) | Reportes avanzados en el portal (v1: los esenciales) |
| **Portal del cliente**: rol nuevo **Cliente** atado a su cuenta; ve solo sus empresas: ventas por día y sucursal, ventas recientes, cierres, inventario, estado de sincronización, cargar paquetes | Vender desde el portal (decisión del propietario: no) |
| El equipo del propietario (Superadministrador, Soporte) ve lo mismo en la ficha de cada empresa | |
| Pantalla **Sincronización** en el backoffice de la tienda: estado, pendientes, "sincronizar ahora" y "exportar paquete" | |

## 1. Decisiones

| # | Decisión | Recomendación y motivo |
|---|---|---|
| D16-01 🔒 | Cómo se captura lo que sube | **Fuentes con cursor** por tipo de dato (ventas, cierres, existencias, productos), leídas de las tablas con una marca de tiempo y un margen de seguridad de ⚙️ 60 s, no eventos de dominio nuevos. Motivo: los documentos ya son inmutables (ADR-0014) y así no se toca ningún módulo; los cursores viven en el esquema `sync` |
| D16-02 🔒 | Cuándo avanza el cursor | Con el **acuse** de la nube (en línea). El paquete de archivo avanza un cursor propio de exportación; lo repetido se ignora en la nube. Nada se pierde si un envío falla |
| D16-03 🔒 | Autenticación de la tienda | El **token de licencia** firmado (Fase 12-B) más la huella del equipo: la nube sabe de qué instalación y empresa es el lote sin credenciales nuevas |
| D16-04 🔒 | Cifrado del paquete | **X25519 + ChaCha20-Poly1305** (NSec) hacia la **clave pública de sincronización** de la nube, embebida en el POS. Solo la nube lo abre; si se pierde la USB, nadie lee las ventas |
| D16-05 | Almacenamiento en la nube | Esquema `sync`: documentos JSON por instalación, tipo e id con su versión (el más reciente gana); consultas del portal sobre ellos. Suficiente para el volumen de v1; si crece, se proyectan tablas |
| D16-06 | Portal del cliente | Rol **CUSTOMER** en el mismo portal (misma seguridad: contraseña + TOTP), atado a una **cuenta**: solo ve las empresas de esa cuenta. Lo crea el superadministrador |
| D16-07 | Existencias en la nube | Instantánea de los saldos que cambiaron (no los movimientos): es para consultar, la verdad sigue en la tienda (ADR-0014: los saldos no se sincronizan como dato autoritativo) |

## 2. Validación
Compilación; prueba de punta a punta del lote (tienda → formato → nube aplica idempotente), prueba del paquete cifrado (se abre solo con la
clave de la nube; alterado = rechazado), prueba del portal del cliente (solo ve su cuenta). Tú: con tu nube local, sincronizar una tienda y
ver sus ventas en el portal.

## 3. Por qué la edición de maestros en el portal va en la Fase 18
Bajar cambios del portal a las tiendas exige: `row_version` por campo, aplicación en la tienda de cambios de otro nodo, resolución de
conflictos y la bandeja de conflictos. Es la parte más delicada de la visión y merece su propia fase, con la subida (esta fase) ya probada.
Hasta entonces, los productos y precios se editan en cada tienda (Fase 15) y el portal los muestra.
