// Instalador premium de BusinessPost (Fase 13-B, D13B-08 y §8). Habla con Windows por el puente de WebView2 (Puente.cs):
//   llamar(accion, datos) → promesa con el resultado; los avisos de la instalación llegan como { evento, … }.
"use strict";

const $ = (id) => document.getElementById(id);
const todas = (sel) => [...document.querySelectorAll(sel)];

// ───────────────────────────── Puente ─────────────────────────────
const pendientes = new Map();
let secuencia = 0;
const webview = window.chrome && window.chrome.webview;

function llamar(accion, datos) {
  return new Promise((resolver, rechazar) => {
    if (!webview) { rechazar(new Error("Abre el instalador desde BusinessPost-Setup.exe.")); return; }
    const id = String(++secuencia);
    pendientes.set(id, { resolver, rechazar });
    webview.postMessage({ id, accion, datos: datos ?? null });
  });
}

if (webview) {
  webview.addEventListener("message", (e) => {
    const m = e.data;
    if (m && m.evento) { alAviso(m); return; }
    const p = m && pendientes.get(m.id);
    if (!p) return;
    pendientes.delete(m.id);
    if (m.ok) p.resolver(m.dato);
    else { const error = new Error(m.mensaje || "Ocurrió un error."); error.codigo = m.codigo; p.rechazar(error); }
  });
}

// ───────────────────────────── Estado ─────────────────────────────
// Instalador 1.2.1: no pide ningún dato. Bienvenida (Instalar) → Instalando → ¡Listo! La tienda queda como única caja, SUPERMERCADO,
// con datos provisionales de la empresa, sin Propietario y en prueba: el primer ingreso a BusinessPost (con la cuenta del portal o la
// clave) crea al Propietario. La caja adicional y recuperar un backup son enlaces discretos de la Bienvenida.
const S = {
  adicional: false, recuperar: false, backup: null, instalando: false, terminado: false, desinstalar: false, maqueta: false,
};

// Modo del equipo (misma regla que Logica/ModoPorLicencia.cs): la única caja, salvo la caja adicional o al recuperar el principal.
function modoActual() {
  if (S.adicional) return "adicional";
  if (S.recuperar) return $("chkRecuperarPrincipal").checked ? "principal" : "unica";
  return "unica";
}

const nombres = {
  bienvenida: "Bienvenida", recuperar: "Recuperar backup", principal: "Equipo principal", instalando: "Instalación", listo: "¡Listo!",
  "d-inicio": "Antes de desinstalar", "d-progreso": "Desinstalación", "d-listo": "¡Listo!",
};

function pasosVisibles() {
  if (S.desinstalar) return ["d-inicio", "d-progreso", "d-listo"];
  if (S.recuperar) return ["bienvenida", "recuperar", "instalando", "listo"];
  if (S.adicional) return ["bienvenida", "principal", "instalando", "listo"];
  return ["bienvenida", "instalando", "listo"];
}

// Pantallas que se muestran dentro de otro paso del panel izquierdo.
const pasoDe = { error: "instalando" };

function ir(pantalla) {
  todas(".pantalla").forEach((p) => p.classList.toggle("activa", p.dataset.s === pantalla));
  const pasos = pasosVisibles();
  const actual = pasos.indexOf(pasoDe[pantalla] || pantalla);
  $("pasos").innerHTML = pasos.map((p, i) =>
    `<li class="${i < actual ? "hecho" : i === actual ? "actual" : ""}"><span class="n">${i < actual ? "✓" : i + 1}</span>${nombres[p]}</li>`).join("");
  const enfoque = document.querySelector(`.pantalla[data-s="${pantalla}"] input:not([readonly])`);
  if (enfoque) setTimeout(() => enfoque.focus(), 50);
}

function aviso(id, texto, clase) {
  const el = $(id);
  el.textContent = texto || "";
  el.classList.toggle("oculto", !texto);
  if (clase) { el.classList.remove("error", "info", "ok"); el.classList.add(clase); }
}

function ocupado(boton, si, texto) {
  if (si) { boton.dataset.texto = boton.textContent; boton.textContent = texto || "Un momento…"; boton.disabled = true; }
  else { boton.textContent = boton.dataset.texto || boton.textContent; boton.disabled = false; }
}

// Modal de confirmación (también pide un texto si se indica).
function confirmar({ titulo, texto, si = "Aceptar", no = "Cancelar", peligro = false, pedir = null, soloAceptar = false }) {
  return new Promise((resolver) => {
    $("modalTitulo").textContent = titulo;
    $("modalTexto").textContent = texto;
    $("modalSi").textContent = si;
    $("modalNo").textContent = no;
    $("modalNo").classList.toggle("oculto", soloAceptar);
    $("modalSi").classList.toggle("btn-peligro", peligro);
    $("modalSi").classList.toggle("btn-primario", !peligro);
    $("modalCampo").classList.toggle("oculto", !pedir);
    $("modalEntrada").value = "";
    $("modalEntrada").placeholder = pedir || "";
    $("velo").classList.remove("oculto");
    if (pedir) setTimeout(() => $("modalEntrada").focus(), 50);
    const cerrar = (valor) => { $("velo").classList.add("oculto"); $("modalSi").onclick = $("modalNo").onclick = null; resolver(valor); };
    $("modalSi").onclick = () => cerrar(pedir ? $("modalEntrada").value : true);
    $("modalNo").onclick = () => cerrar(false);
  });
}

// ───────────────────────────── Ventana ─────────────────────────────
$("btnMinimizar").onclick = () => llamar("minimizar");
$("btnCerrar").onclick = pedirCierre;
for (const zona of [$("barra"), document.querySelector(".marca")]) {
  zona.addEventListener("mousedown", (e) => { if (e.button === 0 && !e.target.closest("button")) llamar("arrastrar"); });
}

async function pedirCierre() {
  if (S.instalando) {
    await confirmar({ titulo: S.desinstalar ? "Desinstalando…" : "Instalación en curso",
      texto: "Espera a que termine: cerrar ahora puede dejar BusinessPost a medias.", si: "Entendido", soloAceptar: true });
    return;
  }

  if (S.terminado) { llamar("cerrar"); return; }
  const salir = await confirmar({
    titulo: S.desinstalar ? "¿Cancelar la desinstalación?" : "¿Salir del instalador?",
    texto: S.desinstalar ? "BusinessPost seguirá instalado como está." : "Todavía no se ha instalado nada. Puedes volver a abrir el instalador cuando quieras.",
    si: "Salir", no: S.desinstalar ? "Seguir" : "Seguir instalando",
  });
  if (salir) llamar("cerrar");
}

// ───────────────────────────── Navegación simple ─────────────────────────────
todas("[data-ir]").forEach((b) => b.addEventListener("click", () => {
  if (b.dataset.ir === "bienvenida") { S.recuperar = false; S.adicional = false; }
  ir(b.dataset.ir);
}));

// ───────────────────────────── Bienvenida: instalación normal, caja adicional o recuperar ─────────────────────────────
$("btnComenzar").onclick = () => { S.recuperar = false; S.adicional = false; instalarTienda(); };

// Caja adicional: no lleva licencia propia; usa la Multicaja del equipo principal y se empareja con él.
function irCajaAdicional() {
  S.recuperar = false;
  S.adicional = true;
  ir("principal");
  buscarPrincipal();
}
$("lnkAdicional").onclick = irCajaAdicional;

$("lnkRecuperar").onclick = () => {
  S.adicional = false;
  S.recuperar = true;
  ir("recuperar");
};

const nombresModo = { unica: "la única caja (Caja Única)", principal: "el equipo principal (Multicaja)", adicional: "una caja adicional (Multicaja)" };

// Un equipo ya instalado se actualiza en el MISMO modo; cambiarlo exige confirmarlo (el paquete quita lo que el modo anterior ya no usa).
// Ahora se pregunta al final (el modo sale de la licencia que se escribió).
async function confirmarCambioDeModo() {
  S.cambiarModo = false;
  const modo = modoActual();
  if (!S.modoInstalado || modo === S.modoInstalado) return true;
  const si = await confirmar({
    titulo: "¿Cambiar el modo de este equipo?",
    texto: `Este equipo ya está instalado como ${nombresModo[S.modoInstalado]}. Si sigue, quedará como ${nombresModo[modo]}: se quitan los servicios y reglas del modo anterior que ya no se usen. Los datos de la tienda se conservan.`,
    si: "Sí, cambiar el modo", no: "Mantener el modo actual", peligro: true,
  });
  if (si) S.cambiarModo = true;
  return si;
}

// ───────────────────────────── Recuperar ─────────────────────────────
$("btnElegirBackup").onclick = async () => {
  try {
    const ruta = await llamar("elegirBackup");
    if (ruta) { S.backup = ruta; $("archivoBackup").textContent = ruta; aviso("errorRecuperar", ""); }
  } catch (e) { aviso("errorRecuperar", e.message); }
};

$("btnRecuperar").onclick = async () => {
  const codigo = $("codigoRecuperacion").value.trim().toUpperCase();
  if (!S.backup) { aviso("errorRecuperar", "Elige el archivo del backup (.posbak)."); return; }
  if (codigo.replace(/[^A-Z0-9]/g, "").length < 20) { aviso("errorRecuperar", "Escribe el código de recuperación completo, tal como lo guardaste."); return; }
  if (!(await confirmarCambioDeModo())) return;
  instalar({ modo: modoActual(), backup: S.backup, codigoRecuperacion: codigo });
};

// ───────────────────────────── Caja adicional ─────────────────────────────
async function buscarPrincipal() {
  $("buscandoPrincipal").textContent = "Buscando en la red del local…";
  $("servidores").innerHTML = "";
  try {
    const lista = await llamar("buscarPrincipal");
    $("buscandoPrincipal").textContent = lista.length ? "Elige el equipo principal de tu local:" : "No encontramos el equipo principal en la red. Escribe sus datos abajo.";
    $("servidores").innerHTML = lista.map((s, i) =>
      `<div class="servidor" data-i="${i}"><b>${escapar(s.nombre)}</b><small>${escapar(s.url)}</small></div>`).join("");
    todas(".servidor").forEach((el) => el.addEventListener("click", () => {
      todas(".servidor").forEach((x) => x.classList.remove("sel"));
      el.classList.add("sel");
      const s = lista[+el.dataset.i];
      $("servidorUrl").value = s.url;
      $("servidorHuella").value = s.huella;
    }));
    if (lista.length === 1) document.querySelector(".servidor").click();
  } catch (e) { $("buscandoPrincipal").textContent = e.message; }
}

$("btnBuscarOtra").onclick = buscarPrincipal;
$("btnPrincipal").onclick = async () => {
  let url = $("servidorUrl").value.trim();
  const huella = $("servidorHuella").value.trim().replace(/[^0-9a-f]/gi, "").toUpperCase();
  if (!/^https:\/\/[^\s/]+(:\d+)?\/?$/i.test(url)) { aviso("errorPrincipal", "La dirección se ve así: https://192.168.1.10:5443/"); return; }
  if (huella.length !== 64) { aviso("errorPrincipal", "La huella tiene 64 letras y números. Cópiala de la administración del equipo principal (Cajas e impresoras)."); return; }
  if (!url.endsWith("/")) url += "/";
  aviso("errorPrincipal", "");
  if (!(await confirmarCambioDeModo())) return;
  instalar({ modo: "adicional", servidor: url, huella });
};

// ───────────────────────────── Datos de la empresa (provisionales) ─────────────────────────────
// La tienda se crea como SUPERMERCADO, con la sucursal "Principal" y la "Caja 1" y datos provisionales de la empresa (razón social
// "Mi negocio", NIT provisional y dirección "Por completar": los mismos de Company.ProvisionalNit/ProvisionalAddress). En el primer
// ingreso con la cuenta del portal la tienda toma el NIT y la razón social de su empresa; lo demás se completa en Configuración (el
// Tablero lo recuerda).
const NEGOCIO_PROVISIONAL = {
  razonSocial: "Mi negocio", nombreComercial: "Mi negocio", tipoPersona: "Natural", tipoDocumento: "NIT", numero: "0", dv: "0",
  regimen: "49", municipio: "11001", direccion: "Por completar", telefono: null, correo: null, tipoNegocio: "SUPERMARKET",
};

async function instalarTienda() {
  if (!(await confirmarCambioDeModo())) return;
  instalar({ modo: modoActual(), negocio: { ...NEGOCIO_PROVISIONAL } });
}

// Ya se instaló y falló la configuración: se reintenta SOLO ese paso, sin reinstalar (nunca se manda al asistente web).
function reconfigurar() {
  S.instalando = true;
  ir("instalando");
  progreso({ etapa: 5, porcentaje: 90, nota: "Dejando lista tu tienda…" });
  llamar("reconfigurar").catch((e) => alAviso({ evento: "error", mensaje: e.message, reintentar: true }));
}

// ───────────────────────────── Instalación ─────────────────────────────
let ultimoResumen = null;

function instalar(solicitud) {
  S.instalando = true;
  solicitud.cambiarModo = !!S.cambiarModo;
  ultimoResumen = solicitud;
  $("itemConfigurar").classList.toggle("oculto", solicitud.modo === "adicional" || !!solicitud.backup);
  ir("instalando");
  progreso({ etapa: 0, porcentaje: 0, nota: "Preparando…" });
  llamar("instalar", solicitud).catch((e) => alAviso({ evento: "error", mensaje: e.message }));
}

function progreso(m) {
  const items = todas("#lista .item");
  items.forEach((it, i) => {
    it.classList.toggle("hecho", i < m.etapa);
    it.classList.toggle("va", i === m.etapa);
    it.querySelector(".e").textContent = i < m.etapa ? "✓" : "";
  });
  $("barraProgreso").style.width = m.porcentaje + "%";
  $("pct").textContent = m.porcentaje + " %";
  if (m.nota) $("notaProgreso").textContent = m.nota;
}

function alAviso(m) {
  switch (m.evento) {
    case "pedirCierre": pedirCierre(); break;
    case "progreso":
      if (S.desinstalar) { $("dBarra").style.width = m.porcentaje + "%"; $("dNota").textContent = m.nota || ""; }
      else progreso(m);
      break;
    case "terminado": S.desinstalar ? desinstalado(m) : terminado(m); break;
    case "error":
      S.instalando = false;
      if (S.desinstalar) { ir("d-inicio"); aviso("dError", m.mensaje); break; }
      $("errorTexto").textContent = m.mensaje;
      // Ya se instaló: "Reintentar" repite solo la configuración (nunca se manda al dueño al asistente web a escribir todo otra vez).
      $("btnReintentar").classList.toggle("oculto", !m.reintentar);
      $("btnVerRegistro").classList.toggle("oculto", !m.registro);
      $("btnVerRegistro").onclick = () => llamar("verRegistro", { ruta: m.registro });
      ir("error");
      break;
  }
}

$("btnSalirError").onclick = () => { S.terminado = true; llamar("cerrar"); };
$("btnReintentar").onclick = () => reconfigurar();

function terminado(m) {
  S.instalando = false;
  S.terminado = true;
  progreso({ etapa: 7, porcentaje: 100, nota: "" });
  const modos = { unica: ["Única caja", "Caja Única"], principal: ["Equipo principal", "Multicaja"], adicional: ["Caja adicional", "Multicaja"] };
  const [modo, plan] = modos[m.modo || modoActual()] || modos.unica;
  $("resEmpresa").textContent = m.empresa || (S.adicional ? "Conectada al principal" : "Tu negocio");
  $("resModo").textContent = modo;
  $("resPlan").textContent = plan;
  if (S.recuperar) { $("resLicencia").textContent = "Desde tu backup"; $("resVigencia").textContent = "datos recuperados"; }
  else if (S.adicional) { $("resLicencia").textContent = "La del principal"; $("resVigencia").textContent = "empareja la caja al abrir"; }
  else { $("resLicencia").textContent = "Prueba gratis"; $("resVigencia").textContent = "se activa al entrar con tu cuenta"; }
  $("listoLead").textContent = S.adicional
    ? "La caja quedó instalada. Al abrir BusinessPost, empareja esta caja con el código que genera el equipo principal."
    : S.recuperar ? "BusinessPost quedó instalado con los datos de tu backup."
    : "BusinessPost quedó instalado. Ábrelo y entra con tu cuenta de BusinessPost (o con tu clave de licencia): quedarás como el dueño de la tienda.";
  aviso("avisoListo", m.aviso || "", "info");
  ir("listo");
}

$("btnAbrir").onclick = () => llamar("abrir", { aliado: $("chkAliado").checked, servidor: ultimoResumen && ultimoResumen.servidor });

// ───────────────────────────── Desinstalar (§8) ─────────────────────────────
function prepararDesinstalar(d) {
  $("subtitulo").textContent = "Desinstalación";
  document.title = "Desinstalar BusinessPost";
  if (!d || !d.instalado) {
    $("dLead").textContent = "BusinessPost no está instalado en este equipo.";
    $("dCuerpo").classList.add("oculto");
    $("btnDesinstalar").classList.add("oculto");
    $("btnCancelarD").textContent = "Cerrar";
    S.terminado = true;
  }
  $("dDatos").textContent = d ? d.datos : "";
  $("dBackup").classList.toggle("oculto", !(d && d.equipoPrincipal));
  ir("d-inicio");
}

$("btnBackupFinal").onclick = async () => {
  const boton = $("btnBackupFinal");
  ocupado(boton, true, "Haciendo el backup…");
  S.instalando = true;
  try {
    const ruta = await llamar("backupFinal");
    if (!ruta) { ocupado(boton, false); return; } // Cerró la ventana de elegir carpeta.
    $("dBackupRuta").textContent = "Backup final guardado en: " + ruta;
    boton.textContent = "Listo ✓";
  } catch (e) { $("dBackupRuta").textContent = e.message; ocupado(boton, false); }
  finally { S.instalando = false; }
};

$("chkBorrar").onchange = async () => {
  if (!$("chkBorrar").checked) return;
  $("chkBorrar").checked = false;
  const primero = await confirmar({
    titulo: "¿Borrar también los datos?",
    texto: "Se borrarán para siempre las ventas, el inventario, los clientes y los backups de ESTE equipo. Haz antes el backup final y guárdalo fuera del equipo. Recuerda: para abrir ese backup en otro computador necesitas tu código de recuperación.",
    si: "Sí, quiero borrarlos", peligro: true,
  });
  if (!primero) return;
  const texto = await confirmar({
    titulo: "Última confirmación",
    texto: "Escribe BORRAR para confirmar que entiendes que los datos de este equipo no se podrán recuperar sin un backup y su código de recuperación.",
    si: "Confirmar", peligro: true, pedir: "BORRAR",
  });
  $("chkBorrar").checked = typeof texto === "string" && texto.trim().toUpperCase() === "BORRAR";
};

$("btnCancelarD").onclick = pedirCierre;
$("btnDesinstalar").onclick = async () => {
  const ok = await confirmar({
    titulo: "¿Desinstalar BusinessPost?",
    texto: $("chkBorrar").checked ? "Se quitarán los programas y se BORRARÁN los datos de este equipo." : "Se quitarán los programas. Tus datos se conservan en este equipo.",
    si: "Desinstalar", peligro: $("chkBorrar").checked,
  });
  if (!ok) return;
  aviso("dError", "");
  S.instalando = true;
  ir("d-progreso");
  llamar("desinstalar", {
    borrarDatos: $("chkBorrar").checked, usuario: $("dUsuario").value.trim() || null, contrasena: $("dContrasena").value || null,
    motivo: $("dMotivo").value.trim() || null, enviarMotivo: $("chkEnviarMotivo").checked,
  }).catch((e) => alAviso({ evento: "error", mensaje: e.message }));
};

function desinstalado(m) {
  S.instalando = false;
  S.terminado = true;
  $("dListoLead").textContent = m.borrados
    ? "Se quitaron los programas y los datos de este equipo. Gracias por usar BusinessPost."
    : `Se quitaron los programas. Tus datos siguen en ${m.datos}: si vuelves a instalar, BusinessPost los usa de nuevo.`;
  aviso("dAvisos", (m.avisos || []).join(" "), "info");
  ir("d-listo");
}
$("btnCerrarD").onclick = () => llamar("cerrar");

// ‑‑maqueta ‑‑pantalla X: llena datos de ejemplo y abre esa pantalla (solo para revisar el diseño).
async function maqueta(p) {
  if (p === "instalando" || p === "listo") { instalarTienda(); return; }
  if (p === "instalando-ejemplo") {
    ultimoResumen = { modo: "unica" }; S.instalando = true; ir("instalando");
    progreso({ etapa: 1, porcentaje: 42, nota: "Revisando los archivos con Windows Defender (la primera vez puede tardar varios minutos)…" }); return;
  }
  if (p === "listo-ejemplo") { terminado({ modo: "unica", empresa: "Mi negocio" }); return; }
  if (p === "principal") { irCajaAdicional(); return; }
  if (p === "recuperar") { $("lnkRecuperar").click(); return; }
  if (p === "error") { alAviso({ evento: "error", mensaje: "La instalación no pudo terminar el paso \"Base de datos (código 1)\".", registro: "x" }); return; }
  if (p === "error-reintentar") { alAviso({ evento: "error", mensaje: "BusinessPost quedó instalado, pero todavía no responde en este equipo.", registro: "x", reintentar: true }); return; }
  ir(p);
}

// ───────────────────────────── Utilidades e inicio ─────────────────────────────
function escapar(t) { return String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }

// Carrusel (marca, funciones y el aliado tutiendanueva.com), como en la maqueta. Cada tarjeta dura 9 s (antes 3,5 s) con un fundido
// suave (instalador.css) y se detiene mientras el mouse está encima.
let diapositiva = 0;
let carruselQuieto = false;
$("carrusel").addEventListener("mouseenter", () => { carruselQuieto = true; });
$("carrusel").addEventListener("mouseleave", () => { carruselQuieto = false; });
setInterval(() => {
  if (carruselQuieto) return;
  const sl = todas(".slide"), pu = todas(".puntos i");
  diapositiva = (diapositiva + 1) % sl.length;
  sl.forEach((x, i) => x.classList.toggle("ver", i === diapositiva));
  pu.forEach((x, i) => x.classList.toggle("on", i === diapositiva));
}, 9000);

document.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.target.matches("textarea") && $("velo").classList.contains("oculto")) {
    const boton = document.querySelector(".pantalla.activa .pie .btn-primario:not(:disabled)");
    if (boton && e.target.matches("input")) boton.click();
  }
  if (e.key === "Escape" && !$("velo").classList.contains("oculto")) $("modalNo").click();
});

(async () => {
  try {
    const inicio = await llamar("inicio");
    S.desinstalar = inicio.modo === "desinstalar";
    S.maqueta = inicio.maqueta;
    S.modoInstalado = inicio.modoInstalado || null;
    // Recuperar: si este equipo ya era el principal, se propone así. Si ya es una caja adicional, el enlace lo dice.
    $("chkRecuperarPrincipal").checked = S.modoInstalado === "principal";
    if (S.modoInstalado === "adicional") {
      $("ayudaAdicional").firstChild.textContent = "Este equipo está instalado como caja adicional. ";
      $("lnkAdicional").textContent = "Actualizar esta caja adicional";
    }
    $("version").textContent = `Versión ${inicio.version} · Hecho en Colombia para tu negocio`;
    if (S.desinstalar) prepararDesinstalar(inicio.desinstalacion); else ir("bienvenida");
    if (inicio.pantalla) maqueta(inicio.pantalla);
  } catch (e) {
    ir("bienvenida");
  }
})();
