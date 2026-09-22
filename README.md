# Visores BIM

Dos visores web de modelos BIM que funcionan en el navegador, sin instalar nada. Los archivos se leen en local: no se suben a ningún servidor.

Al entrar en la página principal se elige el visor:

| Visor | Carpeta | Archivos |
|---|---|---|
| **Visor IFC** | `ifc/` | IFC2X3, IFC4, IFC4X3 |
| **Visor Revit** | `revit/` | DAE + XLSX exportados con la macro `ExportarVisor` |

## Visor IFC

- Varios IFC a la vez, federados en sus coordenadas reales. Pensado para modelos grandes (cientos de MB): cada IFC se procesa en segundo plano (web-ifc en un Web Worker) sin bloquear la página.
- Árbol BIM (estructura espacial, por plantas o por clase IFC) y búsqueda por nombre, clase o GUID.
- Propiedades: GUID, atributos, Psets, cantidades, tipo y materiales. Copiables a Excel.
- Ocultar (H), aislar (I), transparentar (T), mostrar todo (Mayús+H), encuadrar (F).
- Color y opacidad por modelo, secciones, cotas de distancia y ángulo.
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
- Medición por tipo o por código de montaje, con cantidad, coste unitario e importe.
  Toma el código de montaje, la descripción y el coste del modelo; lo que falte se puede
  añadir a mano (se guarda en el navegador). Exporta a Excel y CSV.
  Las instalaciones (tuberías, conductos, bandejas, tubos y sus accesorios) se desglosan por tamaño,
  y los aislamientos por espesor. Los muros en m² se miden por área bruta o neta, a elegir.

## Estructura

- `index.html`: página de selección de visor
- `ifc/index.html`: visor IFC
- `revit/index.html`: visor Revit (three.js desde CDN; SheetJS se carga en segundo plano)
- `revit/macro/ExportadorVisor.cs`: macro de Revit que exporta DAE + XLSX
- `revit/ejemplo/`: tres modelos ficticios (arquitectura, estructura e instalaciones) con orígenes distintos
- `.nojekyll`: evita que GitHub Pages procese el sitio con Jekyll
