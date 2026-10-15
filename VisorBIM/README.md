# BECSA Visores

Tres visores web (modelos BIM y planos CAD) que funcionan en el navegador, sin instalar nada. Los archivos se leen en local: no se suben a ningún servidor.

Al entrar en la página principal se elige el visor:

| Visor | Carpeta | Archivos |
|---|---|---|
| **Visor IFC** | `ifc/` | IFC2X3, IFC4, IFC4X3 |
| **Visor Revit** | `revit/` | DAE + XLSX exportados con la macro `ExportarVisor` |
| **Visor CAD** | `cad/` | DWG, DXF (planos 2D) |

## Visor IFC

- Varios IFC a la vez, federados en sus coordenadas reales. Pensado para modelos grandes (cientos de MB): cada IFC se procesa en segundo plano (web-ifc en un Web Worker) sin bloquear la página.
- Árbol BIM (estructura espacial, por plantas o por clase IFC) y búsqueda por nombre, clase o GUID.
- Propiedades: GUID, atributos, Psets, cantidades, tipo y materiales. Copiables a Excel.
- Ocultar (H), aislar (I), transparentar (T), mostrar todo (Mayús+H), encuadrar (F).
- Color y opacidad por modelo y secciones.
- Pestaña **Mediciones** (como BIM Vision):
  - **Volumen** y **superficie total** de los elementos seleccionados, calculados con su geometría al cargar
    (avisa si algún elemento no es un sólido cerrado y su volumen no es fiable).
  - **Área de cara:** clic en caras planas del modelo; se suman, con perímetro y etiqueta sobre cada cara.
  - **Longitud:** entre dos puntos, arista completa (une los tramos alineados), punto a plano y plano a plano (caras paralelas).
  - **Peso** = volumen × densidad: acero 7.850, hormigón en masa 2.300, hormigón armado 2.500, madera 500,
    aluminio 2.700 kg/m³ o una densidad propia (se recuerda en el navegador).
  - **Distancia** y **ángulo** con imán a vértices y aristas. Resultados fijados copiables y lista de cotas.
- Tema claro u oscuro.

Librerías desde CDN: xeokit-sdk 2.6.114 y web-ifc 0.0.77.

## Visor Revit

1. En Revit, abre una vista 3D de cada modelo y ejecuta la macro `ExportarVisor` (`revit/macro/ExportadorVisor.cs`).
   Se generan `<modelo>.dae` y `<modelo>.xlsx` en `Escritorio\ExportVisor`.
2. Abre el visor y arrastra los archivos. Cada .dae se empareja con el .xlsx del mismo nombre.

Funciones:

- Varios modelos federados por coordenadas compartidas; se pueden ocultar y quitar para liberar memoria.
- Propiedades por elemento y coordenadas del punto pulsado.
- Búsqueda en parámetros, suma de parámetros numéricos, aislar resultados.
- Colorear por categoría, por modelo o por cualquier parámetro.
- Caja de sección y plano de corte alineado a una cara del modelo, vistas en planta y alzados, proyección ortogonal, rayos X, aristas.
- Ocultar (H), aislar (I), encuadrar (F), deseleccionar (Esc).
- Selección múltiple: Ctrl+clic añade o quita; Ctrl+arrastrar hace ventana (hacia la derecha, elementos enteros dentro;
  hacia la izquierda, los que toque; con Mayús quita). Resumen por tipo con nº de elementos, longitud, área, volumen y peso,
  y botones para aislar, ocultar, medir solo la selección o exportarla a Excel.
- Cotas de distancia entre dos puntos (D), con imán a vértices y aristas; muestran distancia total, horizontal y vertical,
  y en las cotas oblicuas dibujan las componentes X, Y, Z (se pueden ocultar).
  Se borran con clic derecho sobre la cifra, con Supr o desde el panel Cotas.
- Carpeta de proyectos (Chrome / Edge): se elige una vez una carpeta (por ejemplo, la de SharePoint sincronizada con
  OneDrive) con una subcarpeta por proyecto, y el visor la recuerda. "Proyectos" lista las subcarpetas con modelos .dae
  y se abren con un clic (también "Abrir «último proyecto»" en la pantalla de inicio).
  - Se cargan todos los .dae/.xlsx del proyecto (hasta dos niveles de subcarpetas) y el BC3: el que indica la medición,
    si no el más reciente del proyecto y, si no hay, el de la carpeta raíz (un banco común para todos).
  - La medición (partidas, unidades y fórmulas) se guarda en `Medicion_VisorBIM.json` dentro del proyecto y la comparte
    todo el equipo. Si dos personas editan a la vez se fusionan los cambios; el visor comprueba cada 20 s y al volver a la pestaña.
  - Un BC3 abierto a mano dentro de un proyecto se puede copiar a la carpeta para que todos usen el mismo.
- Modo Sencillo / Avanzado (se recuerda en el navegador). Sencillo: navegar, buscar, propiedades, plano de corte, cotas y la
  medición en consulta (tabla, importe, Excel/CSV). Avanzado: además sección, rayos X, abrir BC3, asignar partidas, cambiar unidades, fórmulas, exportar BC3
  y compartir datos.
- Medición con la misma lógica que la macro BECSA BoQ v2.9, pero sin escribir nada en Revit:
  - Una fila por modelo, categoría, familia, tipo y tamaño (instalaciones por tamaño, aislamientos por espesor).
  - Abrir un banco de precios BC3 (FIEBDC-3, ANSI / 850 / 437 / UTF-8). Si el modelo ya trae
    código de montaje, descripción y coste, se enlazan con la partida del BC3; lo que falte se completa
    desde el BC3 y lo que difiera se marca en azul ("Actualizar desde BC3" trae los valores del banco).
    Los códigos Uniformat de las familias (B2010, D5020...) se ignoran si no existen en el BC3.
  - Asignar partidas desde el navegador del BC3 (capítulos, búsqueda en todo el banco o en un capítulo,
    "solo unidades medibles", texto y descompuesto), escribiendo el código o con autocompletar.
    Varias filas a la vez con Ctrl / Mayús.
  - Cambiar la unidad de medida de cada fila (ud, m, m², m² bruta, m³, kg, t) y crear mediciones
    con fórmula a partir de cualquier parámetro numérico: `[Longitud (m)] * [Altura desconectada]`,
    `[Volumen (m³)] * 7850`, `round([Área] * 1,05; 2)`... aplicable a un tipo o a toda la categoría.
  - Estado por fila: correcta, incompleta, propia / modificada, sin partida y "cambiada en el visor".
  - Filtros de elementos (modo Avanzado), como el BoQ v3.0 del add-in: desplegable de **fase de creación** y
    **filtro por parámetro** con hasta 4 reglas (Y / O): es igual, distinto, contiene, empieza por, mayor, menor, tiene valor
    o no tiene valor, con los valores que existen en los modelos y vista previa de cuántos elementos cumplen. Se aplican a
    cada elemento antes de agrupar, así medición, importe y exportaciones salen solo con lo que cumple
    (p. ej. «Certificación» = 15 → producción del mes 15; ≤ 15 → a origen). El filtro aparece en el título del BC3 y del Excel.
  - Importe total destacado arriba. Ancho de columnas ajustable como en Excel
    (arrastrar el borde de la cabecera, doble clic para ajustar al contenido; se recuerda en el navegador).
    La ventana del BC3 ocupa casi toda la pantalla y sus tres paneles se pueden ensanchar arrastrando los separadores.
  - Exporta a BC3 con mediciones (capítulos del banco o por categoría, con descompuestos), Excel y CSV.
  - Lo hecho en el visor se guarda en este navegador (y el último BC3 usado); "Datos" exporta e importa
    un .json para pasárselo a otra persona.

## Visor CAD

- Abre planos DWG y DXF en 2D (modelo y presentaciones), con un botón o arrastrando el archivo.
- Capas (encender y apagar), zoom y desplazamiento, medir distancia, área, ángulo y arco, herramientas de revisión y exportar.
- Línea de comandos al estilo AutoCAD y tema claro u oscuro.
- Basado en [CAD-Viewer](https://github.com/mlightcad/cad-viewer) (MIT) y LibreDWG (GPL) para leer DWG.
- `cad/` ya está compilado; el código fuente y cómo recompilarlo están en `cad-fuente/` (ver `cad-fuente/LEEME.md`).
- No se abre con doble clic (`file://`): necesita servirse por http(s). En GitHub Pages funciona tal cual.
- Limitaciones: no muestra XRefs ni tablas de DWG; si un DWG falla, guardarlo como DXF. Los textos usan fuentes de jsDelivr.

## Estructura

- `index.html`: página de selección de visor (BECSA Visores)
- `ifc/index.html`: visor IFC
- `revit/index.html`: visor Revit (three.js desde CDN; SheetJS se carga en segundo plano)
- `revit/macro/ExportadorVisor.cs`: macro de Revit que exporta DAE + XLSX
- `cad/`: visor CAD compilado; `cad-fuente/`: su código fuente
- `revit/ejemplo/`: tres modelos ficticios (arquitectura, estructura e instalaciones) con orígenes distintos con «Fase de creación» y «Certificación» (meses 13-15) para probar los filtros, y un BC3 de prueba (`Ejemplo_banco.bc3`)
- `.nojekyll`: evita que GitHub Pages procese el sitio con Jekyll
