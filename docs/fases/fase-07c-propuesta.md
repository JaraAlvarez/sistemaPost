# Fase 7-C · Perfiles de negocio y básculas (catálogo de compatibles) — Propuesta

> Estado: **APROBADA en lo conceptual** · 2026-10-17 (seis tipos de negocio aprobados; báscula de etiqueta + báscula conectada;
> catálogo de básculas con imagen y referencia, cambio de báscula por daño o mejora). Empieza cuando termine la Fase 13-B.
> Base: [doc 01](../01-analisis-producto.md) (peso variable, báscula conectada), [doc 02](../02-modulos.md) §Devices,
> [doc 03](../03-arquitectura.md) (periféricos como interfaz + driver), [ADR-0036](../adr/0036-agente-de-caja.md) (agente de caja),
> RN-CAT-03 y RN-SAL-16 ([doc 05](../05-reglas-y-estados.md)), [plan de variantes](plan-variantes-ropa-calzado.md).

## 1. Perfiles de negocio y funciones activables

En el instalador (paso "Tu negocio") y en **Configuración → Funciones del negocio**:

| Perfil | Se enciende | Se oculta |
|---|---|---|
| Supermercado / minimercado | Báscula, PLU, lotes y vencimientos, promociones, compras | Tallas y colores |
| Fruver / carnicería | Báscula (conectada y de etiqueta), kilo o libra, tara | Tallas; lotes opcional |
| Droguería | Lotes y vencimientos obligatorios, alertas | Báscula, tallas |
| Ropa / calzado / deportes | Tallas y colores (cuando exista la fase de variantes), cambios por talla, etiquetas | Báscula, PLU, vencimientos |
| Ferretería / papelería / miscelánea | Venta por metro o fracción, precio abierto | Báscula, tallas, vencimientos |
| Otro | Lo básico | — |

- **No es un plan de precios** (ADR-0015 sigue: todas las funciones en Caja Única y Multicaja). Solo ordena pantallas, menús y
  valores iniciales (unidades, categorías de ejemplo, palabras como "prenda").
- Cada función es un interruptor que el dueño puede cambiar; el perfil es un punto de partida. Apagar una función **oculta**, nunca
  borra datos.
- La nube conoce el perfil de cada cliente (portal: filtro y estadística).

## 2. Básculas: dos formas

| Forma | Cómo funciona | Estado |
|---|---|---|
| **Báscula etiquetadora** (sección de carnes o fruver) | Pesa, imprime etiqueta EAN-13 con PLU + peso o precio; la caja la escanea | **Ya funciona** (D4-12, RN-SAL-16) |
| **Báscula conectada a la caja** | Cable serial/USB al computador de la caja; el agente lee el peso en vivo | **Nueva** |
| Escáner-báscula de caja (opcional) | Escáner de mostrador con báscula integrada (tipo supermercado grande) | Nueva, si hay demanda |

## 3. Catálogo de básculas compatibles (lo que pediste)

En **Configuración → Cajas e impresoras → Báscula** (y en el instalador si el perfil usa báscula):

1. **Galería** con tarjetas: **imagen**, **marca y referencia**, tipo (de mostrador conectada · etiquetadora · escáner-báscula),
   conexión (serial RS-232 / USB-serial / red) y la etiqueta **"Compatible con BusinessPost"**.
2. Al elegir una: **guía de instalación ilustrada de ese modelo** (qué cable usar, en qué puerto conectarla, qué configurar en la
   báscula: protocolo y velocidad) y el sistema aplica la configuración correcta (protocolo, velocidad, bits, comando de lectura).
3. **"Probar báscula"**: el peso en vivo en pantalla, indicador de "estable", prueba de tara; no deja guardar si no lee.
4. **Cambiar de báscula** (daño o mejora): asistente "Reemplazar báscula" que conserva el historial (qué báscula tuvo cada caja y
   desde cuándo), desactiva la anterior y repite la prueba con la nueva. Queda en la auditoría.
5. **Modelo no listado**: opción "Otra báscula (configuración manual)" para técnicos, con protocolos genéricos.
6. Las etiquetadoras también salen en el catálogo: al elegir una, se crea sola la **regla de código de barras** de ese modelo
   (prefijo, PLU, peso/precio) — lo que hoy se configura a mano.

**Catálogo administrado desde tu portal** (nube): tú agregas o retiras modelos (imagen, referencia, protocolo, guía) y las tiendas
lo reciben con la licencia/sincronización; así no hace falta una versión nueva del programa para sumar una báscula compatible.

**Imágenes:** las fotos de producto de cada fabricante tienen derechos de autor. Opciones: (a) fotos propias o de tus
distribuidores (recomendado: tomarlas a las básculas que vendas o instales), (b) ilustraciones propias con el estilo de BusinessPost,
(c) fotos oficiales con permiso del fabricante o distribuidor. Por defecto se entrega con ilustraciones propias por tipo, y desde el
portal se reemplaza cada imagen por la foto real.

## 4. En la caja

- Producto por peso: al escanearlo o buscarlo, si la caja tiene báscula conectada aparece **"Pesar"** (o se pesa solo al escanear,
  configurable): peso en vivo → al estabilizarse se agrega **0,700 kg × $3.000/kg = $2.100**.
- **Tara** (bolsa, bandeja) por producto o tecla rápida. Sin báscula, el cajero digita el peso (como hoy).
- No agrega con peso inestable, en cero o negativo; muestra el peso en el tiquete.
- **Kilo o libra**: precio por kg o por **libra de 500 g** (uso colombiano), configurable por producto; ⚙️ factor de la libra.

## 5. Primeros modelos (a confirmar con fichas técnicas y pruebas reales)

| Tipo | Candidatos comunes en Colombia | Protocolo |
|---|---|---|
| Mostrador conectada | CAS PD-II, CAS ER Plus, Torrey PCR/LPC, Moretti | CAS / Toledo 8217 (muchas lo emulan) |
| Etiquetadora | CAS CL5200, Dibal serie L/M, Epelsa | Reglas EAN-13 prefijo 20–28 |
| Escáner-báscula | Datalogic Magellan 8x00 | SASI / NCR |

El primer driver será **Toledo 8217** (lo emulan muchas marcas) + **CAS**; los demás se agregan como drivers sin tocar el POS
(doc 03: interfaz + driver por marca).

## 6. Lo que necesito de ti

1. **Qué básculas tienes o venderás** (aunque sea una) para probar con el equipo real: sin una báscula física no se puede validar la
   lectura (se prueba con un simulador por puerto serial virtual).
2. **Fotos** de esas básculas (o autorización de tu distribuidor para usar las suyas).

## 7. Partes

| Parte | Contenido |
|---|---|
| A | Perfiles de negocio y Funciones del negocio (configuración, menús, instalador) |
| B | Driver de báscula en el agente de caja (Toledo 8217 + CAS) + "Probar báscula" + simulador para pruebas |
| C | "Pesar" en la caja, tara, kilo/libra |
| D | Catálogo de básculas en el portal (imagen, referencia, guía) y galería en la tienda; asistente "Reemplazar báscula" |
