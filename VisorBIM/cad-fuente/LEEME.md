# Visor CAD · BECSA (prototipo)

Visor de planos **DWG / DXF** que funciona entero en el navegador. El archivo no se sube a ningún servidor.
Basado en [CAD-Viewer](https://github.com/mlightcad/cad-viewer) (licencia MIT) y LibreDWG (licencia GPL) para leer DWG.

## Subirlo a GitHub Pages (VisorBIM)

1. Copia la carpeta `cad/` del ZIP a la raíz del repo VisorBIM (queda `VisorBIM/cad/index.html`).
2. Añade un enlace en el `index.html` del portal, por ejemplo:
   `<a href="cad/index.html">Visor CAD (DWG / DXF)</a>`
3. Haz commit y push. La flecha ← de la cabecera del visor vuelve al portal (`../index.html`).

No hace falta compilar nada para esto: `cad/` ya está compilado.

> **Importante:** no se puede abrir con doble clic (`file://`). Los workers que leen el DWG necesitan servirse por http(s).
> En GitHub Pages funciona tal cual. En local, usa `run.bat`.

## Modificarlo

- Código: `index.html`, `src/main.js`, `src/style.css`.
- `run.bat` activa el Node portable, instala dependencias la primera vez, compila y abre `http://localhost:4173`.
- El resultado compilado sale en `dist/`. Para publicarlo, copia el contenido de `dist/` a `VisorBIM/cad/`.

## Qué hace

- Abrir DWG/DXF con un botón o arrastrando el archivo a la ventana.
- Barra lateral de la librería: seleccionar, pan, zoom, capas (encender y apagar), presentaciones, medir (distancia, área, ángulo, arco), herramientas de revisión, exportar.
- Línea de comandos al estilo AutoCAD (abajo).
- Tema claro u oscuro, que se recuerda en el navegador.

## Limitaciones conocidas

- Solo planos 2D.
- No muestra XRefs ni las tablas de los DWG.
- Algunos DWG pueden fallar por bugs de LibreDWG. Si pasa, guarda el plano como DXF.
- Los textos usan fuentes que se descargan de jsDelivr (cdn.jsdelivr.net). Si la red las bloquea, los textos se ven con la fuente por defecto o no aparecen.
- Los menús de la barra de la librería están en inglés: la librería todavía no trae traducción al español.
- Peso aproximado: 16 MB. Casi todo es el lector de DWG (`libredwg-web.wasm`, unos 11 MB), que el navegador guarda en caché tras la primera carga.

## Licencias

CAD-Viewer es MIT. LibreDWG es GPL. Para un uso interno, con el código accesible en el repo, no hay problema. Si algún día se quisiera vender o cerrar el código, habría que cambiar el lector de DWG.
