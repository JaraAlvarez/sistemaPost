# ADR-0007 · Código en inglés; documentación y UI en español

- **Estado:** Aceptada · 2026-09-28 (aprobada por el propietario del producto)

## Decisión
- **Inglés:** nombres de proyectos, clases, métodos, tablas, columnas, rutas de la API y códigos de error (`SALES.INSUFFICIENT_STOCK`).
- **Español:** documentación, comentarios, mensajes de error para el usuario, logs de negocio e interfaz gráfica.
- Los **nombres de las pruebas** se escriben en español, porque describen reglas de negocio (`Precio_con_IVA_incluido_se_descompone_sin_perder_centavos`).

## Consecuencias
- ✅ Compatible con librerías, herramientas y desarrolladores de cualquier país. El usuario final ve todo en español.
- ⚠️ Los mensajes están en el código por ahora (analizador CA1303 desactivado). Si el producto se lleva a otro idioma, se extraerán a archivos de recursos.
