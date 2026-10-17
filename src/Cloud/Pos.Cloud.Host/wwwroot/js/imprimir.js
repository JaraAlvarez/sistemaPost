// Abre el cuadro de impresión del navegador (manual de instalación: "Imprimir / Guardar como PDF"). Módulo ES del mismo origen: lo
// permite la CSP (script-src 'self').
export function imprimir() {
    window.print();
}
