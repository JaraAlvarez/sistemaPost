# ADR-0001 · Monolito modular, local-first, API-first

- **Estado:** Aceptada · 2026-09-28

## Contexto
El POS se instala en el PC de un supermercado, debe vender sin Internet y crecer a varias cajas, sucursales y nube. El equipo de desarrollo es pequeño.

## Decisión
- Un **único proceso servidor por tienda** (monolito), dividido en **módulos** con fronteras estrictas: cada módulo tiene capas `Domain`, `Application`, `Infrastructure`, `Api`, `Contracts` y su propio esquema de BD. Entre módulos solo se usan los `Contracts`.
- **Local-first:** la base de datos de la tienda es la fuente de verdad de la operación. La nube solo complementa (licencias, actualizaciones, facturación electrónica, respaldo).
- **API-first:** toda funcionalidad se expone por HTTP versionado (`/api/v1`). La UI, el agente de caja, las pruebas y las integraciones usan la misma API.
- El host carga los módulos desde una **lista explícita** (`ModuleCatalog`), no por reflexión.

## Consecuencias
- ✅ Despliegue simple (un servicio + una BD), transacciones ACID entre módulos, sin la complejidad de los microservicios.
- ✅ Se puede construir y probar todo el sistema sin interfaz gráfica.
- ✅ Un módulo podría extraerse a un servicio en el futuro, gracias a sus fronteras.
- ⚠️ Las fronteras se deben vigilar: las pruebas de arquitectura (`Pos.ArchitectureTests`) las hacen cumplir automáticamente.
