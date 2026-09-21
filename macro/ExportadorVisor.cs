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
 * Unidades: la geometría se escribe en metros.
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
using Autodesk.Revit.UI;

// ---------- Pegar desde aquí dentro de la clase ThisApplication ----------

    private const double PIES_A_METROS = 0.3048;
    private const bool USAR_COORDENADAS_COMPARTIDAS = true;

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

                GeometryElement geo = null;
                try { geo = el.get_Geometry(opciones); } catch { geo = null; }
                if (geo == null) continue;

                List<XYZ> vertices = new List<XYZ>();
                List<int> triangulos = new List<int>();
                RecogerGeometria(geo, vertices, triangulos);
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

        List<string> fijas = new List<string> { "ElementId", "UniqueId", "Categoría", "Familia", "Tipo", "Nivel" };
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

        AnadirParametros(el, "", d, columnasExtra);
        if (tipo != null) AnadirParametros(tipo, "Tipo: ", d, columnasExtra);
        return d;
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
                EscribirFila(w, 1, columnas);
                int r = 2;
                foreach (Dictionary<string, string> fila in filas)
                {
                    List<string> valores = new List<string>(columnas.Count);
                    foreach (string c in columnas)
                    {
                        string v;
                        valores.Add(fila.TryGetValue(c, out v) ? v : "");
                    }
                    EscribirFila(w, r++, valores);
                }
                w.Write("</sheetData></worksheet>");
            }
        }
    }

    private void EscribirFila(StreamWriter w, int r, List<string> valores)
    {
        w.Write("<row r=\"" + r + "\">");
        for (int i = 0; i < valores.Count; i++)
        {
            string v = valores[i];
            if (string.IsNullOrEmpty(v)) continue;
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
