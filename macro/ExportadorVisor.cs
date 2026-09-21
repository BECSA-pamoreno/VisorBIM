/*
 * ExportadorVisor — macro de Revit (módulo de aplicación, ThisApplication)
 *
 * Exporta lo que se ve en la vista 3D activa a dos archivos en el Escritorio\ExportVisor:
 *   - <modelo>.dae  : geometría triangulada (Collada), un nodo por elemento con name = ElementId
 *   - <modelo>.xlsx : una fila por elemento con ElementId, UniqueId, categoría, familia, tipo,
 *                     nivel y todos sus parámetros de ejemplar y de tipo (prefijo "Tipo: ")
 *
 * El visor (visor.html) enlaza ambos archivos por la columna ElementId.
 *
 * Instalación:
 *   1. Gestionar > Administrador de macros > pestaña Aplicación > Crear módulo (C#).
 *   2. Pega los "using" arriba del archivo y el resto dentro de la clase ThisApplication.
 *   3. Revit 2024 o anterior: añade la referencia System.IO.Compression
 *      (Proyecto > Añadir referencia). En Revit 2025+ no hace falta.
 *   4. Compila, abre una vista 3D, oculta lo que no quieras exportar y ejecuta ExportarVisor.
 *
 * Unidades: la geometría se escribe en metros. Requiere Revit 2023 o posterior.
 * Medición: las columnas "Longitud (m)", "Área (m²)" y "Volumen (m³)" salen del valor interno
 * de Revit, así que son exactas aunque el proyecto use mm u otras unidades.
 *
 * Coordenadas: con USAR_COORDENADAS_COMPARTIDAS = true la geometría sale en coordenadas
 * compartidas, para que varios modelos (arquitectura, estructura, instalaciones...) caigan
 * en su sitio en el visor. Los modelos deben tener las coordenadas compartidas bien
 * definidas (Adquirir / Publicar coordenadas). Para no perder precisión con coordenadas
 * UTM grandes, los vértices se escriben relativos a un origen redondeado que se guarda
 * en el DAE como <subject>ORIGEN x y z SISTEMA ...</subject>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

// ---------- Pegar desde aquí dentro de la clase ThisApplication ----------

    private const double PIES_A_METROS = 0.3048;
    private static readonly bool USAR_COORDENADAS_COMPARTIDAS = true; // static readonly, no const: evita el aviso CS0429

    // Columnas de medición: valor interno de Revit convertido a m, m² y m³ (no dependen de las unidades del proyecto)
    private const string COL_LONG = "Longitud (m)";
    private const string COL_AREA = "Área (m²)";
    private const string COL_VOL = "Volumen (m³)";
    private const string COL_AREA_BRUTA = "Área bruta (m²)";   // muros: longitud x altura, sin descontar huecos
    private const string COL_TAM_MEP = "Tamaño MEP";            // tamaño calculado, o espesor en aislamientos
    private const string COL_W = "W (kg/m)";                    // peso lineal del perfil (parámetro W)
    private const string COL_KG = "Peso (kg)";                  // W x longitud (longitud de corte en vigas)

    // Instalaciones que se miden por tipo + tamaño (mismo criterio que el add-in de Woodea)
    private static readonly HashSet<BuiltInCategory> CATEGORIAS_MEP = new HashSet<BuiltInCategory>
    {
        BuiltInCategory.OST_Conduit, BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_PipeInsulations, BuiltInCategory.OST_DuctInsulations
    };

    // Transformación interna -> compartida y origen (en metros) del archivo que se está exportando
    private Transform _transformacion = Transform.Identity;
    private XYZ _origen = XYZ.Zero;
    private static readonly CultureInfo INV = CultureInfo.InvariantCulture;

    public void ExportarVisor()
    {
        UIDocument uidoc = this.ActiveUIDocument;
        if (uidoc == null) { TaskDialog.Show("Exportar visor", "No hay ningún documento abierto."); return; }
        Document doc = uidoc.Document;

        View3D vista = doc.ActiveView as View3D;
        if (vista == null || vista.IsTemplate)
        {
            TaskDialog.Show("Exportar visor", "Abre una vista 3D antes de exportar. Se exporta lo que se ve en esa vista.");
            return;
        }

        Stopwatch reloj = Stopwatch.StartNew();

        _transformacion = USAR_COORDENADAS_COMPARTIDAS
            ? doc.ActiveProjectLocation.GetTotalTransform().Inverse
            : Transform.Identity;
        XYZ o = _transformacion.OfPoint(XYZ.Zero) * PIES_A_METROS;
        _origen = new XYZ(Math.Round(o.X), Math.Round(o.Y), Math.Round(o.Z));

        string carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ExportVisor");
        Directory.CreateDirectory(carpeta);
        string nombreBase = NombreArchivo(doc.Title);
        string rutaDae = Path.Combine(carpeta, nombreBase + ".dae");
        string rutaXlsx = Path.Combine(carpeta, nombreBase + ".xlsx");

        // Al fijar la vista, Revit usa su nivel de detalle y su visibilidad
        Options opciones = new Options();
        opciones.View = vista;
        opciones.ComputeReferences = false;
        opciones.IncludeNonVisibleObjects = false;

        IList<Element> elementos = new FilteredElementCollector(doc, vista.Id)
            .WhereElementIsNotElementType()
            .ToElements();

        List<Dictionary<string, string>> filas = new List<Dictionary<string, string>>();
        HashSet<string> columnasExtra = new HashSet<string>();
        int triangulosTotales = 0;

        using (StreamWriter dae = new StreamWriter(rutaDae, false, new UTF8Encoding(false)))
        {
            EscribirCabeceraDae(dae);
            dae.WriteLine("  <library_geometries>");

            List<string> ids = new List<string>();
            foreach (Element el in elementos)
            {
                Category cat = el.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                // Tramos, descansillos y zancas se exportan dentro de su escalera (una sola fila)
                if (EsParteDeEscalera(el)) continue;

                GeometryElement geo = null;
                try { geo = el.get_Geometry(opciones); } catch { geo = null; }
                Stairs escalera = el as Stairs;
                if (geo == null && escalera == null) continue;

                List<XYZ> vertices = new List<XYZ>();
                List<int> triangulos = new List<int>();
                if (geo != null) RecogerGeometria(geo, vertices, triangulos);
                // Si la escalera no trae geometría propia, se toma la de sus tramos, descansillos y zancas
                if (escalera != null && triangulos.Count == 0)
                {
                    foreach (ElementId pid in PartesEscalera(escalera))
                    {
                        Element parte = doc.GetElement(pid);
                        GeometryElement gp = null;
                        try { gp = parte != null ? parte.get_Geometry(opciones) : null; } catch { gp = null; }
                        if (gp != null) RecogerGeometria(gp, vertices, triangulos);
                    }
                }
                if (triangulos.Count == 0) continue;

                string id = el.Id.ToString();
                EscribirGeometria(dae, id, vertices, triangulos);
                ids.Add(id);
                triangulosTotales += triangulos.Count / 3;

                filas.Add(DatosElemento(doc, el, columnasExtra));
            }

            dae.WriteLine("  </library_geometries>");
            EscribirEscena(dae, ids);
        }

        List<string> fijas = new List<string> { "ElementId", "UniqueId", "Categoría", "Familia", "Tipo", "Nivel", COL_TAM_MEP, COL_LONG, COL_AREA, COL_AREA_BRUTA, COL_VOL, COL_W, COL_KG };
        List<string> columnas = new List<string>(fijas);
        columnas.AddRange(columnasExtra.Where(c => !fijas.Contains(c)).OrderBy(c => c.StartsWith("Tipo: ") ? 1 : 0).ThenBy(c => c, StringComparer.CurrentCultureIgnoreCase));
        EscribirXlsx(rutaXlsx, columnas, filas);

        reloj.Stop();
        TaskDialog.Show("Exportar visor",
            "Coordenadas " + (USAR_COORDENADAS_COMPARTIDAS ? "compartidas" : "internas") + ", origen " +
            _origen.X.ToString("0", INV) + ", " + _origen.Y.ToString("0", INV) + ", " + _origen.Z.ToString("0", INV) + " m.\n" +
            "Exportados " + filas.Count + " elementos (" + triangulosTotales.ToString("N0") + " triángulos) en " +
            reloj.Elapsed.TotalSeconds.ToString("0.0") + " s.\n\n" + rutaDae + "\n" + rutaXlsx);
    }

    // ---------- Geometría ----------

    private void RecogerGeometria(GeometryElement geo, List<XYZ> vertices, List<int> triangulos)
    {
        foreach (GeometryObject obj in geo)
        {
            Solid solido = obj as Solid;
            if (solido != null)
            {
                if (solido.Faces.Size == 0) continue;
                foreach (Face cara in solido.Faces)
                {
                    Mesh malla = null;
                    try { malla = cara.Triangulate(); } catch { malla = null; }
                    if (malla != null) AnadirMalla(malla, vertices, triangulos);
                }
                continue;
            }

            Mesh mallaSuelta = obj as Mesh;
            if (mallaSuelta != null) { AnadirMalla(mallaSuelta, vertices, triangulos); continue; }

            // Familias: la geometría de ejemplar ya viene en coordenadas del modelo
            GeometryInstance instancia = obj as GeometryInstance;
            if (instancia != null)
            {
                GeometryElement geoInst = instancia.GetInstanceGeometry();
                if (geoInst != null) RecogerGeometria(geoInst, vertices, triangulos);
            }
        }
    }

    private void AnadirMalla(Mesh malla, List<XYZ> vertices, List<int> triangulos)
    {
        int desplazamiento = vertices.Count;
        foreach (XYZ v in malla.Vertices) vertices.Add(v);
        for (int i = 0; i < malla.NumTriangles; i++)
        {
            MeshTriangle t = malla.get_Triangle(i);
            triangulos.Add(desplazamiento + (int)t.get_Index(0));
            triangulos.Add(desplazamiento + (int)t.get_Index(1));
            triangulos.Add(desplazamiento + (int)t.get_Index(2));
        }
    }

    private void EscribirCabeceraDae(StreamWriter w)
    {
        w.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        w.WriteLine("<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">");
        w.WriteLine("  <asset>");
        w.WriteLine("    <contributor><authoring_tool>ExportadorVisor (Revit)</authoring_tool></contributor>");
        string ahora = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", INV);
        w.WriteLine("    <created>" + ahora + "</created><modified>" + ahora + "</modified>");
        w.WriteLine("    <subject>ORIGEN " + _origen.X.ToString("0.###", INV) + " " + _origen.Y.ToString("0.###", INV) + " " +
            _origen.Z.ToString("0.###", INV) + " SISTEMA " + (USAR_COORDENADAS_COMPARTIDAS ? "compartidas" : "internas") + "</subject>");
        w.WriteLine("    <unit name=\"meter\" meter=\"1\"/>");
        w.WriteLine("    <up_axis>Z_UP</up_axis>");
        w.WriteLine("  </asset>");
        w.WriteLine("  <library_effects><effect id=\"fx\"><profile_COMMON><technique sid=\"common\"><lambert><diffuse><color>0.8 0.8 0.8 1</color></diffuse></lambert></technique></profile_COMMON></effect></library_effects>");
        w.WriteLine("  <library_materials><material id=\"mat\" name=\"mat\"><instance_effect url=\"#fx\"/></material></library_materials>");
    }

    private void EscribirGeometria(StreamWriter w, string id, List<XYZ> vertices, List<int> triangulos)
    {
        string g = "g" + id;
        w.WriteLine("    <geometry id=\"" + g + "\" name=\"" + id + "\"><mesh>");
        w.Write("      <source id=\"" + g + "-p\"><float_array id=\"" + g + "-pa\" count=\"" + (vertices.Count * 3) + "\">");
        StringBuilder sb = new StringBuilder(vertices.Count * 24);
        for (int i = 0; i < vertices.Count; i++)
        {
            XYZ v = _transformacion.OfPoint(vertices[i]) * PIES_A_METROS - _origen;
            if (i > 0) sb.Append(' ');
            sb.Append(v.X.ToString("0.####", INV)).Append(' ')
              .Append(v.Y.ToString("0.####", INV)).Append(' ')
              .Append(v.Z.ToString("0.####", INV));
        }
        w.Write(sb.ToString());
        w.WriteLine("</float_array><technique_common><accessor source=\"#" + g + "-pa\" count=\"" + vertices.Count + "\" stride=\"3\"><param name=\"X\" type=\"float\"/><param name=\"Y\" type=\"float\"/><param name=\"Z\" type=\"float\"/></accessor></technique_common></source>");
        w.WriteLine("      <vertices id=\"" + g + "-v\"><input semantic=\"POSITION\" source=\"#" + g + "-p\"/></vertices>");
        w.Write("      <triangles material=\"mat\" count=\"" + (triangulos.Count / 3) + "\"><input semantic=\"VERTEX\" source=\"#" + g + "-v\" offset=\"0\"/><p>");
        w.Write(string.Join(" ", triangulos));
        w.WriteLine("</p></triangles>");
        w.WriteLine("    </mesh></geometry>");
    }

    private void EscribirEscena(StreamWriter w, List<string> ids)
    {
        w.WriteLine("  <library_visual_scenes><visual_scene id=\"Scene\" name=\"Scene\">");
        foreach (string id in ids)
        {
            w.WriteLine("    <node id=\"n" + id + "\" name=\"" + id + "\"><instance_geometry url=\"#g" + id + "\"><bind_material><technique_common><instance_material symbol=\"mat\" target=\"#mat\"/></technique_common></bind_material></instance_geometry></node>");
        }
        w.WriteLine("  </visual_scene></library_visual_scenes>");
        w.WriteLine("  <scene><instance_visual_scene url=\"#Scene\"/></scene>");
        w.WriteLine("</COLLADA>");
    }

    // ---------- Datos ----------

    private Dictionary<string, string> DatosElemento(Document doc, Element el, HashSet<string> columnasExtra)
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        d["ElementId"] = el.Id.ToString();
        d["UniqueId"] = el.UniqueId;
        d["Categoría"] = el.Category != null ? el.Category.Name : "";

        ElementType tipo = doc.GetElement(el.GetTypeId()) as ElementType;
        d["Familia"] = tipo != null ? tipo.FamilyName : "";
        d["Tipo"] = tipo != null ? tipo.Name : el.Name;

        string nivel = "";
        if (el.LevelId != ElementId.InvalidElementId)
        {
            Element lv = doc.GetElement(el.LevelId);
            if (lv != null) nivel = lv.Name;
        }
        d["Nivel"] = nivel;

        AnadirMediciones(el, d);
        AnadirParametros(el, "", d, columnasExtra);
        if (tipo != null) AnadirParametros(tipo, "Tipo: ", d, columnasExtra);
        return d;
    }

    private void AnadirMediciones(Element el, Dictionary<string, string> d)
    {
        BuiltInCategory bic = el.Category != null ? el.Category.BuiltInCategory : BuiltInCategory.INVALID;
        double v;

        Stairs escalera = el as Stairs;
        if (escalera != null) { MedicionEscalera(escalera, d); return; }

        // Tamaño para instalaciones: espesor en aislamientos, tamaño calculado en el resto
        if (CATEGORIAS_MEP.Contains(bic))
        {
            BuiltInParameter bipTam = bic == BuiltInCategory.OST_PipeInsulations ? BuiltInParameter.RBS_INSULATION_THICKNESS_FOR_PIPE
                : bic == BuiltInCategory.OST_DuctInsulations ? BuiltInParameter.RBS_INSULATION_THICKNESS_FOR_DUCT
                : BuiltInParameter.RBS_CALCULATED_SIZE;
            Parameter pt = null;
            try { pt = el.get_Parameter(bipTam); } catch { pt = null; }
            if (pt != null && pt.HasValue)
            {
                string tam = pt.AsValueString();
                if (!string.IsNullOrEmpty(tam)) d[COL_TAM_MEP] = tam;
            }
        }

        // Longitud (ml)
        bool sinLongitud = bic == BuiltInCategory.OST_Floors || bic == BuiltInCategory.OST_Roofs || bic == BuiltInCategory.OST_Ceilings;
        if (!sinLongitud)
        {
            double lon = 0;
            if (bic == BuiltInCategory.OST_Walls)
            {
                LocationCurve lc = el.Location as LocationCurve;
                if (lc != null && lc.Curve != null) lon = lc.Curve.Length;
            }
            else if (bic == BuiltInCategory.OST_CurtainWallPanels)
            {
                Parameter pw = el.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH);
                if (pw != null && pw.HasValue) lon = pw.AsDouble();
            }
            else if (Medida(el, BuiltInParameter.CURVE_ELEM_LENGTH, SpecTypeId.Length, new string[] { "Longitud", "Length" }, out v)) lon = v;
            if (lon > 0) d[COL_LONG] = M(lon, UnitTypeId.Meters);
        }

        // Área (m²): en muros se guardan neta y bruta; el visor usa la que elija el usuario
        double area = 0;
        if (bic == BuiltInCategory.OST_Walls)
        {
            Parameter pa = el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
            if (pa != null && pa.HasValue) area = pa.AsDouble();
            Parameter ph = el.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            Parameter pl = el.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            if (ph != null && pl != null && ph.HasValue && pl.HasValue && ph.AsDouble() * pl.AsDouble() > 0)
                d[COL_AREA_BRUTA] = M(ph.AsDouble() * pl.AsDouble(), UnitTypeId.SquareMeters);
        }
        else if (bic == BuiltInCategory.OST_Windows)
        {
            FamilyInstance fi = el as FamilyInstance;
            if (fi != null && fi.Symbol != null)
            {
                Parameter pw = fi.Symbol.get_Parameter(BuiltInParameter.WINDOW_WIDTH);
                Parameter ph = fi.Symbol.get_Parameter(BuiltInParameter.WINDOW_HEIGHT);
                if (pw != null && ph != null && pw.HasValue && ph.HasValue) area = pw.AsDouble() * ph.AsDouble();
            }
        }
        else if (Medida(el, BuiltInParameter.HOST_AREA_COMPUTED, SpecTypeId.Area, new string[] { "Área", "Area" }, out v)) area = v;
        if (area > 0) d[COL_AREA] = M(area, UnitTypeId.SquareMeters);

        // Volumen (m³)
        if (Medida(el, BuiltInParameter.HOST_VOLUME_COMPUTED, SpecTypeId.Volume, new string[] { "Volumen", "Volume" }, out v))
            d[COL_VOL] = M(v, UnitTypeId.CubicMeters);

        // Peso (kg) = W x longitud, pensado para vigas y pilares de acero
        double w;
        if (PesoLineal(el, out w))
        {
            d[COL_W] = w.ToString("0.####", INV);
            double lm = LongitudParaPeso(el, bic) * PIES_A_METROS;
            if (lm > 0) d[COL_KG] = (w * lm).ToString("0.###", INV);
        }
    }

    // Lee W (de ejemplar o de tipo) y lo devuelve en kg/m
    private bool PesoLineal(Element el, out double kgPorMetro)
    {
        kgPorMetro = 0;
        Parameter p = el.LookupParameter("W");
        if (p == null || !p.HasValue)
        {
            Element tipo = el.Document.GetElement(el.GetTypeId());
            if (tipo != null) p = tipo.LookupParameter("W");
        }
        if (p == null || !p.HasValue) return false;

        if (p.StorageType == StorageType.Double)
        {
            string spec = "";
            try { ForgeTypeId dt = p.Definition.GetDataType(); spec = dt != null ? dt.TypeId : ""; } catch { spec = ""; }
            double bruto = p.AsDouble();
            if (spec.Contains("massPerUnitLength")) kgPorMetro = bruto / PIES_A_METROS;          // interno kg/pie
            else if (spec.Contains("weightPerUnitLength")) kgPorMetro = bruto / 9.80665;          // interno N/m -> kg/m
            else if (spec == "" || spec.Contains("spec:number")) kgPorMetro = bruto;              // número: ya en kg/m
            else kgPorMetro = NumeroDeTexto(p.AsValueString());
        }
        else if (p.StorageType == StorageType.String) kgPorMetro = NumeroDeTexto(p.AsString());
        else if (p.StorageType == StorageType.Integer) kgPorMetro = p.AsInteger();
        return kgPorMetro > 0;
    }

    // Longitud en unidades internas: de corte en vigas, longitud del pilar en pilares
    private double LongitudParaPeso(Element el, BuiltInCategory bic)
    {
        Parameter p;
        if (bic == BuiltInCategory.OST_StructuralFraming)
        {
            p = el.get_Parameter(BuiltInParameter.STRUCTURAL_FRAME_CUT_LENGTH);
            if (p != null && p.HasValue && p.AsDouble() > 0) return p.AsDouble();
        }
        if (bic == BuiltInCategory.OST_StructuralColumns)
        {
            p = el.get_Parameter(BuiltInParameter.INSTANCE_LENGTH_PARAM);
            if (p != null && p.HasValue && p.AsDouble() > 0) return p.AsDouble();
        }
        p = el.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
        if (p != null && p.HasValue && p.AsDouble() > 0) return p.AsDouble();
        LocationCurve lc = el.Location as LocationCurve;
        if (lc != null && lc.Curve != null) return lc.Curve.Length;
        return 0;
    }

    private double NumeroDeTexto(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return 0;
        StringBuilder sb = new StringBuilder();
        foreach (char c in texto.Trim())
        {
            if (char.IsDigit(c) || c == ',' || c == '.' || c == '-') sb.Append(c); else break;
        }
        double r;
        return double.TryParse(sb.ToString().Replace(',', '.'), NumberStyles.Float, INV, out r) ? r : 0;
    }

    // ---------- Escaleras ----------
    // m³: volumen real de los sólidos (escalera, tramos, descansillos y zancas)
    // m²: proyección horizontal de tramos + descansillos
    // ml: nº de contrahuellas x ancho de tramo (ml de peldaño)
    private void MedicionEscalera(Stairs escalera, Dictionary<string, string> d)
    {
        Document doc = escalera.Document;
        Options op = new Options();
        op.DetailLevel = ViewDetailLevel.Fine;
        op.ComputeReferences = false;

        double volumen = VolumenSolidos(escalera.get_Geometry(op));
        if (volumen <= 0)
        {
            foreach (ElementId pid in PartesEscalera(escalera))
            {
                Element parte = doc.GetElement(pid);
                if (parte != null) volumen += VolumenSolidos(parte.get_Geometry(op));
            }
        }
        if (volumen > 0) d[COL_VOL] = M(volumen, UnitTypeId.CubicMeters);

        double area = 0, mlPeldano = 0;
        foreach (ElementId id in escalera.GetStairsRuns())
        {
            StairsRun tramo = doc.GetElement(id) as StairsRun;
            if (tramo == null) continue;
            try { area += AreaPlanta(tramo.GetFootprintBoundary()); } catch { }
            mlPeldano += tramo.ActualRisersNumber * tramo.ActualRunWidth;
        }
        foreach (ElementId id in escalera.GetStairsLandings())
        {
            StairsLanding descansillo = doc.GetElement(id) as StairsLanding;
            if (descansillo == null) continue;
            try { area += AreaPlanta(descansillo.GetFootprintBoundary()); } catch { }
        }
        if (area > 0) d[COL_AREA] = M(area, UnitTypeId.SquareMeters);
        if (mlPeldano > 0) d[COL_LONG] = M(mlPeldano, UnitTypeId.Meters);
    }

    private List<ElementId> PartesEscalera(Stairs escalera)
    {
        List<ElementId> ids = new List<ElementId>();
        try { ids.AddRange(escalera.GetStairsRuns()); } catch { }
        try { ids.AddRange(escalera.GetStairsLandings()); } catch { }
        try { ids.AddRange(escalera.GetStairsSupports()); } catch { }
        return ids;
    }

    private bool EsParteDeEscalera(Element el)
    {
        if (el is StairsRun || el is StairsLanding) return true;
        return el.Category != null && el.Category.BuiltInCategory == BuiltInCategory.OST_StairsStringerCarriage;
    }

    private double VolumenSolidos(GeometryElement geo)
    {
        double total = 0;
        if (geo == null) return 0;
        foreach (GeometryObject obj in geo)
        {
            Solid solido = obj as Solid;
            if (solido != null) { try { if (solido.Volume > 0) total += solido.Volume; } catch { } continue; }
            GeometryInstance inst = obj as GeometryInstance;
            if (inst != null) total += VolumenSolidos(inst.GetInstanceGeometry());
        }
        return total;
    }

    // Área en planta de un contorno cerrado (fórmula del polígono sobre la curva teselada)
    private double AreaPlanta(CurveLoop contorno)
    {
        List<XYZ> puntos = new List<XYZ>();
        foreach (Curve c in contorno)
        {
            IList<XYZ> pts = c.Tessellate();
            for (int i = 0; i < pts.Count - 1; i++) puntos.Add(pts[i]);   // el último punto es el primero de la siguiente curva
        }
        double a = 0;
        for (int i = 0; i < puntos.Count; i++)
        {
            XYZ p1 = puntos[i], p2 = puntos[(i + 1) % puntos.Count];
            a += p1.X * p2.Y - p2.X * p1.Y;
        }
        return Math.Abs(a) / 2.0;
    }

    private string M(double valorInterno, ForgeTypeId unidad)
    {
        return UnitUtils.ConvertFromInternalUnits(valorInterno, unidad).ToString("0.####", INV);
    }

    // Primero el parámetro de sistema; si no existe, uno con ese nombre y ese tipo de dato (conductos, aislamientos...)
    private bool Medida(Element el, BuiltInParameter bip, ForgeTypeId tipoDato, string[] nombres, out double valor)
    {
        valor = 0;
        Parameter p = null;
        try { p = el.get_Parameter(bip); } catch { p = null; }
        if (p != null && p.HasValue && p.StorageType == StorageType.Double)
        {
            valor = p.AsDouble();
            if (valor > 0) return true;
        }
        foreach (Parameter q in el.Parameters)
        {
            if (q == null || !q.HasValue || q.StorageType != StorageType.Double || q.Definition == null) continue;
            if (Array.IndexOf(nombres, q.Definition.Name) < 0) continue;
            ForgeTypeId dt = null;
            try { dt = q.Definition.GetDataType(); } catch { dt = null; }
            if (dt == null || !dt.Equals(tipoDato)) continue;
            valor = q.AsDouble();
            if (valor > 0) return true;
        }
        return false;
    }

    private void AnadirParametros(Element el, string prefijo, Dictionary<string, string> d, HashSet<string> columnasExtra)
    {
        foreach (Parameter p in el.Parameters)
        {
            if (p == null || p.Definition == null || !p.HasValue) continue;
            string valor = ValorParametro(p);
            if (string.IsNullOrEmpty(valor)) continue;
            string nombre = prefijo + p.Definition.Name;
            if (d.ContainsKey(nombre)) continue; // nombres duplicados: se queda el primero
            d[nombre] = valor;
            columnasExtra.Add(nombre);
        }
    }

    private string ValorParametro(Parameter p)
    {
        try
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString();
                case StorageType.ElementId:
                    return p.AsValueString();
                case StorageType.Integer:
                case StorageType.Double:
                    string conUnidades = p.AsValueString();
                    if (!string.IsNullOrEmpty(conUnidades)) return conUnidades;
                    return p.StorageType == StorageType.Integer
                        ? p.AsInteger().ToString(INV)
                        : p.AsDouble().ToString(INV);
            }
        }
        catch { }
        return null;
    }

    // ---------- XLSX mínimo (sin librerías externas) ----------

    private void EscribirXlsx(string ruta, List<string> columnas, List<Dictionary<string, string>> filas)
    {
        if (File.Exists(ruta)) File.Delete(ruta);
        using (FileStream fs = new FileStream(ruta, FileMode.Create))
        using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            EntradaZip(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "</Types>");
            EntradaZip(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            EntradaZip(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Elementos\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            EntradaZip(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");

            ZipArchiveEntry hoja = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using (StreamWriter w = new StreamWriter(hoja.Open(), new UTF8Encoding(false)))
            {
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                w.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                w.Write("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                w.Write("<sheetData>");
                HashSet<int> numericas = new HashSet<int> { columnas.IndexOf(COL_LONG), columnas.IndexOf(COL_AREA), columnas.IndexOf(COL_AREA_BRUTA), columnas.IndexOf(COL_VOL), columnas.IndexOf(COL_W), columnas.IndexOf(COL_KG) };
                EscribirFila(w, 1, columnas, null);
                int r = 2;
                foreach (Dictionary<string, string> fila in filas)
                {
                    List<string> valores = new List<string>(columnas.Count);
                    foreach (string c in columnas)
                    {
                        string v;
                        valores.Add(fila.TryGetValue(c, out v) ? v : "");
                    }
                    EscribirFila(w, r++, valores, numericas);
                }
                w.Write("</sheetData></worksheet>");
            }
        }
    }

    private void EscribirFila(StreamWriter w, int r, List<string> valores, HashSet<int> numericas)
    {
        w.Write("<row r=\"" + r + "\">");
        for (int i = 0; i < valores.Count; i++)
        {
            string v = valores[i];
            if (string.IsNullOrEmpty(v)) continue;
            if (numericas != null && numericas.Contains(i))
            {
                w.Write("<c r=\"" + LetraColumna(i) + r + "\"><v>" + v + "</v></c>");
                continue;
            }
            w.Write("<c r=\"" + LetraColumna(i) + r + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + EscaparXml(v) + "</t></is></c>");
        }
        w.Write("</row>");
    }

    private void EntradaZip(ZipArchive zip, string nombre, string contenido)
    {
        ZipArchiveEntry e = zip.CreateEntry(nombre);
        using (StreamWriter w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(contenido);
    }

    private string LetraColumna(int indice)
    {
        string s = "";
        int n = indice + 1;
        while (n > 0)
        {
            int m = (n - 1) % 26;
            s = (char)('A' + m) + s;
            n = (n - 1) / 26;
        }
        return s;
    }

    private string EscaparXml(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (ch == '&') sb.Append("&amp;");
            else if (ch == '<') sb.Append("&lt;");
            else if (ch == '>') sb.Append("&gt;");
            else if (ch == '"') sb.Append("&quot;");
            else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue; // caracteres no válidos en XML
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private string NombreArchivo(string titulo)
    {
        string s = titulo;
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

// ---------- Hasta aquí ----------
