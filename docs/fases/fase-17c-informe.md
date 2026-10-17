# Fase 17-C · Mejoras después de la interfaz sin ventanas — Informe

- **Estado:** Implementada — pendiente de tu validación · 2026-10-17
- **Pedido:** mejoras 1 a 8 de la Fase 17-B y la consulta de precio en la caja (aprobado en el chat).

| # | Mejora | Resultado |
|---|---|---|
| 1 | La sesión se perdía al recargar | Sesión en la pestaña (`sessionStorage`, D17C-01); al volver a entrar regresa a la página (`?volver=`). Se carga también la credencial del equipo emparejado antes de la primera petición |
| 2 | Arranque lento | Windows Defender revisa cada .exe/.dll nuevo sin firma: 4 min 23 s con archivos recién compilados. Modo `--precalentar` en servidor, migrador y agente; el actualizador lo corre al descomprimir (arranque real después: 4 s). `build-installer.ps1 -CodeSigningThumbprint` firma los ejecutables. Guía de instalación §8 |
| 3 | Filtros que se perdían | 14 listas con los filtros en la dirección (`Services/Filtros.cs`) |
| 4 | Probar guardar en los formularios | Recorrido en el navegador de producto, proveedor, cliente, compra, pago, orden, traslado, ajuste y promoción. Arreglos: crear cliente desde la administración (servidor, con prueba), fechas con zona a UTC al guardar (servidor), formato numérico de Colombia en todos los campos, nombres de categorías |
| 5 | Ficha de producto | Presentaciones; precio por lista, sucursal y presentación; códigos por presentación; PLU obligatorio en báscula |
| 6 | Nombres del cliente | El servidor envía nombres, apellidos, razón social y nombre comercial por separado |
| 7 | Reportes X/Z por la impresora | El Z se imprime al cerrar la jornada ("Reimprimir Z"); "Imprimir" en la jornada; columnas alineadas. Verificado con el agente de impresión (salida a archivo) |
| 8 | Código repetido | Servicio `Bodegas`, componente `LineasProductos`, clase `.fila-clic`, guía de diseño actualizada |
| — | Consulta de precio (F5) en la caja | Código, nombre o código interno; precio de la sucursal, IVA y existencias; no toca la venta |

## Validación
Compilación sin errores; pruebas: arquitectura (23), infraestructura (79), base de datos (78), integración enfocada (15) y la de crear cliente.
Recorridos en el navegador descritos arriba.

## Pendiente
- Certificado de firma de código para instalar en clientes (guía de instalación §8).
- Decidir si las facturas del pago a proveedor salen desmarcadas.
