# Visor BIM (prueba de concepto)

Visor web de modelos exportados desde Revit: geometría en DAE y datos en XLSX, enlazados por ElementId.
Varios modelos a la vez, colocados por coordenadas compartidas.

## Uso

1. En Revit, abre una vista 3D de cada modelo y ejecuta la macro `ExportarVisor` (`macro/ExportadorVisor.cs`).
   Se generan `<modelo>.dae` y `<modelo>.xlsx` en `Escritorio\ExportVisor`.
2. Abre el visor y arrastra los archivos. Cada .dae se empareja con el .xlsx del mismo nombre.

Los archivos se leen en el navegador: no se suben a ningún servidor.

## Funciones

- Varios modelos federados por coordenadas compartidas; se pueden ocultar y quitar para liberar memoria.
- Propiedades por elemento y coordenadas del punto pulsado.
- Búsqueda en parámetros, suma de parámetros numéricos, aislar resultados.
- Colorear por categoría, por modelo o por cualquier parámetro.
- Caja de sección, vistas en planta y alzados, proyección ortogonal, rayos X, aristas.
- Ocultar (H), aislar (I), encuadrar (F), deseleccionar (Esc).
- Medición por tipo o por código de montaje, con cantidad, coste unitario e importe.
  Toma el código de montaje, la descripción y el coste del modelo; lo que falte se puede
  añadir a mano (se guarda en el navegador). Exporta a Excel y CSV.
  Las instalaciones (tuberías, conductos, bandejas, tubos y sus accesorios) se desglosan por tamaño,
  y los aislamientos por espesor. Los muros en m² se miden por área bruta o neta, a elegir.

## Estructura

- `index.html`: el visor (three.js desde CDN; SheetJS se carga en segundo plano)
- `macro/ExportadorVisor.cs`: macro de Revit que exporta DAE + XLSX
- `ejemplo/`: tres modelos ficticios (arquitectura, estructura e instalaciones) con orígenes distintos
