// Genera el PDF (A4) de un manual de docs/ (por defecto docs/manual-instalacion.md → docs/manual-instalacion.pdf) con sus capturas
// (docs/imagenes/instalacion o docs/imagenes/uso), con Microsoft Edge sin ventana (protocolo de DevTools: Page.printToPDF con número de
// página). Convierte el MISMO subconjunto de Markdown que el portal (src/Cloud/Pos.Cloud.Host/Downloads/InstallManual.cs): títulos,
// párrafos, listas, citas, negrita, código, enlaces http(s) y capturas; además (solo en el PDF) tablas |…|, líneas --- y comentarios <!-- -->.
// Las citas que empiezan por **Consejo** o **Importante** salen como recuadros de color; una línea <!-- salto-de-pagina --> abre página.
//
// Uso (desde la raíz del repositorio; Node 22 o superior):
//   node tools/scripts/manual-pdf.mjs [--manual manual-instalacion] [--salida docs/<manual>.pdf]
//                                     [--encabezado "Guía para el dueño del negocio"] [--edge "C:\...\msedge.exe"]
//   node tools/scripts/manual-pdf.mjs --manual manual-puesta-en-marcha
//   node tools/scripts/manual-pdf.mjs --manual manual-administracion
//   node tools/scripts/manual-pdf.mjs --manual manual-caja --encabezado "Guía para el cajero"
//   node tools/scripts/manual-pdf.mjs --manual manual-integraciones
//   node tools/scripts/manual-pdf.mjs --manual manual-beneficios
// El PDF de instalación se publica junto al instalador en /updates del VPS (docs/despliegue-nube.md, "Manual de instalación en PDF").
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const raiz = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const argumento = (nombre, defecto) => { const i = process.argv.indexOf(nombre); return i > 0 && i + 1 < process.argv.length ? process.argv[i + 1] : defecto; };
const manual = argumento("--manual", "manual-instalacion");
if (!/^[a-z0-9-]+$/.test(manual)) throw new Error("--manual es el nombre del archivo de docs/ sin la extensión .md");
const salida = resolve(raiz, argumento("--salida", `docs/${manual}.pdf`));
const edge = argumento("--edge", "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe");
const docs = join(raiz, "docs");
// Encabezado pequeño sobre el título: cambia según a quién va el manual (p. ej. --encabezado "Guía para el cajero").
const encabezado = argumento("--encabezado", "Guía para el dueño del negocio");
// Una línea <!-- salto-de-pagina --> fuerza una página nueva en el PDF (el portal no la muestra); los demás comentarios se ignoran.
const SALTO = "\u0001salto-de-pagina\u0001";
const markdown = readFileSync(join(docs, `${manual}.md`), "utf8")
  .replace(/^[ \t]*<!--\s*salto-de-pagina\s*-->[ \t]*$/gim, SALTO)
  .replace(/<!--[\s\S]*?-->/g, "");
const titulo = (/^# (.+)$/m.exec(markdown)?.[1] ?? "Manual de BusinessPost").trim();

// ───────────── Markdown → HTML (mismo subconjunto y mismas reglas de seguridad que SafeMarkdown) ─────────────
const codificar = (t) => t.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
function enLinea(texto) {
  let html = "", i = 0;
  while (i < texto.length) {
    const fin = (marca, desde) => texto.indexOf(marca, desde);
    if (texto[i] === "`" && fin("`", i + 1) > 0) { const f = fin("`", i + 1); html += `<code>${codificar(texto.slice(i + 1, f))}</code>`; i = f + 1; continue; }
    if (texto.startsWith("**", i) && fin("**", i + 2) > 0) { const f = fin("**", i + 2); html += `<strong>${enLinea(texto.slice(i + 2, f))}</strong>`; i = f + 2; continue; }
    if (texto[i] === "[") {
      const ct = fin("](", i + 1), cu = ct < 0 ? -1 : fin(")", ct + 2);
      if (cu > 0) {
        const t = texto.slice(i + 1, ct), url = texto.slice(ct + 2, cu).trim();
        html += /^https?:\/\//i.test(url) ? `<a href="${codificar(url)}">${enLinea(t)}</a>` : enLinea(t);
        i = cu + 1; continue;
      }
    }
    html += codificar(texto[i]); i++;
  }
  return html;
}
const imagen = (linea) => {
  const m = /^!\[([^\]]*)\]\(imagenes\/(instalacion|uso)\/([a-z0-9][a-z0-9-]{0,80}\.png)\)$/.exec(linea);
  return m ? { pie: m[1].trim(), carpeta: m[2], archivo: m[3] } : null;
};
const ancla = (t) => t.toLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");

function convertir(md) {
  const html = [], parrafo = [];
  let lista = null, cita = null, tabla = null;
  const cerrarParrafo = () => { if (parrafo.length) { html.push(`<p>${enLinea(parrafo.join(" "))}</p>`); parrafo.length = 0; } };
  const cerrarLista = () => { if (lista) { html.push(`</${lista}>`); lista = null; } };
  const cerrarCita = () => {
    if (!cita) return;
    const texto = cita.join(" ");
    const clase = /^\*\*Importante/i.test(texto) ? ' class="importante"' : /^\*\*Consejo/i.test(texto) ? ' class="consejo"' : "";
    html.push(`<blockquote${clase}><p>${enLinea(texto)}</p></blockquote>`);
    cita = null;
  };
  const celdas = (fila) => fila.replace(/^\|/, "").replace(/\|$/, "").split("|").map((c) => c.trim());
  const cerrarTabla = () => {
    if (!tabla) return;
    const [cabeza, , ...cuerpo] = tabla;
    html.push(`<table><thead><tr>${celdas(cabeza).map((c) => `<th>${enLinea(c)}</th>`).join("")}</tr></thead><tbody>`
      + cuerpo.map((f) => `<tr>${celdas(f).map((c) => `<td>${enLinea(c)}</td>`).join("")}</tr>`).join("") + "</tbody></table>");
    tabla = null;
  };
  for (const cruda of md.replace(/\r\n/g, "\n").split("\n")) {
    const linea = cruda.trimEnd(), t = linea.trimStart();
    if (!t) { cerrarParrafo(); cerrarLista(); cerrarCita(); cerrarTabla(); continue; }
    if (t.startsWith("|")) { cerrarParrafo(); cerrarLista(); cerrarCita(); (tabla ??= []).push(t); continue; }
    cerrarTabla();
    if (t === SALTO) { cerrarParrafo(); cerrarLista(); cerrarCita(); html.push('<div class="salto"></div>'); continue; }
    if (/^-{3,}$/.test(t)) { cerrarParrafo(); cerrarLista(); cerrarCita(); html.push("<hr>"); continue; }
    if (t.startsWith(">")) { cerrarParrafo(); cerrarLista(); (cita ??= []).push(t.replace(/^>\s?/, "")); continue; }
    cerrarCita();
    const img = imagen(t);
    if (img) {
      cerrarParrafo(); cerrarLista();
      html.push(`<figure><img src="${pathToFileURL(join(docs, "imagenes", img.carpeta, img.archivo)).href}" alt="${codificar(img.pie)}"><figcaption>${codificar(img.pie)}</figcaption></figure>`);
      continue;
    }
    const h = /^(#{1,4}) (.+)$/.exec(t);
    if (h) { cerrarParrafo(); cerrarLista(); const n = h[1].length; html.push(`<h${n} id="${ancla(h[2])}">${enLinea(h[2].trim())}</h${n}>`); continue; }
    const li = /^(?:([-*])|(\d{1,3})\.) (.+)$/.exec(t);
    if (li) {
      cerrarParrafo();
      const tipo = li[1] ? "ul" : "ol";
      if (lista !== tipo) { cerrarLista(); html.push(tipo === "ol" && +li[2] > 1 ? `<ol start="${+li[2]}">` : `<${tipo}>`); lista = tipo; }
      html.push(`<li>${enLinea(li[3].trim())}</li>`);
      continue;
    }
    if (lista && linea.startsWith("  ")) { html[html.length - 1] = html[html.length - 1].replace(/<\/li>$/, ` ${enLinea(t)}</li>`); continue; }
    cerrarLista();
    parrafo.push(t);
  }
  cerrarParrafo(); cerrarLista(); cerrarCita(); cerrarTabla();
  return html.join("\n");
}

// La tipografía Inter del POS va incrustada (una página file:// no puede cargar fuentes de otra carpeta).
const fuente = (archivo) => "data:font/woff2;base64," + readFileSync(join(raiz, "src", "Client", "Pos.Client", "wwwroot", "fonts", archivo)).toString("base64");
const pagina = `<!doctype html>
<html lang="es"><head><meta charset="utf-8"><title>${codificar(titulo)}</title>
<style>
  @font-face { font-family: "Inter"; src: url("${fuente("inter-latin.woff2")}") format("woff2"); font-weight: 100 900; unicode-range: U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+2000-206F, U+2074, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD; }
  @font-face { font-family: "Inter"; src: url("${fuente("inter-latin-ext.woff2")}") format("woff2"); font-weight: 100 900; unicode-range: U+0100-024F, U+0259, U+1E00-1EFF, U+2020, U+20A0-20AB, U+20AD-20CF, U+2113, U+2C60-2C7F, U+A720-A7FF; }
  @page { size: A4; margin: 16mm 16mm 18mm; }
  :root { --morado: #5B3DF5; --tinta: #1B1F3B; --suave: #5A6075; --linea: #E4E1F5; --fondo: #F6F4FF; }
  * { box-sizing: border-box; }
  body { font-family: "Inter", "Segoe UI", Arial, sans-serif; color: var(--tinta); font-size: 10.5pt; line-height: 1.5; margin: 0; }
  h1 { font-size: 24pt; line-height: 1.15; color: var(--morado); margin: 0 0 6mm; letter-spacing: -.01em; }
  h1::before { content: "BusinessPost · ${encabezado.replace(/["\\<>]/g, "")}"; display: block; font-size: 9pt; letter-spacing: .08em; text-transform: uppercase; color: var(--suave); margin-bottom: 3mm; font-weight: 600; }
  h2 { font-size: 15pt; color: var(--morado); margin: 8mm 0 3mm; padding-bottom: 1.5mm; border-bottom: 2px solid var(--linea); break-after: avoid; }
  h3 { font-size: 12pt; color: var(--tinta); margin: 6mm 0 2mm; break-after: avoid; }
  h4 { font-size: 11pt; margin: 4mm 0 1.5mm; break-after: avoid; }
  #inicio-rapido { margin-top: 6mm; }
  #inicio-rapido + p + ol { background: var(--fondo); border: 1px solid var(--linea); border-radius: 3mm; padding: 4mm 6mm 4mm 12mm; }
  #inicio-rapido + p + ol li { margin-bottom: 1.6mm; }
  [id="1-antes-de-empezar"] { break-before: page; margin-top: 0; }
  [id="15-hoja-de-atajos-para-imprimir"] { break-before: page; margin-top: 0; }
  .salto { break-before: page; height: 0; }
  .salto + h2, .salto + h3 { margin-top: 0; }
  p, ul, ol { margin: 0 0 2.5mm; }
  li { margin: 0 0 1mm; }
  code { font-family: Consolas, "Cascadia Mono", monospace; font-size: .92em; background: var(--fondo); border: 1px solid var(--linea); border-radius: 1.2mm; padding: 0 1.2mm; }
  strong { font-weight: 650; }
  a { color: var(--morado); }
  blockquote { margin: 0 0 3mm; padding: 2.5mm 4mm; border-left: 1.2mm solid var(--morado); background: var(--fondo); border-radius: 0 2mm 2mm 0; }
  blockquote p { margin: 0; }
  figure { margin: 3mm 0 5mm; break-inside: avoid; text-align: center; }
  figure img { max-width: 100%; max-height: 78mm; border: 1px solid var(--linea); border-radius: 2.5mm; box-shadow: 0 1mm 4mm rgba(17, 21, 125, .10); }
  figcaption { font-size: 8.8pt; color: var(--suave); margin-top: 1.8mm; }
  blockquote.consejo { border-left-color: #1E9E6A; background: #EEF9F3; }
  blockquote.importante { border-left-color: #D98B06; background: #FFF6E5; }
  table { width: 100%; border-collapse: collapse; margin: 1mm 0 4mm; font-size: 9.2pt; line-height: 1.4; }
  thead { display: table-header-group; }
  tr { break-inside: avoid; }
  th { background: var(--fondo); font-weight: 650; text-align: left; }
  th, td { border: 1px solid var(--linea); padding: 1.4mm 2mm; vertical-align: top; }
  hr { border: 0; border-top: 1px solid var(--linea); margin: 5mm 0; }
</style></head>
<body>${convertir(markdown)}</body></html>`;

// ───────────── Edge sin ventana → PDF (hasta 3 intentos: Edge a veces corta la conexión de depuración) ─────────────
const espera = (ms) => new Promise((r) => setTimeout(r, ms));
const conLimite = (promesa, ms, que) => Promise.race([promesa, espera(ms).then(() => { throw new Error(`Edge no respondió a tiempo (${que})`); })]);

async function imprimir() {
  const temporal = mkdtempSync(join(tmpdir(), "manual-bp-"));
  const html = join(temporal, "manual.html");
  writeFileSync(html, pagina, "utf8");
  const puerto = 9300 + Math.floor(Math.random() * 500);
  const navegador = spawn(edge, ["--headless=new", `--remote-debugging-port=${puerto}`, `--user-data-dir=${join(temporal, "perfil")}`,
    "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--allow-file-access-from-files", "about:blank"], { stdio: "ignore" });
  let ws, secuencia = 0;
  const pendientes = new Map();
  const cdp = (method, params = {}) => conLimite(new Promise((ok, mal) => {
    const id = ++secuencia; pendientes.set(id, { ok, mal }); ws.send(JSON.stringify({ id, method, params }));
  }), 120000, method);
  try {
    let objetivos;
    for (let i = 0; i < 60 && !objetivos; i++) { try { objetivos = await (await fetch(`http://127.0.0.1:${puerto}/json/list`)).json(); } catch { await espera(250); } }
    if (!objetivos) throw new Error("Edge no abrió el puerto de depuración");
    ws = new WebSocket(objetivos.find((o) => o.type === "page").webSocketDebuggerUrl);
    await conLimite(new Promise((r) => ws.addEventListener("open", r)), 15000, "conexión");
    ws.addEventListener("message", (e) => { const m = JSON.parse(e.data); const p = m.id && pendientes.get(m.id); if (p) { pendientes.delete(m.id); m.error ? p.mal(new Error(m.error.message)) : p.ok(m.result); } });
    ws.addEventListener("close", (e) => { for (const p of pendientes.values()) p.mal(new Error(`Edge cerró la conexión (${e.code})`)); pendientes.clear(); });
    await cdp("Page.enable");
    await cdp("Page.navigate", { url: pathToFileURL(html).href });
    await espera(2500);
    await cdp("Runtime.evaluate", { expression: "document.fonts.ready.then(() => Promise.all([...document.images].map(i => i.decode().catch(() => null))))", awaitPromise: true });
    const pie = `<div style="font-family: Segoe UI, Arial; font-size: 7.5pt; color: #8A8FA3; width: 100%; padding: 0 16mm; display: flex; justify-content: space-between;">
      <span>${codificar(titulo)}</span><span>Página <span class="pageNumber"></span> de <span class="totalPages"></span></span></div>`;
    const pdf = await cdp("Page.printToPDF", {
      printBackground: true, preferCSSPageSize: true, displayHeaderFooter: true, headerTemplate: "<span></span>", footerTemplate: pie,
      transferMode: "ReturnAsStream", // un PDF grande (3–4 MB) en un solo mensaje cuelga o corta (1006) el protocolo: se lee por partes
    });
    const partes = [];
    for (let fin = false; !fin;) {
      const parte = await cdp("IO.read", { handle: pdf.stream, size: 1 << 20 });
      partes.push(Buffer.from(parte.data, parte.base64Encoded ? "base64" : "utf8"));
      fin = parte.eof;
    }
    await cdp("IO.close", { handle: pdf.stream });
    writeFileSync(salida, Buffer.concat(partes));
  } finally {
    try { ws?.close(); } catch { /* ya cerrado */ }
    navegador.kill();
    await espera(500);
    try { rmSync(temporal, { recursive: true, force: true }); } catch { /* Edge puede tardar en soltar el perfil */ }
  }
}

for (let intento = 1; ; intento++) {
  try {
    await imprimir();
    console.log(`PDF: ${salida} (${(statSync(salida).size / 1024 / 1024).toFixed(2)} MB)`);
    break;
  } catch (error) {
    if (intento < 3) { console.error(`Intento ${intento} falló (${error.message}); se reintenta.`); await espera(1500); continue; }
    console.error("No se pudo generar el PDF:", error);
    process.exitCode = 1;
    break;
  }
}
