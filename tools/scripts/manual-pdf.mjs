// Genera docs/manual-instalacion.pdf (A4) desde docs/manual-instalacion.md y sus capturas (docs/imagenes/instalacion), con Microsoft Edge
// sin ventana (protocolo de DevTools: Page.printToPDF con número de página). Convierte el MISMO subconjunto de Markdown que el portal
// (src/Cloud/Pos.Cloud.Host/Downloads/InstallManual.cs): títulos, párrafos, listas, citas, negrita, código, enlaces http(s) y capturas.
//
// Uso (desde la raíz del repositorio; Node 22 o superior):
//   node tools/scripts/manual-pdf.mjs [--salida docs/manual-instalacion.pdf] [--edge "C:\...\msedge.exe"]
// El PDF se publica junto al instalador en /updates del VPS (docs/despliegue-nube.md, "Manual de instalación en PDF").
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const raiz = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const argumento = (nombre, defecto) => { const i = process.argv.indexOf(nombre); return i > 0 && i + 1 < process.argv.length ? process.argv[i + 1] : defecto; };
const salida = resolve(raiz, argumento("--salida", "docs/manual-instalacion.pdf"));
const edge = argumento("--edge", "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe");
const docs = join(raiz, "docs");
const markdown = readFileSync(join(docs, "manual-instalacion.md"), "utf8");

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
  const m = /^!\[([^\]]*)\]\(imagenes\/instalacion\/([a-z0-9][a-z0-9-]{0,80}\.png)\)$/.exec(linea);
  return m ? { pie: m[1].trim(), archivo: m[2] } : null;
};
const ancla = (t) => t.toLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");

function convertir(md) {
  const html = [], parrafo = [];
  let lista = null, cita = null;
  const cerrarParrafo = () => { if (parrafo.length) { html.push(`<p>${enLinea(parrafo.join(" "))}</p>`); parrafo.length = 0; } };
  const cerrarLista = () => { if (lista) { html.push(`</${lista}>`); lista = null; } };
  const cerrarCita = () => { if (cita) { html.push(`<blockquote><p>${enLinea(cita.join(" "))}</p></blockquote>`); cita = null; } };
  for (const cruda of md.replace(/\r\n/g, "\n").split("\n")) {
    const linea = cruda.trimEnd(), t = linea.trimStart();
    if (!t) { cerrarParrafo(); cerrarLista(); cerrarCita(); continue; }
    if (t.startsWith(">")) { cerrarParrafo(); cerrarLista(); (cita ??= []).push(t.replace(/^>\s?/, "")); continue; }
    cerrarCita();
    const img = imagen(t);
    if (img) {
      cerrarParrafo(); cerrarLista();
      html.push(`<figure><img src="${pathToFileURL(join(docs, "imagenes", "instalacion", img.archivo)).href}" alt="${codificar(img.pie)}"><figcaption>${codificar(img.pie)}</figcaption></figure>`);
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
  cerrarParrafo(); cerrarLista(); cerrarCita();
  return html.join("\n");
}

// La tipografía Inter del POS va incrustada (una página file:// no puede cargar fuentes de otra carpeta).
const fuente = (archivo) => "data:font/woff2;base64," + readFileSync(join(raiz, "src", "Client", "Pos.Client", "wwwroot", "fonts", archivo)).toString("base64");
const pagina = `<!doctype html>
<html lang="es"><head><meta charset="utf-8"><title>Manual de instalación de BusinessPost</title>
<style>
  @font-face { font-family: "Inter"; src: url("${fuente("inter-latin.woff2")}") format("woff2"); font-weight: 100 900; unicode-range: U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+2000-206F, U+2074, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD; }
  @font-face { font-family: "Inter"; src: url("${fuente("inter-latin-ext.woff2")}") format("woff2"); font-weight: 100 900; unicode-range: U+0100-024F, U+0259, U+1E00-1EFF, U+2020, U+20A0-20AB, U+20AD-20CF, U+2113, U+2C60-2C7F, U+A720-A7FF; }
  @page { size: A4; margin: 16mm 16mm 18mm; }
  :root { --morado: #5B3DF5; --tinta: #1B1F3B; --suave: #5A6075; --linea: #E4E1F5; --fondo: #F6F4FF; }
  * { box-sizing: border-box; }
  body { font-family: "Inter", "Segoe UI", Arial, sans-serif; color: var(--tinta); font-size: 10.5pt; line-height: 1.5; margin: 0; }
  h1 { font-size: 24pt; line-height: 1.15; color: var(--morado); margin: 0 0 6mm; letter-spacing: -.01em; }
  h1::before { content: "BusinessPost · Guía para el dueño del negocio"; display: block; font-size: 9pt; letter-spacing: .08em; text-transform: uppercase; color: var(--suave); margin-bottom: 3mm; font-weight: 600; }
  h2 { font-size: 15pt; color: var(--morado); margin: 8mm 0 3mm; padding-bottom: 1.5mm; border-bottom: 2px solid var(--linea); break-after: avoid; }
  h3 { font-size: 12pt; color: var(--tinta); margin: 6mm 0 2mm; break-after: avoid; }
  h4 { font-size: 11pt; margin: 4mm 0 1.5mm; break-after: avoid; }
  #inicio-rapido { margin-top: 6mm; }
  #inicio-rapido + p + ol { background: var(--fondo); border: 1px solid var(--linea); border-radius: 3mm; padding: 4mm 6mm 4mm 12mm; }
  #inicio-rapido + p + ol li { margin-bottom: 1.6mm; }
  [id="1-antes-de-empezar"] { break-before: page; margin-top: 0; }
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
</style></head>
<body>${convertir(markdown)}</body></html>`;

// ───────────── Edge sin ventana → PDF ─────────────
const temporal = mkdtempSync(join(tmpdir(), "manual-bp-"));
const html = join(temporal, "manual.html");
writeFileSync(html, pagina, "utf8");
const puerto = 9300 + Math.floor(Math.random() * 500);
const navegador = spawn(edge, ["--headless=new", `--remote-debugging-port=${puerto}`, `--user-data-dir=${join(temporal, "perfil")}`,
  "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--allow-file-access-from-files", "about:blank"], { stdio: "ignore" });
const espera = (ms) => new Promise((r) => setTimeout(r, ms));
let ws, secuencia = 0;
const pendientes = new Map();
const cdp = (method, params = {}) => new Promise((ok, mal) => { const id = ++secuencia; pendientes.set(id, { ok, mal }); ws.send(JSON.stringify({ id, method, params })); });
try {
  let objetivos;
  for (let i = 0; i < 60 && !objetivos; i++) { try { objetivos = await (await fetch(`http://127.0.0.1:${puerto}/json/list`)).json(); } catch { await espera(250); } }
  ws = new WebSocket(objetivos.find((o) => o.type === "page").webSocketDebuggerUrl);
  await new Promise((r) => ws.addEventListener("open", r));
  ws.addEventListener("message", (e) => { const m = JSON.parse(e.data); const p = m.id && pendientes.get(m.id); if (p) { pendientes.delete(m.id); m.error ? p.mal(new Error(m.error.message)) : p.ok(m.result); } });
  await cdp("Page.enable");
  await cdp("Page.navigate", { url: pathToFileURL(html).href });
  await espera(2500);
  await cdp("Runtime.evaluate", { expression: "document.fonts.ready.then(() => Promise.all([...document.images].map(i => i.decode().catch(() => null))))", awaitPromise: true });
  const pie = `<div style="font-family: Segoe UI, Arial; font-size: 7.5pt; color: #8A8FA3; width: 100%; padding: 0 16mm; display: flex; justify-content: space-between;">
    <span>Manual de instalación de BusinessPost</span><span>Página <span class="pageNumber"></span> de <span class="totalPages"></span></span></div>`;
  const pdf = await cdp("Page.printToPDF", {
    printBackground: true, preferCSSPageSize: true, displayHeaderFooter: true, headerTemplate: "<span></span>", footerTemplate: pie,
  });
  writeFileSync(salida, Buffer.from(pdf.data, "base64"));
  console.log(`PDF: ${salida} (${(statSync(salida).size / 1024 / 1024).toFixed(2)} MB)`);
} catch (error) {
  console.error("No se pudo generar el PDF:", error);
  process.exitCode = 1;
} finally {
  try { ws?.close(); } catch { /* ya cerrado */ }
  navegador.kill();
  await espera(500);
  try { rmSync(temporal, { recursive: true, force: true }); } catch { /* Edge puede tardar en soltar el perfil */ }
}
