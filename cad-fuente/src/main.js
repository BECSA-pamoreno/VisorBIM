// Visor CAD · BECSA
// Visor DWG/DXF 100 % en el navegador, basado en CAD-Viewer (mlightcad, MIT)
// y LibreDWG (GPL) para leer DWG. Nada se sube a ningún servidor.

import {
  AcApDocManager,
  AcEdOpenMode,
  acedApplyUiTheme
} from '@mlightcad/cad-simple-viewer'
import { acuiRegisterSimpleUiPlugin } from '@mlightcad/cad-simple-ui-plugin/register'
import {
  AcDbDatabaseConverterManager,
  AcDbFileType,
  AcDbSysVarManager,
  AcDbSystemVariables
} from '@mlightcad/data-model'
import { AcDbLibreDwgConverter } from '@mlightcad/libredwg-converter'

const WORKERS = {
  dwgParser: './workers/libredwg-parser-worker.js',
  mtextRender: './workers/mtext-renderer-worker.js'
}
// Fuentes SHX/TTF por defecto (se descargan bajo demanda desde jsDelivr)
const FONTS_BASE_URL = 'https://cdn.jsdelivr.net/gh/mlightcad/cad-data@main/'

const $ = id => document.getElementById(id)
const els = {
  stage: $('stage'),
  container: $('cad-container'),
  landing: $('landing'),
  dropOverlay: $('dropOverlay'),
  fileInput: $('fileInput'),
  btnOpen: $('btnOpen'),
  btnOpenLanding: $('btnOpenLanding'),
  btnFit: $('btnFit'),
  btnTheme: $('btnTheme'),
  fileChip: $('fileChip'),
  fileExt: $('fileExt'),
  fileName: $('fileName'),
  toasts: $('toasts')
}

let initPromise = null
let busy = false

/* ---------------- Tema claro / oscuro ---------------- */

function readTheme() {
  try {
    return localStorage.getItem('visorcad-theme') === 'light' ? 'light' : 'dark'
  } catch {
    return 'dark'
  }
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme
  try {
    localStorage.setItem('visorcad-theme', theme)
  } catch {
    /* sin almacenamiento: no pasa nada */
  }
  if (initPromise) syncLibraryTheme(theme)
}

// La barra y los paneles de la librería siguen la variable COLORTHEME (0 oscuro, 1 claro)
function syncLibraryTheme(theme) {
  acedApplyUiTheme(theme, document.documentElement)
  acedApplyUiTheme(theme, els.stage)
  const db = AcApDocManager.tryGetInstance?.()?.curDocument?.database
  if (db) {
    try {
      AcDbSysVarManager.instance().setVar(AcDbSystemVariables.COLORTHEME, theme === 'light' ? 1 : 0, db)
    } catch (err) {
      console.warn('COLORTHEME', err)
    }
  }
}

/* ---------------- Avisos ---------------- */

function toast(msg, kind = '', ms = 4500) {
  const el = document.createElement('div')
  el.className = `toast ${kind}`
  el.textContent = msg
  els.toasts.appendChild(el)
  setTimeout(() => el.remove(), ms)
}

/* ---------------- Inicialización perezosa ---------------- */
// El motor (≈ varios MB + workers) solo se arranca al abrir el primer plano.

function initViewer() {
  if (initPromise) return initPromise
  initPromise = (async () => {
    const theme = readTheme()
    acedApplyUiTheme(theme, document.documentElement)
    acedApplyUiTheme(theme, els.stage)

    // DWG: LibreDWG en un Web Worker (el .wasm va junto al worker)
    AcDbDatabaseConverterManager.instance.register(
      AcDbFileType.DWG,
      new AcDbLibreDwgConverter({
        convertByEntityType: false,
        useWorker: true,
        parserWorkerUrl: WORKERS.dwgParser
      })
    )

    AcApDocManager.createInstance({
      container: els.container,
      busyIndicatorHost: els.stage,
      autoResize: true,
      baseUrl: FONTS_BASE_URL,
      useMainThreadDraw: true,
      webworkerFileUrls: WORKERS,
      openDocumentDefaults: () => openOptions()
    })

    const manager = AcApDocManager.instance
    const ready = await manager.areWorkersReady()
    if (!ready) {
      toast('No se encuentran los workers (carpeta ./workers). ¿Estás abriendo el visor con doble clic? Tiene que servirse por http(s).', 'err', 9000)
    }

    // Barra de herramientas + panel de capas + búsqueda de texto
    await acuiRegisterSimpleUiPlugin(manager.pluginManager, {
      host: els.stage,
      dockPanel: { defaultOpen: false, defaultWidth: 280, defaultHeight: 240 },
      toolbar: { placement: 'right', items: 'default', collapsible: true },
      layouts: {
        phone: {
          toolbar: {
            placement: 'bottom',
            showLabels: true,
            size: 'stretch',
            edgeOffset: 0,
            collapsible: false,
            inCanvasParent: true
          }
        }
      }
    })
    syncLibraryTheme(theme)
  })()
  initPromise.catch(err => {
    console.error(err)
    toast('No se ha podido arrancar el visor: ' + (err?.message || err), 'err', 9000)
    initPromise = null
  })
  return initPromise
}

function openOptions() {
  return {
    minimumChunkSize: 1000,
    mode: AcEdOpenMode.Write,
    sysVars: { lwdisplay: false }
  }
}

/* ---------------- Abrir archivos ---------------- */

async function openFile(file) {
  if (!file || busy) return
  const ext = (file.name.split('.').pop() || '').toLowerCase()
  if (ext !== 'dwg' && ext !== 'dxf') {
    toast('Solo se admiten archivos .dwg o .dxf', 'err')
    return
  }

  busy = true
  setButtonsDisabled(true)
  const t0 = performance.now()
  try {
    await initViewer()
    els.landing.classList.add('hidden')
    const buffer = await file.arrayBuffer()
    const ok = await AcApDocManager.instance.openDocument(file.name, buffer, openOptions())
    if (ok) {
      showFile(file.name, ext)
      const s = ((performance.now() - t0) / 1000).toLocaleString('es-ES', { maximumFractionDigits: 1 })
      const mb = (file.size / 1048576).toLocaleString('es-ES', { maximumFractionDigits: 1 })
      toast(`${file.name} abierto (${mb} MB en ${s} s)`, 'ok')
    } else {
      failOpen(file.name, ext)
    }
  } catch (err) {
    console.error(err)
    failOpen(file.name, ext, err)
  } finally {
    busy = false
    setButtonsDisabled(false)
  }
}

function failOpen(name, ext, err) {
  const hint = ext === 'dwg' ? ' Prueba a guardarlo como DXF desde AutoCAD/Revit.' : ''
  toast(`No se ha podido abrir ${name}.${hint}${err ? ' (' + (err.message || err) + ')' : ''}`, 'err', 9000)
  if (!els.fileChip.hidden) return
  els.landing.classList.remove('hidden')
}

function showFile(name, ext) {
  els.fileExt.textContent = ext.toUpperCase()
  els.fileName.textContent = name
  els.fileName.title = name
  els.fileChip.hidden = false
  document.title = `${name} · Visor CAD`
  els.btnFit.disabled = false
}

function setButtonsDisabled(disabled) {
  els.btnOpen.disabled = disabled
  els.btnOpenLanding.disabled = disabled
  if (disabled) els.btnFit.disabled = true
  else els.btnFit.disabled = els.fileChip.hidden
}

/* ---------------- Eventos de la interfaz ---------------- */

els.btnOpen.addEventListener('click', () => els.fileInput.click())
els.btnOpenLanding.addEventListener('click', () => els.fileInput.click())
els.fileInput.addEventListener('change', () => {
  const file = els.fileInput.files?.[0]
  els.fileInput.value = ''
  openFile(file)
})

els.btnFit.addEventListener('click', () => {
  AcApDocManager.instance?.curView?.zoomToFitDrawing()
})

els.btnTheme.addEventListener('click', () => {
  applyTheme(readTheme() === 'dark' ? 'light' : 'dark')
})

// Arrastrar y soltar en cualquier parte de la ventana
let dragDepth = 0
const hasFiles = e => Array.from(e.dataTransfer?.types || []).includes('Files')
window.addEventListener('dragenter', e => {
  if (!hasFiles(e)) return
  e.preventDefault()
  dragDepth++
  els.dropOverlay.hidden = false
})
window.addEventListener('dragover', e => {
  if (hasFiles(e)) e.preventDefault()
})
window.addEventListener('dragleave', e => {
  if (!hasFiles(e)) return
  dragDepth = Math.max(0, dragDepth - 1)
  if (dragDepth === 0) els.dropOverlay.hidden = true
})
window.addEventListener('drop', e => {
  if (!hasFiles(e)) return
  e.preventDefault()
  dragDepth = 0
  els.dropOverlay.hidden = true
  openFile(e.dataTransfer.files[0])
})

applyTheme(readTheme())
