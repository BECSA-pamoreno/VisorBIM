# BECSA Visor BIM

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
- Color y opacidad por modelo y secciones.
- Pestaña **Mediciones** (como BIM Vision):
  - **Volumen** y **superficie total** de los elementos seleccionados, calculados con su geometría al cargar
    (avisa si algún elemento no es un sólido cerrado y su volumen no es fiable).
  - **Área de cara:** clic en caras planas del modelo; se suman, con perímetro y etiqueta sobre cada cara.
  - **Longitud:** entre dos puntos, arista completa (une los tramos alineados), punto a plano y plano a plano (caras paralelas).
  - **Peso** = volumen × densidad: acero 7.850, hormigón en masa 2.300, hormigón armado 2.500, madera 500,
    aluminio 2.700 kg/m³ o una densidad propia (se recuerda en el navegador).
  - **Distancia** y **ángulo** con imán a vértices y aristas. Resultados fijados copiables y lista de cotas.
- **Modo caminar** (botón Caminar): clic en un suelo para empezar y pasear por dentro como en primera persona.
  Muros, forjados, mobiliario y barandillas no se atraviesan (se desliza a lo largo de la pared); puertas, ventanas, huecos y espacios sí.
  El suelo y las escaleras fijan la altura de la vista (ojos a 1,60 m; sube peldaños de hasta 45 cm y cae por los huecos).
  W A S D o flechas para andar, ← → girar, arrastrar para mirar, Mayús correr, doble clic para ir a un punto,
  G para volar sin colisiones, Esc para salir. Un clic selecciona el elemento y muestra sus propiedades (también puertas y ventanas);
  H oculta, I aísla y T transparenta sin salir del paseo, y el clic derecho abre el menú contextual.
- Tema claro u oscuro.

Librerías desde CDN: xeokit-sdk 2.6.114 y web-ifc 0.0.77.

## Visor Revit

1. En Revit, abre una vista 3D de cada modelo y ejecuta la macro `ExportarVisor` (`revit/macro/ExportadorVisor.cs`).
   Se generan `<modelo>.dae` y `<modelo>.xlsx` en `Escritorio\ExportVisor`.
2. Abre el visor y arrastra los archivos. Cada .dae se empareja con el .xlsx del mismo nombre.

**Civil 3D 2027** (`revit/civil3d/`): add-in con el botón *BECSATools > Visor BIM > Exportar visor*
(comando `BECSA_EXPORTARVISOR`). Saca el dibujo con el mismo formato (DAE + XLSX en la carpeta que elijas; recuerda la última, la misma que en Revit):
superficies TIN, obras lineales por capa de subensamblaje (con PK y volumen por prismoides), ejes con sus PK, redes de tuberías y a presión, sólidos, mallas, bloques y el contenido
de las referencias externas, con sus propiedades de Civil 3D, datos de pieza y conjuntos de propiedades. Va en las
coordenadas del dibujo; si el proyecto de Revit adquirió las coordenadas de ese dibujo, los dos modelos caen en su
sitio y se miden juntos. Para instalarlo: doble clic en `build.bat` (descarga el SDK de .NET 10 portable la primera
vez, compila e instala el bundle en `%APPDATA%\Autodesk\ApplicationPlugins`, sin permisos de administrador).

Funciones:

- Varios modelos federados por coordenadas compartidas; se pueden ocultar y quitar para liberar memoria.
- Propiedades por elemento y coordenadas del punto pulsado.
- Búsqueda en parámetros, suma de parámetros numéricos, aislar resultados.
- Colorear por categoría, por modelo o por cualquier parámetro.
- **Planos de corte** (como en el visor IFC): clic en una cara para crear un corte alineado con ella, o "+ Horizontal" a media altura.
  Se pueden crear varios a la vez; cada uno se mueve arrastrando la flecha amarilla en el modelo,
  se activa o desactiva con su casilla, se invierte el lado y se borra. El ojo oculta el plano dibujado sin quitar
  el corte. Cerrar el panel no quita los cortes; el botón muestra cuántos hay activos.
  Con cortes activos las cotas se ajustan al contorno de la sección (esquinas y bordes cortados), como si el corte estuviera relleno.
- Vistas en planta y alzados, proyección ortogonal, rayos X, aristas. Iconos en todos los comandos de la barra.
- Ocultar (H), aislar (I), encuadrar (F), deseleccionar (Esc).
- **Modo caminar** (botón Caminar, también en modo Sencillo): igual que en el visor IFC (clic para ver propiedades, H ocultar, I aislar). Se atraviesan puertas, ventanas,
  habitaciones y espacios; muros, suelos, escaleras, mobiliario, etc. no. Lo que esté oculto o fuera del aislamiento tampoco choca.
- Selección múltiple: Ctrl+clic añade o quita; Ctrl+arrastrar hace ventana (hacia la derecha, elementos enteros dentro;
  hacia la izquierda, los que toque; con Mayús quita). Resumen por tipo con nº de elementos, longitud, área, volumen y peso,
  y botones para aislar, ocultar, medir solo la selección o exportarla a Excel.
- **Medir** (D): ventana propia junto a la de planos de corte (no ocupa la paleta izquierda) con **Distancia** (dos puntos)
  y **Ángulo** (tres puntos, el segundo es el vértice). Imán a vértices y aristas (también a las esquinas y bordes de la sección)
  y **lupa** junto al cursor para afinar. Las distancias muestran total, horizontal y vertical, y en las oblicuas las componentes
  X, Y, Z (se pueden ocultar). Las cotas y ángulos se quedan en pantalla y en la lista de la ventana, donde se ocultan o borran;
  también con clic derecho sobre la cifra o con Supr. Cerrar la ventana no las borra y el botón Medir muestra cuántas hay.
  - **Punto**: clic en el modelo y fija sus coordenadas reales X, Y, Z (las compartidas) y, si hay ejes de Civil 3D,
    su **PK y desplazamiento** al eje más cercano (izquierda / derecha). Se copian para pegarlas en Excel.
  - **Volumen entre superficies** (desmonte y terraplén, excavación): se eligen la superficie inicial (terreno) y la final
    (diseño, rasante, fondo de excavación). Dos métodos:
    - **Prismoides**: exacto, como el "Volumen por prismoides" de InRoads: superpone en planta los triángulos de las dos
      superficies y suma el volumen de cada prisma (en cada trozo común las dos son planas, la integral es exacta).
    - **Secciones por PK** a lo largo de un eje, cada X metros y con un ancho a cada lado: V = d/6·(A1 + 4·Am + A2) con la
      sección intermedia real a mitad de cada tramo; también da el resultado por áreas medias y la tabla de cubicación por PK.
    Factores de esponjamiento (desmonte) y compactación (terraplén). Exporta a Excel.
- **Ejes PK** (modelos de Civil 3D): dibuja los ejes con sus PK rotulados; el intervalo se adapta al zoom.
- Carpeta de proyectos (Chrome / Edge): se elige una vez una carpeta (por ejemplo, la de SharePoint sincronizada con
  OneDrive) con una subcarpeta por proyecto, y el visor la recuerda. "Proyectos" lista las subcarpetas con modelos .dae
  y se abren con un clic (también "Abrir «último proyecto»" en la pantalla de inicio).
  - Se cargan todos los .dae/.xlsx del proyecto (hasta dos niveles de subcarpetas) y el BC3: el que indica la medición,
    si no el más reciente del proyecto y, si no hay, el de la carpeta raíz (un banco común para todos).
  - La medición (partidas, unidades y fórmulas) se guarda en `Medicion_VisorBIM.json` dentro del proyecto y la comparte
    todo el equipo. Si dos personas editan a la vez se fusionan los cambios; el visor comprueba cada 20 s y al volver a la pestaña.
  - Un BC3 abierto a mano dentro de un proyecto se puede copiar a la carpeta para que todos usen el mismo.
- Modo Sencillo / Avanzado (se recuerda en el navegador). Sencillo: navegar, buscar, propiedades, plano de corte, cotas y la
  medición en consulta (tabla, importe, Excel/CSV). Avanzado: además rayos X, abrir BC3, asignar partidas, cambiar unidades, fórmulas, exportar BC3
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

Para planos DWG y DXF está el **BECSA Visor 2D**, en su propio repo (`Visor2D`); la portada enlaza a él.

## Estructura

- `index.html`: página de selección de visor (BECSA Visor BIM)
- `ifc/index.html`: visor IFC
- `revit/index.html`: visor Revit (three.js desde CDN; SheetJS se carga en segundo plano)
- `revit/macro/ExportadorVisor.cs`: macro de Revit que exporta DAE + XLSX
- `revit/civil3d/`: add-in de Civil 3D 2027 que exporta DAE + XLSX con el mismo formato (`build.bat` compila e instala)
- `revit/ejemplo/`: tres modelos ficticios (arquitectura, estructura e instalaciones) con orígenes distintos con «Fase de creación» y «Certificación» (meses 13-15) para probar los filtros, y un BC3 de prueba (`Ejemplo_banco.bc3`)
- `.nojekyll`: evita que GitHub Pages procese el sitio con Jekyll
