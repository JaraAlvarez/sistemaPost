// Preferencia de apariencia del portal (claro u oscuro; sin valor = la del sistema) en el almacenamiento de ESTE navegador.
// Módulo ES del mismo origen: lo permite la CSP (script-src 'self'); el portal no usa scripts en línea.
const clave = "bp.portal.tema";

export function leer() {
    try {
        return localStorage.getItem(clave);
    } catch {
        return null;
    }
}

export function guardar(valor) {
    try {
        localStorage.setItem(clave, valor);
    } catch {
        // Sin almacenamiento (navegación privada o bloqueado): la elección dura hasta cerrar la pestaña.
    }
}
