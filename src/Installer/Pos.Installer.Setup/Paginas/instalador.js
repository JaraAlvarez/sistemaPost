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
const S = {
  modo: "unica", recuperar: false, prueba: false, clave: null, vista: null, cuenta: null, google: null, googleCuenta: null,
  backup: null, instalando: false, terminado: false, desinstalar: false, maqueta: false, nube: true, tipoNegocio: "SUPERMARKET",
};

const nombres = {
  bienvenida: "Bienvenida", equipo: "Este equipo", recuperar: "Recuperar backup", principal: "Equipo principal",
  licencia: "Licencia", cuenta: "Tu cuenta", negocio: "Tu negocio", instalando: "Instalación", listo: "¡Listo!",
  "d-inicio": "Antes de desinstalar", "d-progreso": "Desinstalación", "d-listo": "¡Listo!",
};

function pasosVisibles() {
  if (S.desinstalar) return ["d-inicio", "d-progreso", "d-listo"];
  if (S.recuperar) return ["bienvenida", "equipo", "recuperar", "instalando", "listo"];
  if (S.modo === "adicional") return ["bienvenida", "equipo", "principal", "instalando", "listo"];
  return ["bienvenida", "equipo", "licencia", "cuenta", "negocio", "instalando", "listo"];
}

// Pantallas que se muestran dentro de otro paso del panel izquierdo.
const pasoDe = { codigo: "instalando", error: "instalando", tipo: "negocio" };

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
  if (b.dataset.ir === "equipo") S.recuperar = false;
  if (b.dataset.ir === "licencia" && S.google === "esperando") cancelarGoogle();
  if (b.dataset.ir === "licencia" && S.prueba) { S.prueba = false; prepararCuenta(); }
  ir(b.dataset.ir);
}));

// ───────────────────────────── Este equipo ─────────────────────────────
todas(".modo").forEach((m) => m.addEventListener("click", () => {
  todas(".modo").forEach((x) => x.classList.remove("sel"));
  m.classList.add("sel");
  S.modo = m.dataset.modo;
}));

$("btnEquipo").onclick = () => {
  S.recuperar = false;
  if (S.modo === "adicional") { ir("principal"); buscarPrincipal(); return; }
  ir("licencia");
};

$("lnkRecuperar").onclick = () => {
  if (S.modo === "adicional") { todas(".modo")[0].click(); }
  S.recuperar = true;
  ir("recuperar");
};

// ───────────────────────────── Recuperar ─────────────────────────────
$("btnElegirBackup").onclick = async () => {
  try {
    const ruta = await llamar("elegirBackup");
    if (ruta) { S.backup = ruta; $("archivoBackup").textContent = ruta; aviso("errorRecuperar", ""); }
  } catch (e) { aviso("errorRecuperar", e.message); }
};

$("btnRecuperar").onclick = () => {
  const codigo = $("codigoRecuperacion").value.trim().toUpperCase();
  if (!S.backup) { aviso("errorRecuperar", "Elige el archivo del backup (.posbak)."); return; }
  if (codigo.replace(/[^A-Z0-9]/g, "").length < 20) { aviso("errorRecuperar", "Escribe el código de recuperación completo, tal como lo guardaste."); return; }
  instalar({ modo: S.modo, backup: S.backup, codigoRecuperacion: codigo });
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
$("btnPrincipal").onclick = () => {
  let url = $("servidorUrl").value.trim();
  const huella = $("servidorHuella").value.trim().replace(/[^0-9a-f]/gi, "").toUpperCase();
  if (!/^https:\/\/[^\s/]+(:\d+)?\/?$/i.test(url)) { aviso("errorPrincipal", "La dirección se ve así: https://192.168.1.10:5443/"); return; }
  if (huella.length !== 64) { aviso("errorPrincipal", "La huella tiene 64 letras y números. Cópiala de la administración del equipo principal (Cajas e impresoras)."); return; }
  if (!url.endsWith("/")) url += "/";
  instalar({ modo: "adicional", servidor: url, huella });
};

// ───────────────────────────── Licencia ─────────────────────────────
let temporizador = null;
$("clave").addEventListener("input", () => {
  const limpia = $("clave").value.replace(/[^A-Z0-9]/gi, "");
  $("empresa").classList.remove("ver");
  aviso("errorLicencia", "");
  $("btnLicencia").disabled = true;
  S.vista = null;
  clearTimeout(temporizador);
  if (limpia.length >= 23) temporizador = setTimeout(vistaPrevia, 350);
});

async function vistaPrevia() {
  const clave = $("clave").value.trim();
  try {
    const v = await llamar("vistaPrevia", { clave });
    S.vista = v;
    S.clave = clave;
    const fecha = new Date(v.validUntil).toLocaleDateString("es-CO", { day: "2-digit", month: "2-digit", year: "numeric" });
    $("empresaNombre").textContent = v.legalName;
    $("empresaDetalle").textContent = `NIT ${formatoNit(v.nit)} · Plan ${v.edition === "SINGLE" ? "Caja Única" : "Multicaja"} · ${v.subscriptionStatus === "TRIAL" ? "Prueba hasta" : "Vigente hasta"} ${fecha}`;
    $("empresa").classList.add("ver");
    if (v.edition === "SINGLE" && S.modo === "principal") {
      aviso("errorLicencia", "Tu plan es Caja Única: vuelve a \"Este equipo\" y elige \"Es la única caja\" (o cambia a Multicaja en tu portal).");
      return;
    }
    $("btnLicencia").disabled = false;
  } catch (e) { aviso("errorLicencia", e.message); }
}

$("btnLicencia").onclick = () => { S.prueba = false; prepararCuenta(); ir("cuenta"); };
$("lnkPrueba").onclick = () => { S.prueba = true; S.vista = null; S.clave = null; prepararCuenta(); ir("cuenta"); };

// ───────────────────────────── Tu cuenta ─────────────────────────────
function prepararCuenta() {
  const prueba = S.prueba;
  $("cuentaTitulo").textContent = prueba ? "Crea tu usuario de administrador" : "Entra con tu cuenta de BusinessPost";
  $("cuentaLead").textContent = prueba
    ? "Con él administrarás la tienda: crear cajeros, cargar productos y ver reportes. Usa tu correo; la contraseña debe tener al menos 10 caracteres."
    : "Es la misma cuenta de tu portal. Con ella administrarás la tienda: crear cajeros, cargar productos y ver reportes. Funciona también sin internet.";
  $("cuentaPortal").classList.toggle("oculto", prueba);
  $("campoNombre").classList.toggle("oculto", !prueba);
  $("campoConfirmar").classList.toggle("oculto", !prueba);
  $("campoTotp").classList.add("oculto");
  $("cuentaContrasena").placeholder = prueba ? "Mínimo 10 caracteres" : "Tu contraseña del portal";
  if (prueba && S.google === "esperando") cancelarGoogle();
  if (prueba) { S.google = null; S.googleCuenta = null; }
  vistaGoogle();
  aviso("avisoCuenta", "");
}

// ───── "Continuar con Google" ─────
// Google no permite entrar dentro de esta ventana: la nube crea una solicitud, abrimos el portal en el navegador de Windows y
// esperamos (la consulta la hace Puente.cs) a que el dueño pulse "Autorizar". Luego pedimos una contraseña LOCAL de BusinessPost.
function vistaGoogle() {
  const esperando = S.google === "esperando", autorizada = S.google === "autorizada";
  $("googleEspera").classList.toggle("oculto", !esperando);
  $("googleContrasena").classList.toggle("oculto", !autorizada);
  $("camposCorreo").classList.toggle("oculto", esperando || autorizada);
  $("cuentaPortal").classList.toggle("oculto", S.prueba || esperando || autorizada);
  $("btnGoogleCancelar").classList.toggle("oculto", !esperando);
  $("btnCuenta").disabled = esperando;
  if (autorizada) $("googleCorreo").textContent = S.googleCuenta.email;
}

function cancelarGoogle() {
  S.google = null;
  S.googleCuenta = null;
  llamar("googleCancelar").catch(() => {});
  vistaGoogle();
}

$("btnGoogle").onclick = async () => {
  if (!S.clave) { aviso("avisoCuenta", "Primero escribe tu clave de licencia.", "error"); return; }
  aviso("avisoCuenta", "");
  S.google = "esperando";
  $("googleCodigo").textContent = "····-····";
  vistaGoogle();
  try {
    const r = await llamar("googleIniciar", { clave: S.clave });
    if (S.google === "esperando") $("googleCodigo").textContent = r.codigo;
  } catch (e) {
    S.google = null;
    vistaGoogle();
    aviso("avisoCuenta", e.message, "error");
  }
};
$("lnkGoogleAbrir").onclick = () => llamar("googleAbrir").catch(() => {});
$("btnGoogleCancelar").onclick = () => { cancelarGoogle(); aviso("avisoCuenta", ""); };
$("lnkGoogleOtra").onclick = () => { cancelarGoogle(); aviso("avisoCuenta", ""); };

function alGoogle(m) {
  if (S.google !== "esperando") return;
  if (m.estado === "autorizada") {
    S.google = "autorizada";
    S.googleCuenta = { email: m.email, displayName: m.displayName };
    $("localContrasena").value = "";
    $("localConfirmar").value = "";
    vistaGoogle();
    aviso("avisoCuenta", "");
    setTimeout(() => $("localContrasena").focus(), 50);
    return;
  }
  S.google = null;
  vistaGoogle();
  aviso("avisoCuenta", m.mensaje || "No se completó el ingreso con Google. Intenta de nuevo o usa tu correo y contraseña.", "error");
}

// Misma regla que IngresoGoogle.ValidarContrasenaLocal (Puente.cs la vuelve a revisar).
function problemaContrasenaLocal(contrasena, confirmacion, correo) {
  if (contrasena.length < 12) return "La contraseña debe tener al menos 12 caracteres.";
  if (contrasena.length > 128) return "La contraseña es demasiado larga (máximo 128 caracteres).";
  if (!/\p{L}/u.test(contrasena) || !/\p{Nd}/u.test(contrasena)) return "Usa letras y números en la contraseña.";
  const usuario = String(correo || "").split("@")[0];
  if (new Set(contrasena).size <= 3 || (usuario.length >= 4 && contrasena.toLowerCase().includes(usuario.toLowerCase()))) {
    return "Esa contraseña es muy fácil de adivinar. Usa una frase o mezcla letras y números.";
  }
  return contrasena === confirmacion ? null : "Las dos contraseñas no coinciden.";
}

$("btnCuenta").onclick = async () => {
  if (S.google === "esperando") { aviso("avisoCuenta", "Termina en el navegador (pulsa \"Autorizar\") o cancela para usar tu correo.", "info"); return; }
  if (S.google === "autorizada") {
    const contrasena = $("localContrasena").value;
    const problema = problemaContrasenaLocal(contrasena, $("localConfirmar").value, S.googleCuenta.email);
    if (problema) { aviso("avisoCuenta", problema, "error"); return; }
    S.cuenta = { usuario: S.googleCuenta.email, nombre: S.googleCuenta.displayName, contrasena, enlace: true };
    aviso("avisoCuenta", "");
    prepararNegocio();
    ir("negocio");
    return;
  }

  const correo = $("cuentaCorreo").value.trim().toLowerCase();
  const contrasena = $("cuentaContrasena").value;
  if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(correo)) { aviso("avisoCuenta", "Escribe un correo válido.", "error"); return; }

  if (S.prueba) {
    const nombre = $("cuentaNombre").value.trim();
    const usuario = correo.split("@")[0];
    if (!nombre) { aviso("avisoCuenta", "Escribe tu nombre.", "error"); return; }
    if (contrasena.length < 10) { aviso("avisoCuenta", "La contraseña debe tener al menos 10 caracteres.", "error"); return; }
    if (new Set(contrasena).size <= 2 || (usuario.length >= 4 && contrasena.toLowerCase().includes(usuario))) {
      aviso("avisoCuenta", "Esa contraseña es muy fácil de adivinar. Usa una frase o mezcla letras y números.", "error"); return;
    }
    if (contrasena !== $("cuentaConfirmar").value) { aviso("avisoCuenta", "Las dos contraseñas no coinciden.", "error"); return; }
    S.cuenta = { usuario: correo, nombre, contrasena, enlace: null };
    prepararNegocio();
    ir("tipo");
    return;
  }

  if (!contrasena) { aviso("avisoCuenta", "Escribe tu contraseña del portal.", "error"); return; }
  const boton = $("btnCuenta");
  ocupado(boton, true, "Validando…");
  try {
    const r = await llamar("cuentaPortal", { clave: S.clave, correo, contrasena, codigo: $("cuentaTotp").value.trim() || null });
    S.cuenta = { usuario: r.email, nombre: r.displayName, contrasena, enlace: true };
    aviso("avisoCuenta", "");
    prepararNegocio();
    ir("tipo");
  } catch (e) {
    if (e.codigo === "LICENSE.OWNER_TOTP_REQUIRED") $("campoTotp").classList.remove("oculto");
    aviso("avisoCuenta", e.message, "error");
  } finally { ocupado(boton, false); }
};

// ───────────────────────────── Tipo de negocio (Fase 7-C parte A) ─────────────────────────────
function elegirTipo(tarjeta) {
  todas(".tipo-negocio").forEach((x) => { x.classList.remove("sel"); x.setAttribute("aria-checked", "false"); });
  tarjeta.classList.add("sel");
  tarjeta.setAttribute("aria-checked", "true");
  S.tipoNegocio = tarjeta.dataset.tipo;
}
todas(".tipo-negocio").forEach((t) => {
  t.addEventListener("click", () => elegirTipo(t));
  t.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); elegirTipo(t); } });
});
$("btnTipo").onclick = () => ir("negocio");

// ───────────────────────────── Tu negocio ─────────────────────────────
let municipioElegido = null;

function prepararNegocio() {
  const conLicencia = !!S.vista;
  $("negocioLead").textContent = conLicencia
    ? "Traemos de tu licencia la razón social y el NIT. Completa lo demás; puedes cambiarlo después en la administración."
    : "Estos datos salen en tus tiquetes y facturas. Puedes cambiarlos después en la administración.";
  $("razonSocial").readOnly = conLicencia;
  $("nitNumero").readOnly = conLicencia;
  if (conLicencia) {
    const [numero, dv] = S.vista.nit.split("-");
    $("razonSocial").value = S.vista.legalName;
    $("nitNumero").value = numero;
    $("nitDv").value = dv;
    if (!$("nombreComercial").value) $("nombreComercial").value = S.vista.legalName.replace(/\s+(S\.?A\.?S\.?|LTDA\.?|S\.?A\.?)$/i, "");
    if (!$("municipio").value && S.vista.city) $("municipio").value = S.vista.city;
  }
  if (!$("correoNegocio").value && S.cuenta) $("correoNegocio").value = S.cuenta.usuario;
}

// Dígito de verificación del NIT (DIAN, módulo 11): el mismo cálculo de Logica/Datos.cs.
function digitoVerificacion(numero) {
  if (!/^\d{5,15}$/.test(numero)) return "";
  const pesos = [3, 7, 13, 17, 19, 23, 29, 37, 41, 43, 47, 53, 59, 67, 71];
  let suma = 0;
  for (let i = 0; i < numero.length; i++) suma += Number(numero[numero.length - 1 - i]) * pesos[i];
  const resto = suma % 11;
  return String(resto > 1 ? 11 - resto : resto);
}

$("nitNumero").addEventListener("input", () => {
  $("nitNumero").value = $("nitNumero").value.replace(/\D/g, "");
  $("nitDv").value = digitoVerificacion($("nitNumero").value);
});

let busqueda = null;
$("municipio").addEventListener("input", () => {
  municipioElegido = null;
  clearTimeout(busqueda);
  busqueda = setTimeout(async () => {
    const texto = $("municipio").value;
    if (texto.trim().length < 2) { $("sugerencias").classList.add("oculto"); return; }
    const lista = await llamar("municipios", { texto });
    $("sugerencias").innerHTML = lista.map((m, i) => `<div data-i="${i}">${escapar(titulo(m.nombre))} <small>${escapar(titulo(m.departamento))}</small></div>`).join("");
    $("sugerencias").classList.toggle("oculto", lista.length === 0);
    todas("#sugerencias div").forEach((el) => el.addEventListener("mousedown", (e) => {
      e.preventDefault();
      const m = lista[+el.dataset.i];
      municipioElegido = m.codigo;
      $("municipio").value = `${titulo(m.nombre)} (${titulo(m.departamento)})`;
      $("sugerencias").classList.add("oculto");
    }));
  }, 180);
});
$("municipio").addEventListener("blur", () => setTimeout(() => $("sugerencias").classList.add("oculto"), 150));
$("municipio").addEventListener("focus", () => { if (!municipioElegido && $("municipio").value) $("municipio").dispatchEvent(new Event("input")); });

$("btnInstalar").onclick = () => {
  const numero = $("nitNumero").value.trim();
  const dv = $("nitDv").value.trim();
  const persona = $("tipoPersona").value;
  const requerido = [["razonSocial", "la razón social o el nombre"], ["direccion", "la dirección del local"]];
  for (const [id, texto] of requerido) if (!$(id).value.trim()) { aviso("errorNegocio", `Escribe ${texto}.`); $(id).focus(); return; }
  if (!/^\d{5,15}$/.test(numero) || dv === "") { aviso("errorNegocio", "Escribe el NIT sin puntos ni dígito de verificación (de 5 a 15 números)."); $("nitNumero").focus(); return; }
  if (!municipioElegido) { aviso("errorNegocio", "Escribe el municipio y elígelo de la lista."); $("municipio").focus(); return; }
  const correo = $("correoNegocio").value.trim();
  if (correo && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(correo)) { aviso("errorNegocio", "El correo del negocio no es válido."); return; }
  aviso("errorNegocio", "");
  instalar({
    modo: S.modo,
    clave: S.prueba ? null : S.clave,
    edicion: S.vista ? S.vista.edition : null,
    negocio: {
      razonSocial: $("razonSocial").value.trim(), nombreComercial: $("nombreComercial").value.trim(), tipoPersona: persona,
      tipoDocumento: "NIT", numero, dv, regimen: $("regimen").value, municipio: municipioElegido, direccion: $("direccion").value.trim(),
      telefono: $("telefono").value.trim() || null, correo: correo || null, tipoNegocio: S.tipoNegocio,
    },
    propietario: { usuario: S.cuenta.usuario, nombre: S.cuenta.nombre, contrasena: S.cuenta.contrasena, enlace: null },
  });
};

// ───────────────────────────── Instalación ─────────────────────────────
let ultimoResumen = null;

function instalar(solicitud) {
  S.instalando = true;
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
    case "google": alGoogle(m); break;
    case "pedirCierre": pedirCierre(); break;
    case "progreso":
      if (S.desinstalar) { $("dBarra").style.width = m.porcentaje + "%"; $("dNota").textContent = m.nota || ""; }
      else progreso(m);
      break;
    case "codigo":
      progreso({ etapa: 6, porcentaje: m.porcentaje || 98 });
      $("codigoTexto").textContent = m.codigo;
      $("impresionCodigo").textContent = m.codigo;
      $("impresionEmpresa").textContent = ultimoResumen && ultimoResumen.negocio ? `${ultimoResumen.negocio.razonSocial} · NIT ${ultimoResumen.negocio.numero}-${ultimoResumen.negocio.dv}` : "";
      $("impresionFecha").textContent = "Generado el " + new Date().toLocaleString("es-CO");
      aviso("avisoCodigo", m.aviso || "", "info");
      ir("codigo");
      break;
    case "terminado": S.desinstalar ? desinstalado(m) : terminado(m); break;
    case "error":
      S.instalando = false;
      if (S.desinstalar) { ir("d-inicio"); aviso("dError", m.mensaje); break; }
      $("errorTexto").textContent = m.mensaje;
      $("btnVerRegistro").classList.toggle("oculto", !m.registro);
      $("btnVerRegistro").onclick = () => llamar("verRegistro", { ruta: m.registro });
      ir("error");
      break;
  }
}

$("chkCodigo").onchange = () => { $("btnCodigo").disabled = !$("chkCodigo").checked; };
$("btnImprimir").onclick = () => window.print();
$("btnCodigo").onclick = async () => {
  const boton = $("btnCodigo");
  ocupado(boton, true);
  try { await llamar("confirmarCodigo"); aviso("errorCodigo", ""); }
  catch (e) { aviso("errorCodigo", e.message); ocupado(boton, false); }
};
$("btnSalirError").onclick = () => { S.terminado = true; llamar("cerrar"); };

function terminado(m) {
  S.instalando = false;
  S.terminado = true;
  progreso({ etapa: 7, porcentaje: 100, nota: "" });
  const modos = { unica: ["Única caja", "Caja Única"], principal: ["Equipo principal", "Multicaja"], adicional: ["Caja adicional", "Multicaja"] };
  const [modo, plan] = modos[m.modo || S.modo] || modos.unica;
  $("resEmpresa").textContent = m.empresa || (S.modo === "adicional" ? "Conectada al principal" : "Tu negocio");
  $("resModo").textContent = modo;
  $("resPlan").textContent = plan;
  if (S.recuperar) { $("resLicencia").textContent = "Desde tu backup"; $("resVigencia").textContent = "datos recuperados"; }
  else if (S.modo === "adicional") { $("resLicencia").textContent = "La del principal"; $("resVigencia").textContent = "empareja la caja al abrir"; }
  else if (S.vista && m.licenciaActiva !== false) {
    $("resLicencia").textContent = "Licencia activa";
    $("resVigencia").textContent = "hasta " + new Date(S.vista.validUntil).toLocaleDateString("es-CO");
  } else { $("resLicencia").textContent = "Prueba gratis"; $("resVigencia").textContent = "30 días"; }
  $("listoLead").textContent = S.modo === "adicional"
    ? "La caja quedó instalada. Al abrir BusinessPost, empareja esta caja con el código que genera el equipo principal."
    : "BusinessPost quedó instalado y tus backups automáticos ya están activos.";
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
  if (p === "instalando" || p === "listo") {
    S.vista = { legalName: "Supermercado La Economía SAS", nit: "900123456-8", edition: "MULTI", validUntil: "2026-11-16T00:00:00Z" };
    instalar({ modo: "principal", clave: "POS-X", negocio: { razonSocial: "Supermercado La Economía SAS", numero: "900123456", dv: "8" } });
    return;
  }
  if (p === "licencia") { $("clave").value = "POS-ABCDE-FGHJK-LMNPQ-RSTUV"; ir("licencia"); await vistaPrevia(); return; }
  if (p === "cuenta-prueba") { S.prueba = true; prepararCuenta(); ir("cuenta"); return; }
  if (p === "cuenta-google") { S.clave = "POS-X"; S.prueba = false; prepararCuenta(); ir("cuenta"); $("btnGoogle").click(); return; }
  if (p === "tipo") { S.cuenta = { usuario: "dueno@correo.co" }; ir("tipo"); return; }
  if (p === "negocio") { S.vista = { legalName: "Supermercado La Economía SAS", nit: "900123456-8", city: "Medellín" }; S.cuenta = { usuario: "dueno@correo.co" }; prepararNegocio(); ir("negocio"); return; }
  // Pantallas completas con datos de ejemplo (capturas del manual de instalación: docs/imagenes/instalacion).
  const ejemplo = { legalName: "Supermercado La Economía SAS", nit: "900123456-8", edition: "SINGLE", subscriptionStatus: "ACTIVE", validUntil: "2026-11-16T00:00:00Z", city: "Medellín" };
  if (p === "cuenta-ejemplo") {
    S.vista = ejemplo; S.clave = "POS-ABCDE-FGHJK-LMNPQ-RSTUV"; S.prueba = false; prepararCuenta();
    ir("cuenta"); $("cuentaCorreo").value = "dueno@laeconomia.co"; $("cuentaContrasena").value = "contrasena-de-ejemplo"; return;
  }
  if (p === "negocio-ejemplo") {
    S.vista = ejemplo; S.cuenta = { usuario: "dueno@laeconomia.co" }; prepararNegocio();
    $("municipio").value = "Medellín (Antioquia)"; $("regimen").value = "48"; $("direccion").value = "Calle 50 # 45-20, local 3";
    $("telefono").value = "604 444 5566"; $("correoNegocio").value = "facturas@laeconomia.co"; ir("negocio"); $("direccion").blur(); return;
  }
  if (p === "instalando-ejemplo") {
    ultimoResumen = { modo: "unica" }; S.instalando = true; ir("instalando");
    progreso({ etapa: 1, porcentaje: 42, nota: "Revisando los archivos con Windows Defender (la primera vez puede tardar varios minutos)…" }); return;
  }
  if (p === "codigo-ejemplo") {
    ultimoResumen = { negocio: { razonSocial: ejemplo.legalName, numero: "900123456", dv: "8" } };
    alAviso({ evento: "codigo", codigo: "7F3A-91C2-0B44-E1D8-AB12-CD34" }); return;
  }
  if (p === "listo-ejemplo") { S.vista = ejemplo; S.modo = "unica"; terminado({ modo: "unica", empresa: ejemplo.legalName, licenciaActiva: true }); return; }
  if (p === "codigo") { alAviso({ evento: "codigo", codigo: "7F3A-91C2-0B44-E1D8-AB12-CD34", aviso: "La licencia no se pudo activar ahora (sin internet). BusinessPost funciona en prueba; actívala luego en Administración → Licencia." }); return; }
  if (p === "principal") { S.modo = "adicional"; ir("principal"); buscarPrincipal(); return; }
  if (p === "error") { alAviso({ evento: "error", mensaje: "La instalación no pudo terminar el paso \"Base de datos (código 1)\".", registro: "x" }); return; }
  ir(p);
}

// ───────────────────────────── Utilidades e inicio ─────────────────────────────
function escapar(t) { return String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }
function titulo(t) { return String(t || "").toLowerCase().replace(/(^|[\s(.,-])(\p{L})/gu, (_, a, b) => a + b.toUpperCase()); }
function formatoNit(nit) { const [n, dv] = String(nit).split("-"); return n.replace(/\B(?=(\d{3})+(?!\d))/g, ".") + (dv ? "-" + dv : ""); }

// Carrusel (marca, funciones y el aliado tutiendanueva.com), como en la maqueta.
let diapositiva = 0;
setInterval(() => {
  const sl = todas(".slide"), pu = todas(".puntos i");
  diapositiva = (diapositiva + 1) % sl.length;
  sl.forEach((x, i) => x.classList.toggle("ver", i === diapositiva));
  pu.forEach((x, i) => x.classList.toggle("on", i === diapositiva));
}, 3500);

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
    S.nube = inicio.nube;
    $("version").textContent = `Versión ${inicio.version} · Hecho en Colombia para tu negocio`;
    if (S.desinstalar) prepararDesinstalar(inicio.desinstalacion); else ir("bienvenida");
    if (inicio.pantalla) maqueta(inicio.pantalla);
  } catch (e) {
    ir("bienvenida");
  }
})();
