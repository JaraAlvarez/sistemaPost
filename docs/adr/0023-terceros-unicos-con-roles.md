# ADR-0023 · Terceros únicos con roles (proveedor, cliente)

- **Estado:** Aceptada · 2026-09-28 · Fase 5 · Decisiones D5-01 y D5-02

## Contexto
Proveedores (Fase 5) y clientes (Fase 8) comparten identificación y datos fiscales (tipo y número, DV del NIT, régimen,
responsabilidades, correo de factura electrónica). Guardarlos en tablas separadas duplica el NIT y sus errores, y un mismo
tercero puede ser proveedor y cliente. Los maestros se crean en cualquier tienda y en el portal, incluso sin conexión.

## Decisión
- `parties.parties` guarda la identidad una sola vez por empresa: persona natural (nombres y apellidos) o jurídica (razón
  social), identificación única (tipo + número) entre los terceros vivos y **DV validado** para NIT (módulo 11 de la DIAN, en
  `Pos.SharedKernel.Fiscal.Nit`, compartido con la empresa).
- Proveedor y cliente son **roles** que referencian al tercero (`purchasing.suppliers.party_id`, un proveedor por tercero).
- El mismo tercero creado sin conexión en dos tiendas se **fusiona** en la nube: se conserva el más antiguo y el otro queda
  `MERGED` con `merged_into_id` (el índice único excluye los fusionados). Los documentos guardan su propio snapshot.
- "Consumidor final" (CC 222222222222) se siembra en cada empresa como tercero del sistema, que no se modifica.

## Consecuencias
- ✅ Un NIT, una ficha; los reportes por tercero cruzan compras y ventas.
- ✅ La factura electrónica (Fase 7/11) toma los datos fiscales de un único lugar.
- ⚠️ La fusión automática se implementa con la sincronización; hoy solo existe el modelo (`MergeInto`).
