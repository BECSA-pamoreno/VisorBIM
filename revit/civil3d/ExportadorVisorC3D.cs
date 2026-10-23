// ============================================================================
// BECSATools - Exportador al Visor BIM para Civil 3D 2027  v1.4  (2026-10-23)
// ----------------------------------------------------------------------------
// Comando BECSA_EXPORTARVISOR
//
// Hace lo mismo que el Exportar Visor de BECSATools en Revit: pregunta donde guardar
// (ventana "Guardar como", recuerda la ultima carpeta, la misma que en Revit) y escribe
//   <nombre>.dae   geometria triangulada (Collada), un nodo por objeto con name = Id
//   <nombre>.xlsx  una fila por objeto: Id, categoria, familia, tipo, capa, mediciones,
//                  propiedades de Civil 3D, datos de pieza y conjuntos de propiedades
// El visor (VisorBIM > Visor Revit) enlaza los dos archivos por la columna ElementId
// y los federa con los modelos de Revit por coordenadas.
//
// Que exporta (todo lo visible del espacio modelo, o la seleccion):
//   - Superficies TIN (terreno, explanacion, superficies de obra lineal...)
//   - Obras lineales: una pieza por capa (forma) de cada subensamblaje, uniendo sus
//     secciones, con PK inicial/final y volumen por prismoides (seccion intermedia con
//     las dimensiones promediadas). Si la API no da secciones: sus solidos (como
//     "Extraer solidos de obra lineal", en un dibujo temporal; el dibujo no se toca)
//   - Ejes: hoja "Ejes" del Excel con puntos cada pocos metros y su PK (cota del perfil
//     de rasante o, si no tiene, de la superficie), para ver los PK en el visor
//   - Redes de tuberias (tubos y estructuras) y redes a presion (tubos, accesorios y
//     dispositivos)
//   - Solidos 3D, cuerpos, superficies de AutoCAD, mallas, caras 3D
//   - Bloques (un objeto por insercion, con sus atributos)
//   - Referencias externas: su contenido, objeto a objeto
// No exporta lineas (ejes, lineas caracteristicas, polilineas 3D): el visor solo
// muestra volumenes y superficies, igual que con Revit.
//
// Coordenadas: las del dibujo (SCU), en metros. Si el proyecto de Revit adquirio las
// coordenadas compartidas de este dibujo, los dos modelos caen en su sitio en el visor.
// Para no perder precision con coordenadas UTM, los vertices se escriben relativos a un
// origen redondeado que se guarda en el DAE: <subject>ORIGEN x y z SISTEMA compartidas</subject>
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using AcFace = Autodesk.AutoCAD.DatabaseServices.Face;
using AcSurface = Autodesk.AutoCAD.DatabaseServices.Surface;
using C3D = Autodesk.Civil.DatabaseServices;

[assembly: CommandClass(typeof(BecsaTools.ExportadorVisorC3D))]

namespace BecsaTools
{
    public class ExportadorVisorC3D
    {
        public const string VERSION = "v1.4";

        // Columnas de medicion: las mismas que escribe la macro de Revit (el visor las reconoce)
        private const string COL_LONG = "Longitud (m)";
        private const string COL_AREA = "Área (m²)";
        private const string COL_VOL = "Volumen (m³)";
        private const string COL_AREA_PLANTA = "Área en planta (m²)";

        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;
        private static readonly CultureInfo ES = CultureInfo.GetCultureInfo("es-ES");

        // ---------------- Estado de una exportacion ----------------
        private double _k = 1.0;                 // unidades del dibujo -> metros
        private double _ox, _oy, _oz;            // origen redondeado (m)
        private StreamWriter _dae;
        private readonly List<string> _ids = new List<string>();
        private readonly HashSet<string> _idsUsados = new HashSet<string>();
        private readonly List<Dictionary<string, string>> _filas = new List<Dictionary<string, string>>();
        private readonly HashSet<string> _columnas = new HashSet<string>();
        private readonly SortedDictionary<string, int> _porCategoria = new SortedDictionary<string, int>();
        private readonly SortedDictionary<string, int> _omitidos = new SortedDictionary<string, int>();
        private readonly List<string> _avisos = new List<string>();
        private HashSet<ObjectId> _capasOcultas = new HashSet<ObjectId>();
        private long _triangulos;
        private string _nombreDibujo = "";

        // Contexto del objeto que se exporta (bloque / referencia externa que lo contiene)
        private sealed class Contexto
        {
            public Matrix3d Xf = Matrix3d.Identity;
            public string Prefijo = "";          // para que los Id de objetos dentro de referencias no se repitan
            public string RefExterna = "";
            public int Profundidad;
        }

        [CommandMethod("BECSA_EXPORTARVISOR", CommandFlags.Modal)]
        public void Exportar()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }
            Database db = doc.Database;
            Editor ed = doc.Editor;

            ed.WriteMessage("\nBECSATools - Exportar al Visor BIM " + VERSION);

            // ---- Que exportar ----
            PromptKeywordOptions pko = new PromptKeywordOptions("\nExportar [Todo/Seleccion] <Todo>: ");
            pko.Keywords.Add("Todo");
            pko.Keywords.Add("Seleccion");
            pko.Keywords.Default = "Todo";
            pko.AllowNone = true;
            PromptResult pkr = ed.GetKeywords(pko);
            if (pkr.Status != PromptStatus.OK && pkr.Status != PromptStatus.None)
            {
                return;
            }
            bool soloSeleccion = pkr.Status == PromptStatus.OK && pkr.StringResult == "Seleccion";

            List<ObjectId> ids = new List<ObjectId>();
            if (soloSeleccion)
            {
                PromptSelectionResult psr = ed.GetSelection();
                if (psr.Status != PromptStatus.OK)
                {
                    return;
                }
                ids.AddRange(psr.Value.GetObjectIds());
            }

            Reiniciar();
            _k = FactorMetros(db, ed);
            _nombreDibujo = NombreDibujo(doc);

            // ---- Donde guardar (como en Revit) ----
            string rutaDae = PedirDestino(_nombreDibujo);
            if (rutaDae == null)
            {
                ed.WriteMessage("\nExportacion cancelada.");
                return;
            }
            string rutaXlsx = Path.ChangeExtension(rutaDae, ".xlsx");
            if (!ComprobarDestino(rutaDae, rutaXlsx))
            {
                ed.WriteMessage("\nExportacion cancelada.");
                return;
            }
            GuardarCarpeta(Path.GetDirectoryName(rutaDae));

            Stopwatch reloj = Stopwatch.StartNew();
            bool cancelado = false;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                _capasOcultas = CapasOcultas(db, tr);

                if (!soloSeleccion)
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        ids.Add(id);
                    }
                }

                CalcularOrigen(ids, tr);

                ProgressMeter pm = new ProgressMeter();
                pm.SetLimit(Math.Max(1, ids.Count));
                pm.Start("BECSATools: exportando al Visor BIM");
                try
                {
                    using (_dae = new StreamWriter(rutaDae, false, new UTF8Encoding(false), 1 << 20))
                    {
                        EscribirCabeceraDae();
                        _dae.WriteLine("  <library_geometries>");

                        foreach (ObjectId id in ids)
                        {
                            pm.MeterProgress();
                            if (HostApplicationServices.Current.UserBreak())
                            {
                                cancelado = true;
                                break;
                            }
                            AcEntity ent = null;
                            try
                            {
                                ent = tr.GetObject(id, OpenMode.ForRead, false) as AcEntity;
                            }
                            catch
                            {
                                ent = null;
                            }
                            if (ent == null || !EsVisible(ent))
                            {
                                continue;
                            }
                            try
                            {
                                Procesar(ent, new Contexto(), tr);
                            }
                            catch (System.Exception ex)
                            {
                                Omitir(ent.GetType().Name + " (error)");
                                if (_avisos.Count < 15)
                                {
                                    _avisos.Add(ent.GetType().Name + " " + ent.Handle + ": " + ex.Message);
                                }
                            }
                        }

                        _dae.WriteLine("  </library_geometries>");
                        EscribirEscena();
                        CalcularEjes(tr);
                    }
                }
                finally
                {
                    pm.Stop();
                    IDisposable dpm = ((object)pm) as IDisposable;
                    if (dpm != null)
                    {
                        dpm.Dispose();
                    }
                    _dae = null;
                }
                tr.Commit();   // solo se ha leido: no cambia nada del dibujo
            }

            if (cancelado)
            {
                ed.WriteMessage("\nExportacion cancelada (Esc). Los archivos estan incompletos.");
                return;
            }

            List<string> fijas = new List<string> { "ElementId", "UniqueId", "Categoría", "Familia", "Tipo", "Nivel", "Capa", "Nombre",
                "Descripción", "Estilo", COL_LONG, COL_AREA, COL_AREA_PLANTA, COL_VOL, "Referencia externa" };
            List<string> columnas = new List<string>(fijas);
            columnas.AddRange(_columnas.Where(c => !fijas.Contains(c)).OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase));
            EscribirXlsx(rutaXlsx, columnas, _filas, _filasEjes);

            reloj.Stop();

            StringBuilder res = new StringBuilder();
            res.AppendLine("Coordenadas del dibujo (compartidas), origen " + _ox.ToString("0", INV) + ", " + _oy.ToString("0", INV)
                + ", " + _oz.ToString("0", INV) + " m. Unidades: 1 unidad = " + _k.ToString("0.####", INV) + " m.");
            res.AppendLine("Exportados " + _filas.Count + " objetos (" + _triangulos.ToString("N0", ES) + " triángulos) en "
                + reloj.Elapsed.TotalSeconds.ToString("0.0", ES) + " s.");
            if (_ejesPendientes.Count > 0)
            {
                res.AppendLine("Ejes con PK: " + _ejesPendientes.Count + " (hoja \"Ejes\" del Excel).");
            }
            res.AppendLine();
            foreach (KeyValuePair<string, int> kv in _porCategoria)
            {
                res.AppendLine("  " + kv.Key + ": " + kv.Value);
            }
            if (_omitidos.Count > 0)
            {
                res.AppendLine();
                res.AppendLine("No exportados (sin volumen ni superficie, o sin geometría):");
                foreach (KeyValuePair<string, int> kv in _omitidos.OrderByDescending(x => x.Value).Take(12))
                {
                    res.AppendLine("  " + kv.Key + ": " + kv.Value);
                }
            }
            if (_avisos.Count > 0)
            {
                res.AppendLine();
                res.AppendLine("Avisos:");
                foreach (string a in _avisos)
                {
                    res.AppendLine("  " + a);
                }
            }
            res.AppendLine();
            res.AppendLine(rutaDae);
            res.AppendLine(rutaXlsx);

            ed.WriteMessage("\n" + res.ToString().Replace("\r", ""));
            // Mismo resumen que en Revit, con la opcion de abrir la carpeta
            System.Windows.Forms.DialogResult r = System.Windows.Forms.MessageBox.Show(VentanaAcad(),
                "Exportación terminada.\n\n" + res.ToString() + "\n¿Abrir la carpeta?", "Exportar al Visor BIM",
                System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Information);
            if (r == System.Windows.Forms.DialogResult.Yes)
            {
                try
                {
                    Process.Start("explorer.exe", "/select,\"" + rutaDae + "\"");
                }
                catch
                {
                }
            }
        }

        // ====================================================================
        // Destino: ventana "Guardar como" que recuerda la ultima carpeta
        // (el mismo archivo que usa el Exportar Visor de Revit: los dos abren en la misma carpeta)
        // ====================================================================

        private static string ArchivoConfig()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BECSATools", "ExportadorVisor.txt");
        }

        private static string CarpetaInicial()
        {
            try
            {
                string f = ArchivoConfig();
                if (File.Exists(f))
                {
                    string c = File.ReadAllText(f).Trim();
                    if (!string.IsNullOrEmpty(c) && Directory.Exists(c))
                    {
                        return c;
                    }
                }
            }
            catch
            {
            }
            string escritorio = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string porDefecto = Path.Combine(escritorio, "ExportVisor");
            return Directory.Exists(porDefecto) ? porDefecto : escritorio;
        }

        private static void GuardarCarpeta(string carpeta)
        {
            try
            {
                string f = ArchivoConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                File.WriteAllText(f, carpeta ?? "");
            }
            catch
            {
            }
        }

        private static string PedirDestino(string nombre)
        {
            using (System.Windows.Forms.SaveFileDialog dlg = new System.Windows.Forms.SaveFileDialog())
            {
                dlg.Title = "Exportar al Visor BIM - dónde guardar";
                dlg.Filter = "Modelo para el Visor BIM (*.dae)|*.dae";
                dlg.DefaultExt = "dae";
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                dlg.InitialDirectory = CarpetaInicial();
                dlg.FileName = nombre + ".dae";
                if (dlg.ShowDialog(VentanaAcad()) != System.Windows.Forms.DialogResult.OK)
                {
                    return null;
                }
                return dlg.FileName;
            }
        }

        // El .xlsx va junto al .dae con el mismo nombre (el visor los empareja por nombre)
        private static bool ComprobarDestino(string rutaDae, string rutaXlsx)
        {
            // Si el .dae no existia (no se ha preguntado al guardar) pero el .xlsx si, se pregunta por el .xlsx
            if (File.Exists(rutaXlsx) && !File.Exists(rutaDae))
            {
                System.Windows.Forms.DialogResult r = System.Windows.Forms.MessageBox.Show(VentanaAcad(),
                    Path.GetFileName(rutaXlsx) + " ya existe.\n¿Quieres reemplazarlo?", "Exportar al Visor BIM",
                    System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning);
                if (r != System.Windows.Forms.DialogResult.Yes)
                {
                    return false;
                }
            }
            // Antes de exportar nada: que ninguno de los dos este abierto (p. ej. el Excel)
            foreach (string ruta in new string[] { rutaDae, rutaXlsx })
            {
                while (EstaBloqueado(ruta))
                {
                    System.Windows.Forms.DialogResult r = System.Windows.Forms.MessageBox.Show(VentanaAcad(),
                        Path.GetFileName(ruta) + " está abierto en otro programa (¿Excel?).\nCiérralo y pulsa Reintentar.",
                        "Exportar al Visor BIM", System.Windows.Forms.MessageBoxButtons.RetryCancel, System.Windows.Forms.MessageBoxIcon.Warning);
                    if (r != System.Windows.Forms.DialogResult.Retry)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool EstaBloqueado(string ruta)
        {
            if (!File.Exists(ruta))
            {
                return false;
            }
            try
            {
                using (FileStream fs = new FileStream(ruta, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Ventana principal de Civil 3D como propietaria de los dialogos (para que no queden detras)
        private sealed class VentanaPropietaria : System.Windows.Forms.IWin32Window
        {
            private readonly IntPtr _h;

            public VentanaPropietaria(IntPtr h)
            {
                _h = h;
            }

            public IntPtr Handle
            {
                get { return _h; }
            }
        }

        private static System.Windows.Forms.IWin32Window VentanaAcad()
        {
            try
            {
                PropertyInfo p = typeof(Autodesk.AutoCAD.ApplicationServices.Application).GetProperty("MainWindow", BindingFlags.Public | BindingFlags.Static);
                object mw = p != null ? p.GetValue(null, null) : null;
                object h = Prop(mw, "Handle");
                if (h is IntPtr && (IntPtr)h != IntPtr.Zero)
                {
                    return new VentanaPropietaria((IntPtr)h);
                }
            }
            catch
            {
            }
            return null;
        }

        private void Reiniciar()
        {
            _ids.Clear();
            _idsUsados.Clear();
            _filas.Clear();
            _columnas.Clear();
            _porCategoria.Clear();
            _omitidos.Clear();
            _avisos.Clear();
            _triangulos = 0;
            _ejesPendientes.Clear();
            _superficiesTin.Clear();
            _filasEjes.Clear();
        }

        // ====================================================================
        // Recorrido de objetos
        // ====================================================================

        private void Procesar(AcEntity ent, Contexto cx, Transaction tr)
        {
            // ---- Civil 3D ----
            C3D.TinSurface tin = ent as C3D.TinSurface;
            if (tin != null)
            {
                ExportarSuperficieTin(tin, cx);
                return;
            }
            C3D.Surface sup = ent as C3D.Surface;
            if (sup != null)
            {
                ExportarSuperficieGenerica(sup, cx);
                return;
            }
            C3D.Corridor obra = ent as C3D.Corridor;
            if (obra != null)
            {
                ExportarObraLineal(obra, cx, tr);
                return;
            }
            if (ent is C3D.Pipe)
            {
                ExportarPieza(ent, "Tuberías", cx, tr);
                return;
            }
            if (ent is C3D.Structure)
            {
                ExportarPieza(ent, "Estructuras", cx, tr);
                return;
            }
            // Redes a presion: por nombre de clase (asi compila aunque cambie el ensamblado que las define)
            string clase = ent.GetType().Name;
            if (clase == "PressurePipe")
            {
                ExportarPieza(ent, "Tuberías a presión", cx, tr);
                return;
            }
            if (clase == "PressureFitting")
            {
                ExportarPieza(ent, "Accesorios a presión", cx, tr);
                return;
            }
            if (clase == "PressureAppurtenance")
            {
                ExportarPieza(ent, "Dispositivos a presión", cx, tr);
                return;
            }

            // ---- Bloques y referencias externas ----
            BlockReference br = ent as BlockReference;
            if (br != null)
            {
                if (br is Table)
                {
                    return;
                }
                ExportarBloque(br, cx, tr);
                return;
            }

            // ---- AutoCAD 3D ----
            string cat = CategoriaAutoCad(ent);
            if (cat != null)
            {
                Malla m = new Malla(_k);
                MallarEntidad(ent, cx.Xf, m);
                if (m.Triangulos == 0)
                {
                    Omitir(cat + " (no se pudo triangular)");
                    return;
                }
                Dictionary<string, string> d = FilaBase(ent, cx, cat, NombreTipoAutoCad(ent), ent.Layer);
                if (ent is AcSurface || ent is AcFace)
                {
                    d[COL_AREA] = Num(m.Area());   // superficie abierta: su area real
                }
                MedidasSolido(d, m);
                ConjuntosDePropiedades(ent, tr, d);
                Emitir(IdDe(ent, cx), m, d, cat);
                return;
            }

            // Ejes: se guardan para la hoja "Ejes" (PK en el visor)
            C3D.Alignment eje = ent as C3D.Alignment;
            if (eje != null)
            {
                EjePendiente ep = new EjePendiente();
                ep.Id = eje.ObjectId;
                ep.Xf = cx.Xf;
                ep.RefExterna = cx.RefExterna;
                _ejesPendientes.Add(ep);
                return;
            }

            // Objetos de Civil 3D sin volumen (lineas caracteristicas, rotulos...): se cuentan
            string ns = ent.GetType().Namespace ?? "";
            if (ns.StartsWith("Autodesk.Civil", StringComparison.Ordinal))
            {
                Omitir(ent.GetType().Name);
            }
        }

        private static string CategoriaAutoCad(AcEntity ent)
        {
            if (ent is Solid3d) return "Sólidos 3D";
            if (ent is Body) return "Sólidos 3D";
            if (ent is AcSurface) return "Superficies de AutoCAD";
            if (ent is SubDMesh || ent is PolyFaceMesh || ent is PolygonMesh || ent is AcFace) return "Mallas";
            return null;
        }

        private static string NombreTipoAutoCad(AcEntity ent)
        {
            if (ent is Solid3d) return "Sólido 3D";
            if (ent is Body) return "Cuerpo";
            if (ent is AcSurface) return "Superficie";
            if (ent is SubDMesh) return "Malla";
            if (ent is PolyFaceMesh) return "Malla policara";
            if (ent is PolygonMesh) return "Malla poligonal";
            if (ent is AcFace) return "Cara 3D";
            return ent.GetType().Name;
        }

        // ====================================================================
        // Superficies
        // ====================================================================

        private void ExportarSuperficieTin(C3D.TinSurface s, Contexto cx)
        {
            if (string.IsNullOrEmpty(cx.Prefijo))
            {
                _superficiesTin.Add(s.ObjectId);   // para dar cota a los ejes sin perfil
            }
            Malla m = new Malla(_k);
            double zmin = double.MaxValue, zmax = double.MinValue;
            var tris = s.GetTriangles(false);
            foreach (C3D.TinSurfaceTriangle t in tris)
            {
                Point3d a = t.Vertex1.Location.TransformBy(cx.Xf);
                Point3d b = t.Vertex2.Location.TransformBy(cx.Xf);
                Point3d c = t.Vertex3.Location.TransformBy(cx.Xf);
                m.TrianguloCompartido(a, b, c);
                zmin = Math.Min(zmin, Math.Min(a.Z, Math.Min(b.Z, c.Z)));
                zmax = Math.Max(zmax, Math.Max(a.Z, Math.Max(b.Z, c.Z)));
            }
            if (m.Triangulos == 0)
            {
                Omitir("Superficie sin triángulos");
                return;
            }
            Dictionary<string, string> d = FilaBase(s, cx, "Superficies", Texto(Prop(s, "StyleName")), s.Name);
            d[COL_AREA] = Num(m.Area());
            d[COL_AREA_PLANTA] = Num(m.AreaPlanta());
            d[COL_LONG] = Num(m.Perimetro());   // ml: perimetro de la superficie
            Campo(d, "Nº de triángulos", m.Triangulos.ToString(INV));
            Campo(d, "Cota mínima (m)", (zmin * _k).ToString("0.###", ES));
            Campo(d, "Cota máxima (m)", (zmax * _k).ToString("0.###", ES));
            PropiedadesCivil(s, d);
            Emitir(IdDe(s, cx), m, d, "Superficies");
        }

        // Otras superficies de Civil (de volumen, rejilla...): si tienen GetTriangles se exportan igual
        private void ExportarSuperficieGenerica(C3D.Surface s, Contexto cx)
        {
            MethodInfo mi = null;
            try
            {
                mi = s.GetType().GetMethod("GetTriangles", new Type[] { typeof(bool) });
            }
            catch
            {
                mi = null;
            }
            IEnumerable tris = null;
            if (mi != null)
            {
                try
                {
                    tris = mi.Invoke(s, new object[] { false }) as IEnumerable;
                }
                catch
                {
                    tris = null;
                }
            }
            if (tris == null)
            {
                Omitir(s.GetType().Name);
                return;
            }
            Malla m = new Malla(_k);
            foreach (object t in tris)
            {
                object v1 = Prop(Prop(t, "Vertex1"), "Location");
                object v2 = Prop(Prop(t, "Vertex2"), "Location");
                object v3 = Prop(Prop(t, "Vertex3"), "Location");
                if (v1 is Point3d && v2 is Point3d && v3 is Point3d)
                {
                    m.TrianguloCompartido(((Point3d)v1).TransformBy(cx.Xf), ((Point3d)v2).TransformBy(cx.Xf), ((Point3d)v3).TransformBy(cx.Xf));
                }
            }
            if (m.Triangulos == 0)
            {
                Omitir(s.GetType().Name);
                return;
            }
            Dictionary<string, string> d = FilaBase(s, cx, "Superficies", Texto(Prop(s, "StyleName")), s.Name);
            d[COL_AREA] = Num(m.Area());
            d[COL_AREA_PLANTA] = Num(m.AreaPlanta());
            d[COL_LONG] = Num(m.Perimetro());
            Campo(d, "Nº de triángulos", m.Triangulos.ToString(INV));
            PropiedadesCivil(s, d);
            Emitir(IdDe(s, cx), m, d, "Superficies");
        }

        // ====================================================================
        // Obras lineales por secciones: una pieza por capa (forma) de cada subensamblaje,
        // con sus PK y el volumen por prismoides entre secciones consecutivas
        // ====================================================================

        private sealed class SeccionForma
        {
            public double PK;
            public List<Point3d> Mundo = new List<Point3d>();
            public List<Point2d> OE = new List<Point2d>();   // desplazamiento, cota (plano de la seccion)
        }

        private sealed class TramoForma
        {
            public string LineaBase = "", Eje = "", Region = "", Subensamblaje = "", Codigos = "";
            public List<SeccionForma> Secciones = new List<SeccionForma>();
        }

        // Devuelve cuantas piezas ha exportado (0 = la API no ha dado secciones: se usan los solidos)
        private int ExportarObraPorSecciones(C3D.Corridor obra, Contexto cx, Transaction tr)
        {
            IEnumerable lineas = Prop(obra, "Baselines") as IEnumerable;
            if (lineas == null)
            {
                return 0;
            }
            Dictionary<string, TramoForma> tramos = new Dictionary<string, TramoForma>();
            List<string> orden = new List<string>();
            int bi = 0;
            foreach (object bl in lineas)
            {
                bi++;
                string lbNom = Texto(Prop(bl, "Name"));
                if (string.IsNullOrEmpty(lbNom))
                {
                    lbNom = "Línea base " + bi.ToString(INV);
                }
                string eje = NombreDeId(Prop(bl, "AlignmentId"), tr);
                IEnumerable regiones = Prop(bl, "BaselineRegions") as IEnumerable;
                if (regiones == null)
                {
                    continue;
                }
                int ri = 0;
                foreach (object rg in regiones)
                {
                    ri++;
                    string rgNom = Texto(Prop(rg, "Name"));
                    if (string.IsNullOrEmpty(rgNom))
                    {
                        rgNom = "Región " + ri.ToString(INV);
                    }
                    IEnumerable aplicados = Prop(rg, "AppliedAssemblies") as IEnumerable;
                    if (aplicados == null)
                    {
                        continue;
                    }
                    foreach (object aa in aplicados)
                    {
                        double pk = PrimerNumero(aa, "Station");
                        if (double.IsNaN(pk))
                        {
                            continue;
                        }
                        IEnumerable subs = (Llamar(aa, "GetAppliedSubassemblies") ?? Prop(aa, "AppliedSubassemblies")) as IEnumerable;
                        string prefijo = bi.ToString(INV) + "|" + ri.ToString(INV) + "|";
                        if (subs != null)
                        {
                            int si = 0;
                            foreach (object sub in subs)
                            {
                                si++;
                                string subNom = NombreDeId(Prop(sub, "SubassemblyId"), tr);
                                if (string.IsNullOrEmpty(subNom))
                                {
                                    subNom = "Subensamblaje " + si.ToString(INV);
                                }
                                AnadirFormas(Prop(sub, "Shapes") as IEnumerable, pk, prefijo + si.ToString(INV) + "|" + subNom,
                                    lbNom, eje, rgNom, subNom, cx, tramos, orden);
                            }
                        }
                        else
                        {
                            AnadirFormas(Prop(aa, "Shapes") as IEnumerable, pk, prefijo + "0|", lbNom, eje, rgNom, "", cx, tramos, orden);
                        }
                    }
                }
            }

            int n = 0;
            foreach (string clave in orden)
            {
                if (EmitirTramoForma(obra, tramos[clave], cx, n + 1))
                {
                    n++;
                }
            }
            return n;
        }

        private void AnadirFormas(IEnumerable formas, double pk, string prefijo, string lb, string eje, string region, string sub,
            Contexto cx, Dictionary<string, TramoForma> tramos, List<string> orden)
        {
            if (formas == null)
            {
                return;
            }
            int fi = 0;
            foreach (object f in formas)
            {
                fi++;
                SeccionForma s = AnilloForma(f, cx.Xf);
                if (s == null)
                {
                    continue;
                }
                s.PK = pk * _k;
                string codigos = Codigos(Prop(f, "CorridorCodes"));
                string clave = prefijo + "|" + fi.ToString(INV) + "|" + codigos;
                TramoForma t;
                if (!tramos.TryGetValue(clave, out t))
                {
                    t = new TramoForma();
                    t.LineaBase = lb;
                    t.Eje = eje ?? "";
                    t.Region = region;
                    t.Subensamblaje = sub;
                    t.Codigos = codigos;
                    tramos[clave] = t;
                    orden.Add(clave);
                }
                t.Secciones.Add(s);
            }
        }

        private static string Codigos(object col)
        {
            IEnumerable e = col as IEnumerable;
            if (e == null || col is string)
            {
                return Texto(col) ?? "";
            }
            List<string> l = new List<string>();
            foreach (object o in e)
            {
                string s = Texto(o);
                if (!string.IsNullOrEmpty(s) && !l.Contains(s))
                {
                    l.Add(s);
                }
            }
            return string.Join(", ", l);
        }

        // Contorno cerrado de una forma calculada, encadenando sus vinculos
        private SeccionForma AnilloForma(object forma, Matrix3d xf)
        {
            IEnumerable vinculos = (Prop(forma, "CalculatedLinks") ?? Prop(forma, "Links")) as IEnumerable;
            if (vinculos == null)
            {
                return null;
            }
            List<Point3d> mundo = new List<Point3d>();
            List<Point2d> oe = new List<Point2d>();
            Dictionary<(long, long), int> nodo = new Dictionary<(long, long), int>();
            List<int[]> aristas = new List<int[]>();
            bool sinOE = false;
            foreach (object lk in vinculos)
            {
                IEnumerable puntos = (Prop(lk, "CalculatedPoints") ?? Prop(lk, "Points")) as IEnumerable;
                if (puntos == null)
                {
                    continue;
                }
                int anterior = -1;
                foreach (object p in puntos)
                {
                    object w = Prop(p, "XYZ");
                    if (!(w is Point3d))
                    {
                        continue;
                    }
                    Point3d pw = (Point3d)w;
                    object soe = Prop(p, "StationOffsetElevationToBaseline");
                    Point2d q;
                    if (soe is Point3d)
                    {
                        q = new Point2d(((Point3d)soe).Y, ((Point3d)soe).Z);
                    }
                    else
                    {
                        sinOE = true;
                        q = new Point2d(pw.X, pw.Y);
                    }
                    (long, long) k = sinOE ? ((long)Math.Round(pw.X * 1000), (long)Math.Round(pw.Y * 1000) * 7919 + (long)Math.Round(pw.Z * 1000))
                                           : ((long)Math.Round(q.X * 1000), (long)Math.Round(q.Y * 1000));
                    int i;
                    if (!nodo.TryGetValue(k, out i))
                    {
                        i = mundo.Count;
                        nodo[k] = i;
                        mundo.Add(pw);
                        oe.Add(q);
                    }
                    if (anterior >= 0 && anterior != i)
                    {
                        aristas.Add(new int[] { anterior, i });
                    }
                    anterior = i;
                }
            }
            if (mundo.Count < 3)
            {
                return null;
            }
            if (sinOE)
            {
                // Sin coordenadas de seccion: plano vertical que mejor ajusta los puntos (eje horizontal principal)
                double cx = mundo.Average(p => p.X), cy = mundo.Average(p => p.Y);
                double sxx = 0, sxy = 0, syy = 0;
                foreach (Point3d p in mundo)
                {
                    sxx += (p.X - cx) * (p.X - cx);
                    sxy += (p.X - cx) * (p.Y - cy);
                    syy += (p.Y - cy) * (p.Y - cy);
                }
                double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ux = Math.Cos(ang), uy = Math.Sin(ang);
                for (int i = 0; i < mundo.Count; i++)
                {
                    oe[i] = new Point2d((mundo[i].X - cx) * ux + (mundo[i].Y - cy) * uy, mundo[i].Z);
                }
            }

            // Encadenar las aristas en un anillo
            List<int>[] ady = new List<int>[mundo.Count];
            for (int i = 0; i < ady.Length; i++)
            {
                ady[i] = new List<int>();
            }
            foreach (int[] a in aristas)
            {
                if (!ady[a[0]].Contains(a[1])) ady[a[0]].Add(a[1]);
                if (!ady[a[1]].Contains(a[0])) ady[a[1]].Add(a[0]);
            }
            List<int> anillo = new List<int>();
            bool[] usado = new bool[mundo.Count];
            int actual = 0, previo = -1;
            while (actual >= 0 && !usado[actual])
            {
                usado[actual] = true;
                anillo.Add(actual);
                int sig = -1;
                foreach (int v in ady[actual])
                {
                    if (v != previo && !usado[v])
                    {
                        sig = v;
                        break;
                    }
                }
                previo = actual;
                actual = sig;
            }
            if (anillo.Count != mundo.Count)
            {
                // Vinculos que no cierran: se ordenan los puntos por angulo alrededor del centro
                double mx = oe.Average(p => p.X), my = oe.Average(p => p.Y);
                anillo = Enumerable.Range(0, mundo.Count).OrderBy(i => Math.Atan2(oe[i].Y - my, oe[i].X - mx)).ToList();
            }
            SeccionForma s = new SeccionForma();
            foreach (int i in anillo)
            {
                Point3d w = mundo[i].TransformBy(xf);
                s.Mundo.Add(new Point3d(w.X, w.Y, w.Z));
                s.OE.Add(new Point2d(oe[i].X * _k, oe[i].Y * _k));
            }
            // Siempre en sentido antihorario en el plano de la seccion
            if (AreaConSigno(s.OE) < 0)
            {
                s.Mundo.Reverse();
                s.OE.Reverse();
            }
            return Math.Abs(AreaConSigno(s.OE)) > 1e-9 ? s : null;
        }

        private static double AreaConSigno(List<Point2d> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++)
            {
                Point2d u = p[i], v = p[(i + 1) % p.Count];
                a += u.X * v.Y - v.X * u.Y;
            }
            return a / 2;
        }

        // Une las secciones de una capa en un solido y calcula su volumen por prismoides
        private bool EmitirTramoForma(C3D.Corridor obra, TramoForma t, Contexto cx, int indice)
        {
            List<SeccionForma> ss = t.Secciones.OrderBy(s => s.PK).ToList();
            for (int i = ss.Count - 1; i > 0; i--)
            {
                if (Math.Abs(ss[i].PK - ss[i - 1].PK) < 1e-6)
                {
                    ss.RemoveAt(i);
                }
            }
            if (ss.Count < 2)
            {
                return false;
            }
            Malla m = new Malla(_k);
            double vPrisma = 0, vMedia = 0, longitud = 0, areaPlanta = 0;
            bool abierto = false;
            for (int i = 0; i < ss.Count - 1; i++)
            {
                SeccionForma a = ss[i], b = ss[i + 1];
                // Distancia entre los centros de las dos secciones (en planta): en curva, una capa desplazada
                // del eje (bordillo, cuneta...) mide distinto que el PK del eje
                double d = DistanciaCentros(a, b);
                if (d < 1e-6)
                {
                    d = b.PK - a.PK;
                }
                longitud += d;
                areaPlanta += d * (Ancho(a) + Ancho(b)) / 2;
                double a1 = Math.Abs(AreaConSigno(a.OE)), a2 = Math.Abs(AreaConSigno(b.OE)), am;
                bool unidas = a.OE.Count == b.OE.Count;
                if (unidas)
                {
                    int desfase = MejorDesfase(a.OE, b.OE);
                    int n = a.OE.Count;
                    // Seccion intermedia: promedio de las dimensiones (vertice a vertice), no de las areas
                    List<Point2d> media = new List<Point2d>(n);
                    for (int k = 0; k < n; k++)
                    {
                        Point2d p = a.OE[k], q = b.OE[(k + desfase) % n];
                        media.Add(new Point2d((p.X + q.X) / 2, (p.Y + q.Y) / 2));
                    }
                    am = Math.Abs(AreaConSigno(media));
                    if (!abierto)
                    {
                        Tapa(m, a, true);
                    }
                    for (int k = 0; k < n; k++)
                    {
                        int k2 = (k + 1) % n;
                        m.Poligono(new Point3d[] { a.Mundo[k], a.Mundo[k2], b.Mundo[(k2 + desfase) % n], b.Mundo[(k + desfase) % n] });
                    }
                    abierto = true;
                    // Si la siguiente no enlaza, se cierra aqui
                    if (i + 2 >= ss.Count || ss[i + 2].OE.Count != b.OE.Count)
                    {
                        Tapa(m, Rotada(b, desfase), false);
                        abierto = false;
                    }
                    else if (desfase != 0)
                    {
                        ss[i + 1] = Rotada(b, desfase);
                    }
                }
                else
                {
                    am = Math.Pow((Math.Sqrt(a1) + Math.Sqrt(a2)) / 2, 2);   // cambia la forma: tronco de piramide
                    if (abierto)
                    {
                        Tapa(m, a, false);
                        abierto = false;
                    }
                }
                vPrisma += d / 6.0 * (a1 + a2 + 4 * am);
                vMedia += d / 2.0 * (a1 + a2);
            }
            if (m.Triangulos == 0)
            {
                return false;
            }
            double pk0 = ss[0].PK, pk1 = ss[ss.Count - 1].PK;
            string tipo = !string.IsNullOrEmpty(t.Codigos) ? t.Codigos : (!string.IsNullOrEmpty(t.Subensamblaje) ? t.Subensamblaje : "Forma");
            Dictionary<string, string> d0 = FilaBase(obra, cx, "Obras lineales", obra.Name, tipo);
            d0[COL_VOL] = Num(vPrisma);
            d0[COL_LONG] = Num(longitud);       // ml: bordillos, rigolas, cunetas...
            d0[COL_AREA] = Num(areaPlanta);     // m2: ancho medio x longitud (rodadura, riegos...)
            Campo(d0, "Obra lineal", obra.Name);
            Campo(d0, "Subensamblaje", t.Subensamblaje);
            Campo(d0, "Código de forma", t.Codigos);
            Campo(d0, "Línea base", t.LineaBase);
            Campo(d0, "Eje", t.Eje);
            Campo(d0, "Región", t.Region);
            Campo(d0, "PK inicial", FormatoPK(pk0));
            Campo(d0, "PK final", FormatoPK(pk1));
            Campo(d0, "Nº de secciones", ss.Count.ToString(INV));
            Campo(d0, "Volumen por prismoides (m³)", vPrisma.ToString("0.###", ES));
            Campo(d0, "Volumen por áreas medias (m³)", vMedia.ToString("0.###", ES));
            Campo(d0, "Área media de sección (m²)", (longitud > 0 ? vPrisma / longitud : 0).ToString("0.###", ES));
            Campo(d0, "Ancho medio (m)", (longitud > 0 ? areaPlanta / longitud : 0).ToString("0.###", ES));
            Emitir(IdDe(obra, cx) + "-" + indice.ToString(INV), m, d0, "Obras lineales");
            return true;
        }

        // Centro de la seccion en planta (media de sus vertices, en metros)
        private double DistanciaCentros(SeccionForma a, SeccionForma b)
        {
            double ax = 0, ay = 0, bx = 0, by = 0;
            foreach (Point3d p in a.Mundo) { ax += p.X; ay += p.Y; }
            foreach (Point3d p in b.Mundo) { bx += p.X; by += p.Y; }
            ax /= a.Mundo.Count; ay /= a.Mundo.Count; bx /= b.Mundo.Count; by /= b.Mundo.Count;
            return Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) * _k;
        }

        // Ancho de la seccion (desplazamiento maximo - minimo)
        private static double Ancho(SeccionForma s)
        {
            double mn = double.MaxValue, mx = double.MinValue;
            foreach (Point2d p in s.OE)
            {
                if (p.X < mn) mn = p.X;
                if (p.X > mx) mx = p.X;
            }
            return mx > mn ? mx - mn : 0;
        }

        private static int MejorDesfase(List<Point2d> a, List<Point2d> b)
        {
            int n = a.Count, mejor = 0;
            double min = double.MaxValue;
            for (int s = 0; s < n; s++)
            {
                double suma = 0;
                for (int k = 0; k < n && suma < min; k++)
                {
                    suma += a[k].GetDistanceTo(b[(k + s) % n]);
                }
                if (suma < min)
                {
                    min = suma;
                    mejor = s;
                }
            }
            return mejor;
        }

        private static SeccionForma Rotada(SeccionForma s, int desfase)
        {
            if (desfase == 0)
            {
                return s;
            }
            SeccionForma r = new SeccionForma();
            r.PK = s.PK;
            int n = s.OE.Count;
            for (int k = 0; k < n; k++)
            {
                r.Mundo.Add(s.Mundo[(k + desfase) % n]);
                r.OE.Add(s.OE[(k + desfase) % n]);
            }
            return r;
        }

        // Tapa de una seccion (triangulacion por orejas: vale para formas concavas)
        private static void Tapa(Malla m, SeccionForma s, bool inicio)
        {
            int n = s.OE.Count;
            List<int> idx = Enumerable.Range(0, n).ToList();
            int guarda = 0;
            while (idx.Count > 3 && guarda++ < 10000)
            {
                bool cortada = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                    Point2d a = s.OE[ia], b = s.OE[ib], c = s.OE[ic];
                    double cruz = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    if (cruz <= 1e-12)
                    {
                        continue;   // vertice reflejo
                    }
                    bool dentro = false;
                    foreach (int j in idx)
                    {
                        if (j == ia || j == ib || j == ic)
                        {
                            continue;
                        }
                        if (DentroTriangulo(s.OE[j], a, b, c))
                        {
                            dentro = true;
                            break;
                        }
                    }
                    if (dentro)
                    {
                        continue;
                    }
                    TrianguloTapa(m, s, ia, ib, ic, inicio);
                    idx.RemoveAt(i);
                    cortada = true;
                    break;
                }
                if (!cortada)
                {
                    break;
                }
            }
            if (idx.Count == 3)
            {
                TrianguloTapa(m, s, idx[0], idx[1], idx[2], inicio);
            }
            else if (idx.Count > 3)
            {
                for (int i = 1; i < idx.Count - 1; i++)
                {
                    TrianguloTapa(m, s, idx[0], idx[i], idx[i + 1], inicio);
                }
            }
        }

        private static void TrianguloTapa(Malla m, SeccionForma s, int a, int b, int c, bool inicio)
        {
            if (inicio)
            {
                m.Poligono(new Point3d[] { s.Mundo[a], s.Mundo[c], s.Mundo[b] });
            }
            else
            {
                m.Poligono(new Point3d[] { s.Mundo[a], s.Mundo[b], s.Mundo[c] });
            }
        }

        private static bool DentroTriangulo(Point2d p, Point2d a, Point2d b, Point2d c)
        {
            double d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            double d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
            double d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static string FormatoPK(double pk)
        {
            double a = Math.Abs(pk);
            int km = (int)Math.Floor(a / 1000.0 + 1e-9);
            double resto = a - km * 1000.0;
            if (resto >= 999.995)
            {
                km++;
                resto = 0;
            }
            return (pk < 0 ? "-" : "") + km.ToString(INV) + "+" + resto.ToString("000.00", ES);
        }

        private static string NombreDeId(object id, Transaction tr)
        {
            if (!(id is ObjectId) || ((ObjectId)id).IsNull)
            {
                return null;
            }
            try
            {
                DBObject o = tr.GetObject((ObjectId)id, OpenMode.ForRead, false);
                return Texto(Prop(o, "Name"));
            }
            catch
            {
                return null;
            }
        }

        private static object Llamar(object o, string metodo)
        {
            if (o == null)
            {
                return null;
            }
            try
            {
                MethodInfo mi = o.GetType().GetMethod(metodo, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return mi != null ? mi.Invoke(o, null) : null;
            }
            catch
            {
                return null;
            }
        }

        // ====================================================================
        // Ejes: puntos cada pocos metros con su PK, para ver los PK en el visor
        // (hoja "Ejes" del Excel)
        // ====================================================================

        private sealed class EjePendiente
        {
            public ObjectId Id;
            public Matrix3d Xf;
            public string RefExterna;
        }

        private readonly List<EjePendiente> _ejesPendientes = new List<EjePendiente>();
        private readonly List<ObjectId> _superficiesTin = new List<ObjectId>();
        private readonly List<string[]> _filasEjes = new List<string[]>();

        private void CalcularEjes(Transaction tr)
        {
            foreach (EjePendiente ep in _ejesPendientes)
            {
                try
                {
                    C3D.Alignment eje = tr.GetObject(ep.Id, OpenMode.ForRead) as C3D.Alignment;
                    if (eje == null)
                    {
                        continue;
                    }
                    string nombre = eje.Name + (string.IsNullOrEmpty(ep.RefExterna) ? "" : " (" + ep.RefExterna + ")");
                    double pk0 = PrimerNumero(eje, "StartingStation"), pk1 = PrimerNumero(eje, "EndingStation");
                    if (double.IsNaN(pk0) || double.IsNaN(pk1) || pk1 <= pk0)
                    {
                        continue;
                    }
                    List<object> perfiles = PerfilesDe(eje, tr);
                    double paso = Math.Max(1.0 / _k, Math.Min(10.0 / _k, (pk1 - pk0) / 2000.0));
                    List<double> pks = new List<double>();
                    for (double s = pk0; s < pk1 - 1e-6; s += paso)
                    {
                        pks.Add(s);
                    }
                    pks.Add(pk1);
                    MethodInfo ubicar = null;
                    foreach (MethodInfo mi in eje.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (mi.Name == "PointLocation" && mi.GetParameters().Length == 4)
                        {
                            ubicar = mi;
                            break;
                        }
                    }
                    if (ubicar == null)
                    {
                        continue;
                    }
                    foreach (double s in pks)
                    {
                        object[] args = new object[] { s, 0.0, 0.0, 0.0 };
                        try
                        {
                            ubicar.Invoke(eje, args);
                        }
                        catch
                        {
                            continue;
                        }
                        double x = Convert.ToDouble(args[2], INV), y = Convert.ToDouble(args[3], INV);
                        string fuente;
                        double z = CotaEje(perfiles, s, x, y, tr, out fuente);
                        Point3d p = new Point3d(x, y, double.IsNaN(z) ? 0 : z).TransformBy(ep.Xf);
                        _filasEjes.Add(new string[]
                        {
                            nombre, (s * _k).ToString("0.###", INV), (p.X * _k).ToString("0.###", INV), (p.Y * _k).ToString("0.###", INV),
                            double.IsNaN(z) ? "" : (p.Z * _k).ToString("0.###", INV), fuente
                        });
                    }
                }
                catch (System.Exception ex)
                {
                    if (_avisos.Count < 15)
                    {
                        _avisos.Add("Eje: " + ex.Message);
                    }
                }
            }
        }

        // Perfiles del eje: primero los de rasante (FG / layout), luego el resto
        private static List<object> PerfilesDe(C3D.Alignment eje, Transaction tr)
        {
            List<object> fg = new List<object>(), otros = new List<object>();
            try
            {
                ObjectIdCollection ids = eje.GetProfileIds();
                foreach (ObjectId id in ids)
                {
                    DBObject p = tr.GetObject(id, OpenMode.ForRead);
                    string tipo = Texto(Prop(p, "ProfileType")) ?? "";
                    if (tipo.IndexOf("FG", StringComparison.OrdinalIgnoreCase) >= 0 || tipo.IndexOf("Layout", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fg.Add(p);
                    }
                    else
                    {
                        otros.Add(p);
                    }
                }
            }
            catch
            {
            }
            fg.AddRange(otros);
            return fg;
        }

        private double CotaEje(List<object> perfiles, double pk, double x, double y, Transaction tr, out string fuente)
        {
            foreach (object p in perfiles)
            {
                try
                {
                    MethodInfo mi = p.GetType().GetMethod("ElevationAt", new Type[] { typeof(double) });
                    if (mi == null)
                    {
                        continue;
                    }
                    double z = Convert.ToDouble(mi.Invoke(p, new object[] { pk }), INV);
                    if (!double.IsNaN(z) && !double.IsInfinity(z))
                    {
                        fuente = "Perfil " + (Texto(Prop(p, "Name")) ?? "");
                        return z;
                    }
                }
                catch
                {
                }
            }
            foreach (ObjectId sid in _superficiesTin)
            {
                try
                {
                    C3D.Surface s = tr.GetObject(sid, OpenMode.ForRead) as C3D.Surface;
                    if (s == null)
                    {
                        continue;
                    }
                    double z = s.FindElevationAtXY(x, y);
                    if (!double.IsNaN(z))
                    {
                        fuente = "Superficie " + s.Name;
                        return z;
                    }
                }
                catch
                {
                }
            }
            fuente = "";
            return double.NaN;
        }

        // ====================================================================
        // Obras lineales: se sacan sus solidos a un dibujo temporal y se triangulan
        // (solo si la API no da las secciones)
        // ====================================================================

        private void ExportarObraLineal(C3D.Corridor obra, Contexto cx, Transaction tr)
        {
            string nombre = obra.Name;
            int n = 0;
            string error = null;

            // 0) Por secciones: una pieza por capa de subensamblaje, con PK y volumen por prismoides
            try
            {
                if (ExportarObraPorSecciones(obra, cx, tr) > 0)
                {
                    return;
                }
            }
            catch (System.Exception ex)
            {
                if (_avisos.Count < 15)
                {
                    _avisos.Add("Obra lineal \"" + nombre + "\" por secciones: " + MensajeReal(ex) + ". Se usan sus sólidos.");
                }
            }

            // 1) A un dibujo temporal en memoria (el dibujo actual no se toca)
            try
            {
                using (Database tmp = new Database(true, true))
                {
                    if (ExportarSolidosObra(obra, tmp))
                    {
                        n = LeerSolidosObra(tmp, obra, nombre, cx);
                    }
                    else
                    {
                        error = "esta versión de Civil 3D no ofrece ExportSolids en la API";
                    }
                }
            }
            catch (System.Exception ex)
            {
                error = MensajeReal(ex);
            }

            // 2) Si no deja usar otro dibujo: en este, dentro de una transacción que luego se deshace
            if (n == 0 && error != null && !error.StartsWith("esta versión", StringComparison.Ordinal))
            {
                Database db = obra.Database;
                try
                {
                    using (Transaction t2 = db.TransactionManager.StartTransaction())
                    {
                        HashSet<ObjectId> antes = IdsEspacioModelo(db, t2);
                        if (ExportarSolidosObra(obra, db))
                        {
                            BlockTable bt = (BlockTable)t2.GetObject(db.BlockTableId, OpenMode.ForRead);
                            BlockTableRecord ms = (BlockTableRecord)t2.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                            foreach (ObjectId id in ms)
                            {
                                if (antes.Contains(id))
                                {
                                    continue;
                                }
                                AcEntity sol = t2.GetObject(id, OpenMode.ForRead) as AcEntity;
                                if (sol != null && EmitirSolidoObra(sol, obra, nombre, cx, t2, ++n) == false)
                                {
                                    n--;
                                }
                            }
                            error = null;
                        }
                        t2.Abort();   // los solidos creados desaparecen
                    }
                }
                catch (System.Exception ex)
                {
                    error = MensajeReal(ex);
                }
            }

            if (n == 0)
            {
                Omitir("Obra lineal sin sólidos");
                if (_avisos.Count < 15)
                {
                    _avisos.Add("Obra lineal \"" + nombre + "\": no se pudieron sacar los sólidos"
                        + (error != null ? " (" + error + ")" : "") + ". Alternativa: \"Extraer sólidos de obra lineal\" y volver a exportar.");
                }
            }
        }

        // Llama a Corridor.ExportSolids(ExportCorridorSolidsParams, Database) por reflexion: si la firma
        // cambia entre versiones, el addin sigue compilando y avisa en vez de fallar.
        private static bool ExportarSolidosObra(C3D.Corridor obra, Database destino)
        {
            Type tObra = obra.GetType();
            MethodInfo mi = null;
            foreach (MethodInfo m in tObra.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "ExportSolids")
                {
                    continue;
                }
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 2 && ps[1].ParameterType == typeof(Database))
                {
                    mi = m;
                    break;
                }
            }
            if (mi == null)
            {
                return false;
            }
            Type tParams = mi.GetParameters()[0].ParameterType;
            object parametros = Activator.CreateInstance(tParams);
            PonerProp(parametros, "ExportShapes", true);
            PonerProp(parametros, "ExportLinks", false);
            mi.Invoke(obra, new object[] { parametros, destino });
            return true;
        }

        private int LeerSolidosObra(Database tmp, C3D.Corridor obra, string nombre, Contexto cx)
        {
            int n = 0;
            using (Transaction t = tmp.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)t.GetObject(tmp.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)t.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    AcEntity sol = t.GetObject(id, OpenMode.ForRead) as AcEntity;
                    if (sol != null && EmitirSolidoObra(sol, obra, nombre, cx, t, n + 1))
                    {
                        n++;
                    }
                }
                t.Commit();
            }
            return n;
        }

        private bool EmitirSolidoObra(AcEntity sol, C3D.Corridor obra, string nombre, Contexto cx, Transaction t, int indice)
        {
            Malla m = new Malla(_k);
            MallarEntidad(sol, cx.Xf, m);
            if (m.Triangulos == 0)
            {
                return false;
            }
            Dictionary<string, string> extra = new Dictionary<string, string>();
            ConjuntosDePropiedades(sol, t, extra);
            // Tipo: el codigo de la forma si viene en los conjuntos de propiedades; si no, la capa del solido
            string tipo = BuscarValor(extra, "shape code", "código de forma", "codigo de forma", "shape name", "nombre de forma", "code", "código", "codigo");
            if (string.IsNullOrEmpty(tipo))
            {
                tipo = sol.Layer;
            }
            Dictionary<string, string> d = FilaBase(obra, cx, "Obras lineales", nombre, tipo);
            d["ElementId"] = "";   // se pone en Emitir
            MedidasSolido(d, m);
            Campo(d, "Obra lineal", nombre);
            Campo(d, "Capa del sólido", sol.Layer);
            foreach (KeyValuePair<string, string> kv in extra)
            {
                Campo(d, kv.Key, kv.Value);
            }
            Emitir(IdDe(obra, cx) + "-" + indice.ToString(INV), m, d, "Obras lineales");
            return true;
        }

        private static HashSet<ObjectId> IdsEspacioModelo(Database db, Transaction t)
        {
            HashSet<ObjectId> set = new HashSet<ObjectId>();
            BlockTable bt = (BlockTable)t.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)t.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                set.Add(id);
            }
            return set;
        }

        // ====================================================================
        // Redes de tuberias y redes a presion
        // ====================================================================

        private void ExportarPieza(AcEntity pieza, string categoria, Contexto cx, Transaction tr)
        {
            Malla m = new Malla(_k);

            // 1) El solido que Civil 3D dibuja para la pieza
            Solid3d cuerpo = Prop(pieza, "Solid3dBody") as Solid3d;
            if (cuerpo != null)
            {
                try
                {
                    MallarEntidad(cuerpo, cx.Xf, m);
                }
                finally
                {
                    if (cuerpo.ObjectId.IsNull)
                    {
                        cuerpo.Dispose();
                    }
                }
            }

            // 2) Si no hay solido: tubo como cilindro entre sus extremos, estructura como cilindro vertical
            if (m.Triangulos == 0)
            {
                MallaAproximada(pieza, cx.Xf, m);
            }
            if (m.Triangulos == 0)
            {
                Omitir(categoria + " sin geometría");
                return;
            }

            string familia = Texto(Prop(pieza, "PartFamilyName"));
            string tipo = Texto(Prop(pieza, "PartSizeName"));
            if (string.IsNullOrEmpty(tipo))
            {
                tipo = Texto(Prop(pieza, "Name"));
            }
            Dictionary<string, string> d = FilaBase(pieza, cx, categoria, familia, tipo);
            Campo(d, "Red", Texto(Prop(pieza, "NetworkName")));

            double lon = PrimerNumero(pieza, "Length3DCenterToCenter", "Length3D", "Length3DToInsideEdge", "Length2DCenterToCenter", "Length2D");
            if (double.IsNaN(lon) && (pieza is C3D.Pipe || pieza.GetType().Name == "PressurePipe"))
            {
                object a = Prop(pieza, "StartPoint"), b = Prop(pieza, "EndPoint");
                if (a is Point3d && b is Point3d)
                {
                    lon = ((Point3d)a).DistanceTo((Point3d)b);
                }
            }
            if (!double.IsNaN(lon) && lon > 0)
            {
                d[COL_LONG] = Num(lon * _k);
            }
            // Pozos y arquetas: la longitud es su profundidad (tapa - fondo), para medir por metro de altura
            if (pieza is C3D.Structure)
            {
                double tapa = PrimerNumero(pieza, "RimElevation"), fondo = PrimerNumero(pieza, "SumpElevation");
                if (!double.IsNaN(tapa) && !double.IsNaN(fondo) && tapa > fondo)
                {
                    d[COL_LONG] = Num((tapa - fondo) * _k);
                    Campo(d, "Profundidad (m)", ((tapa - fondo) * _k).ToString("0.###", ES));
                }
            }
            MedidasSolido(d, m);

            DatosDePieza(pieza, d);
            PropiedadesCivil(pieza, d);
            ConjuntosDePropiedades(pieza, tr, d);
            Emitir(IdDe(pieza, cx), m, d, categoria);
        }

        private void MallaAproximada(AcEntity pieza, Matrix3d xf, Malla m)
        {
            object a = Prop(pieza, "StartPoint"), b = Prop(pieza, "EndPoint");
            if (a is Point3d && b is Point3d)
            {
                double diam = PrimerNumero(pieza, "OuterDiameterOrWidth", "OuterDiameter", "InnerDiameterOrWidth", "InnerDiameter");
                if (double.IsNaN(diam) || diam <= 0)
                {
                    diam = 0.2 / _k;
                }
                m.Cilindro(((Point3d)a).TransformBy(xf), ((Point3d)b).TransformBy(xf), diam / 2.0, 16);
                return;
            }
            object pos = Prop(pieza, "Position");
            if (!(pos is Point3d))
            {
                pos = Prop(pieza, "Location");
            }
            if (pos is Point3d)
            {
                Point3d p = (Point3d)pos;
                double diam = PrimerNumero(pieza, "DiameterOrWidth", "InnerDiameterOrWidth", "OuterDiameterOrWidth");
                double tapa = PrimerNumero(pieza, "RimElevation");
                double fondo = PrimerNumero(pieza, "SumpElevation");
                if (double.IsNaN(diam) || diam <= 0)
                {
                    diam = 1.0 / _k;
                }
                if (double.IsNaN(tapa))
                {
                    tapa = p.Z;
                }
                if (double.IsNaN(fondo) || fondo >= tapa)
                {
                    fondo = tapa - 1.5 / _k;
                }
                m.Cilindro(new Point3d(p.X, p.Y, fondo).TransformBy(xf), new Point3d(p.X, p.Y, tapa).TransformBy(xf), diam / 2.0, 20);
            }
        }

        // Datos de la pieza del catalogo (diametro, material, espesor...)
        private void DatosDePieza(AcEntity pieza, Dictionary<string, string> d)
        {
            try
            {
                object datos = Prop(pieza, "PartData");
                if (datos == null)
                {
                    return;
                }
                MethodInfo mi = datos.GetType().GetMethod("GetAllDataFields", Type.EmptyTypes);
                IEnumerable campos = mi != null ? mi.Invoke(datos, null) as IEnumerable : null;
                if (campos == null)
                {
                    return;
                }
                foreach (object c in campos)
                {
                    string nombre = Texto(Prop(c, "Description"));
                    if (string.IsNullOrEmpty(nombre))
                    {
                        nombre = Texto(Prop(c, "Name"));
                    }
                    if (string.IsNullOrEmpty(nombre))
                    {
                        continue;
                    }
                    string valor = Texto(Prop(c, "Value"));
                    if (!string.IsNullOrEmpty(valor))
                    {
                        Campo(d, "Pieza: " + nombre, valor);
                    }
                }
            }
            catch
            {
            }
        }

        // ====================================================================
        // Bloques y referencias externas
        // ====================================================================

        private void ExportarBloque(BlockReference br, Contexto cx, Transaction tr)
        {
            if (cx.Profundidad > 8)
            {
                return;
            }
            BlockTableRecord btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null || btr.IsLayout)
            {
                return;
            }
            Matrix3d xf = cx.Xf * br.BlockTransform;

            // Referencia externa: su contenido objeto a objeto (con sus categorias de Civil 3D)
            if (btr.IsFromExternalReference || btr.IsFromOverlayReference)
            {
                if (!btr.IsResolved)
                {
                    if (_avisos.Count < 15)
                    {
                        _avisos.Add("Referencia externa sin cargar: " + btr.Name);
                    }
                    return;
                }
                Contexto hijo = new Contexto();
                hijo.Xf = xf;
                hijo.Prefijo = cx.Prefijo + br.Handle.ToString() + ".";
                hijo.RefExterna = string.IsNullOrEmpty(cx.RefExterna) ? btr.Name : cx.RefExterna + " > " + btr.Name;
                hijo.Profundidad = cx.Profundidad + 1;
                foreach (ObjectId id in btr)
                {
                    AcEntity e = null;
                    try
                    {
                        e = tr.GetObject(id, OpenMode.ForRead, false) as AcEntity;
                    }
                    catch
                    {
                        e = null;
                    }
                    if (e == null || !EsVisible(e))
                    {
                        continue;
                    }
                    try
                    {
                        Procesar(e, hijo, tr);
                    }
                    catch
                    {
                        Omitir(e.GetType().Name + " (error)");
                    }
                }
                return;
            }

            // Bloque normal: un objeto por insercion con toda su geometria 3D
            Malla m = new Malla(_k);
            RecogerBloque(btr, xf, m, tr, cx.Profundidad + 1);
            if (m.Triangulos == 0)
            {
                return;   // bloque 2D: no es un objeto del modelo
            }
            string nombre = btr.Name;
            try
            {
                if (br.IsDynamicBlock)
                {
                    BlockTableRecord din = tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    if (din != null)
                    {
                        nombre = din.Name;
                    }
                }
            }
            catch
            {
            }
            Dictionary<string, string> d = FilaBase(br, cx, "Bloques", nombre, br.Layer);
            d["Nombre"] = nombre;
            MedidasSolido(d, m);
            try
            {
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    AttributeReference ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (ar != null && !string.IsNullOrEmpty(ar.Tag))
                    {
                        Campo(d, "Atributo: " + ar.Tag, ar.TextString);
                    }
                }
            }
            catch
            {
            }
            ConjuntosDePropiedades(br, tr, d);
            Emitir(IdDe(br, cx), m, d, "Bloques");
        }

        private void RecogerBloque(BlockTableRecord btr, Matrix3d xf, Malla m, Transaction tr, int profundidad)
        {
            if (profundidad > 8)
            {
                return;
            }
            foreach (ObjectId id in btr)
            {
                AcEntity e = null;
                try
                {
                    e = tr.GetObject(id, OpenMode.ForRead, false) as AcEntity;
                }
                catch
                {
                    e = null;
                }
                if (e == null || !EsVisible(e))
                {
                    continue;
                }
                BlockReference anidado = e as BlockReference;
                if (anidado != null)
                {
                    BlockTableRecord b2 = tr.GetObject(anidado.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    if (b2 != null && !b2.IsLayout && !b2.IsFromExternalReference && !(anidado is Table))
                    {
                        RecogerBloque(b2, xf * anidado.BlockTransform, m, tr, profundidad + 1);
                    }
                    continue;
                }
                if (CategoriaAutoCad(e) != null)
                {
                    MallarEntidad(e, xf, m);
                }
            }
        }

        // ====================================================================
        // Triangulacion de solidos, superficies y mallas de AutoCAD
        // ====================================================================

        private void MallarEntidad(AcEntity ent, Matrix3d xf, Malla m)
        {
            int antes = m.Triangulos;

            // Caras 3D y mallas policara: directo
            AcFace cara = ent as AcFace;
            if (cara != null)
            {
                List<Point3d> pts = new List<Point3d>(4);
                for (int i = 0; i < 4; i++)
                {
                    Point3d p = cara.GetVertexAt((short)i).TransformBy(xf);
                    if (pts.Count == 0 || !p.IsEqualTo(pts[pts.Count - 1]))
                    {
                        pts.Add(p);
                    }
                }
                if (pts.Count > 3 && pts[pts.Count - 1].IsEqualTo(pts[0]))
                {
                    pts.RemoveAt(pts.Count - 1);
                }
                m.Poligono(pts);
                return;
            }

            // 1) Como el comando MALLASUAVE: SubDMesh.GetObjectMesh(entidad, MeshFaceterData)
            if (MallaPorFacetador(ent, xf, m) && m.Triangulos > antes)
            {
                return;
            }

            // 2) Mallas policara leyendo sus vertices y caras
            PolyFaceMesh pfm = ent as PolyFaceMesh;
            if (pfm != null)
            {
                MallaPolicara(pfm, xf, m);
                return;
            }

            // 3) SubDMesh leyendo sus vertices y caras
            if (ent is SubDMesh && MallaDeDatos(Prop(ent, "Vertices"), Prop(ent, "FaceArray"), xf, m) && m.Triangulos > antes)
            {
                return;
            }

            // 4) Solidos y superficies por Brep (BoundaryRepresentation)
            if (ent is Solid3d || ent is Body || ent is AcSurface)
            {
                MallaPorBrep(ent, xf, m);
            }
        }

        private static Type _tFacetador;
        private static MethodInfo _miObjectMesh;
        private static bool _facetadorBuscado;

        private bool MallaPorFacetador(AcEntity ent, Matrix3d xf, Malla m)
        {
            if (!_facetadorBuscado)
            {
                _facetadorBuscado = true;
                try
                {
                    Assembly a = typeof(SubDMesh).Assembly;
                    _tFacetador = a.GetType("Autodesk.AutoCAD.DatabaseServices.MeshFaceterData");
                    if (_tFacetador != null)
                    {
                        _miObjectMesh = typeof(SubDMesh).GetMethod("GetObjectMesh", BindingFlags.Public | BindingFlags.Static,
                            null, new Type[] { typeof(AcEntity), _tFacetador }, null);
                    }
                }
                catch
                {
                    _miObjectMesh = null;
                }
            }
            if (_miObjectMesh == null)
            {
                return false;
            }
            try
            {
                object fd = CrearFacetador();
                if (fd == null)
                {
                    return false;
                }
                object datos = _miObjectMesh.Invoke(null, new object[] { ent, fd });
                if (datos == null)
                {
                    return false;
                }
                bool ok = MallaDeDatos(Prop(datos, "VertexArray"), Prop(datos, "FaceArray"), xf, m);
                IDisposable disp = datos as IDisposable;
                if (disp != null)
                {
                    disp.Dispose();
                }
                return ok;
            }
            catch
            {
                return false;
            }
        }

        private static object CrearFacetador()
        {
            object fd = null;
            try
            {
                fd = Activator.CreateInstance(_tFacetador);
            }
            catch
            {
                fd = null;
            }
            if (fd == null)
            {
                // Constructor con parametros (longitud maxima de arista, desviacion de normal, tipo de malla...)
                foreach (ConstructorInfo ci in _tFacetador.GetConstructors())
                {
                    ParameterInfo[] ps = ci.GetParameters();
                    if (ps.Length != 8)
                    {
                        continue;
                    }
                    try
                    {
                        fd = ci.Invoke(new object[] { 0.0, 15 * Math.PI / 180, 2, 2, 15, 5, 5, 0 }
                            .Select((v, i) => Convert.ChangeType(v, ps[i].ParameterType, INV)).ToArray());
                        break;
                    }
                    catch
                    {
                        fd = null;
                    }
                }
            }
            if (fd != null)
            {
                // Malla de triangulos y suavizado 0 (la malla sigue la forma real del solido)
                PonerProp(fd, "FaceterMeshType", 2);
                PonerProp(fd, "FaceterDevNormal", 15 * Math.PI / 180);
            }
            return fd;
        }

        // Point3dCollection de vertices + Int32Collection de caras: [n, i0, i1, ... , n, i0, ...]
        private static bool MallaDeDatos(object vertices, object caras, Matrix3d xf, Malla m)
        {
            Point3dCollection vs = vertices as Point3dCollection;
            IEnumerable fs = caras as IEnumerable;
            if (vs == null || fs == null || vs.Count == 0)
            {
                return false;
            }
            Point3d[] pv = new Point3d[vs.Count];
            for (int i = 0; i < vs.Count; i++)
            {
                pv[i] = vs[i].TransformBy(xf);
            }
            List<int> f = new List<int>();
            foreach (object o in fs)
            {
                f.Add(Convert.ToInt32(o, INV));
            }
            int antes = m.Triangulos;
            List<Point3d> poli = new List<Point3d>(8);
            int k = 0;
            while (k < f.Count)
            {
                int n = f[k++];
                if (n < 3 || k + n > f.Count)
                {
                    break;
                }
                poli.Clear();
                for (int j = 0; j < n; j++)
                {
                    int vi = f[k + j];
                    if (vi >= 0 && vi < pv.Length)
                    {
                        poli.Add(pv[vi]);
                    }
                }
                k += n;
                m.Poligono(poli);
            }
            return m.Triangulos > antes;
        }

        private static void MallaPolicara(PolyFaceMesh pfm, Matrix3d xf, Malla m)
        {
            Transaction tr = pfm.Database.TransactionManager.TopTransaction;
            if (tr == null)
            {
                return;
            }
            List<Point3d> vs = new List<Point3d>();
            List<int[]> caras = new List<int[]>();
            foreach (ObjectId id in pfm)
            {
                DBObject o = tr.GetObject(id, OpenMode.ForRead);
                PolyFaceMeshVertex v = o as PolyFaceMeshVertex;
                if (v != null)
                {
                    vs.Add(v.Position.TransformBy(xf));
                    continue;
                }
                FaceRecord fr = o as FaceRecord;
                if (fr != null)
                {
                    int[] c = new int[4];
                    for (int i = 0; i < 4; i++)
                    {
                        c[i] = Math.Abs(Convert.ToInt32(fr.GetVertexAt((short)i), INV));   // negativo = arista invisible
                    }
                    caras.Add(c);
                }
            }
            List<Point3d> poli = new List<Point3d>(4);
            foreach (int[] c in caras)
            {
                poli.Clear();
                foreach (int i in c)
                {
                    if (i >= 1 && i <= vs.Count && (poli.Count == 0 || !poli[poli.Count - 1].IsEqualTo(vs[i - 1])))
                    {
                        poli.Add(vs[i - 1]);
                    }
                }
                if (poli.Count > 3 && poli[poli.Count - 1].IsEqualTo(poli[0]))
                {
                    poli.RemoveAt(poli.Count - 1);
                }
                m.Poligono(poli);
            }
        }

        // Brep -> Mesh2d (todo por reflexion para no depender de la firma exacta de cada version)
        private static void MallaPorBrep(AcEntity ent, Matrix3d xf, Malla m)
        {
            List<IDisposable> basura = new List<IDisposable>();
            try
            {
                Assembly a = typeof(Database).Assembly;
                Type tBrep = a.GetType("Autodesk.AutoCAD.BoundaryRepresentation.Brep");
                Type tCtrl = a.GetType("Autodesk.AutoCAD.BoundaryRepresentation.Mesh2dControl");
                Type tFiltro = a.GetType("Autodesk.AutoCAD.BoundaryRepresentation.Mesh2dFilter");
                Type tMalla = a.GetType("Autodesk.AutoCAD.BoundaryRepresentation.Mesh2d");
                if (tBrep == null || tCtrl == null || tFiltro == null || tMalla == null)
                {
                    return;
                }
                object brep = Activator.CreateInstance(tBrep, new object[] { ent });
                basura.Add(brep as IDisposable);
                object ctrl = Activator.CreateInstance(tCtrl);
                basura.Add(ctrl as IDisposable);
                object filtro = Activator.CreateInstance(tFiltro);
                basura.Add(filtro as IDisposable);
                MethodInfo insertar = null;
                foreach (MethodInfo mi in tFiltro.GetMethods())
                {
                    ParameterInfo[] ps = mi.GetParameters();
                    if (mi.Name == "Insert" && ps.Length == 2 && ps[1].ParameterType == tCtrl && ps[0].ParameterType.IsAssignableFrom(tBrep))
                    {
                        insertar = mi;
                        break;
                    }
                }
                if (insertar == null)
                {
                    return;
                }
                insertar.Invoke(filtro, new object[] { brep, ctrl });
                object malla = Activator.CreateInstance(tMalla, new object[] { filtro });
                basura.Add(malla as IDisposable);
                IEnumerable elementos = Prop(malla, "Element2ds") as IEnumerable;
                if (elementos == null)
                {
                    return;
                }
                List<Point3d> poli = new List<Point3d>(4);
                foreach (object el in elementos)
                {
                    poli.Clear();
                    IEnumerable nodos = Prop(el, "Nodes") as IEnumerable;
                    if (nodos != null)
                    {
                        foreach (object nd in nodos)
                        {
                            object p = Prop(nd, "Point");
                            if (p is Point3d)
                            {
                                poli.Add(((Point3d)p).TransformBy(xf));
                            }
                            IDisposable dn = nd as IDisposable;
                            if (dn != null)
                            {
                                dn.Dispose();
                            }
                        }
                    }
                    IDisposable de = el as IDisposable;
                    if (de != null)
                    {
                        de.Dispose();
                    }
                    m.Poligono(poli);
                }
            }
            catch
            {
            }
            finally
            {
                for (int i = basura.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (basura[i] != null)
                        {
                            basura[i].Dispose();
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        // ====================================================================
        // Datos (fila del Excel)
        // ====================================================================

        private Dictionary<string, string> FilaBase(AcEntity ent, Contexto cx, string categoria, string familia, string tipo)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            d["Categoría"] = categoria;
            d["Familia"] = string.IsNullOrEmpty(familia) ? categoria : familia;
            d["Tipo"] = string.IsNullOrEmpty(tipo) ? ent.Layer : tipo;
            d["Nivel"] = "";
            d["Capa"] = ent.Layer;
            string nombre = Texto(Prop(ent, "Name"));
            if (!string.IsNullOrEmpty(nombre))
            {
                d["Nombre"] = nombre;
            }
            string desc = Texto(Prop(ent, "Description"));
            if (!string.IsNullOrEmpty(desc))
            {
                d["Descripción"] = desc;
            }
            string estilo = Texto(Prop(ent, "StyleName"));
            if (!string.IsNullOrEmpty(estilo))
            {
                d["Estilo"] = estilo;
            }
            try
            {
                if (!string.IsNullOrEmpty(ent.Material) && !ent.Material.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)
                    && !ent.Material.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
                {
                    Campo(d, "Material", ent.Material);
                }
            }
            catch
            {
            }
            if (!string.IsNullOrEmpty(cx.RefExterna))
            {
                d["Referencia externa"] = cx.RefExterna;
            }
            Campo(d, "Tipo de objeto", ent.GetType().Name);
            Campo(d, "Dibujo", _nombreDibujo);
            return d;
        }

        // Las cuatro unidades para todo lo que tiene volumen: ud siempre; ml = longitud (la propia del objeto
        // si la tiene, si no su dimension mayor en planta); m2 = area en planta (huella); m3 = volumen
        private void MedidasSolido(Dictionary<string, string> d, Malla m)
        {
            string v;
            // Volumen solo si la malla es cerrada (una superficie o malla abierta no encierra volumen)
            if ((!d.TryGetValue(COL_VOL, out v) || string.IsNullOrEmpty(v)) && m.Perimetro() < 0.001)
            {
                d[COL_VOL] = Num(m.Volumen());
            }
            if (!d.TryGetValue(COL_AREA, out v) || string.IsNullOrEmpty(v))
            {
                d[COL_AREA] = Num(m.AreaHuella());
            }
            if (!d.TryGetValue(COL_LONG, out v) || string.IsNullOrEmpty(v))
            {
                d[COL_LONG] = Num(m.LongitudPrincipal());
            }
            Campo(d, "Superficie exterior (m²)", m.Area().ToString("0.###", ES));
        }

        private void Campo(Dictionary<string, string> d, string columna, string valor)
        {
            if (string.IsNullOrEmpty(columna) || valor == null)
            {
                return;
            }
            d[columna] = valor;
            _columnas.Add(columna);
        }

        // Nombres en castellano para las propiedades mas habituales de la API de Civil 3D
        private static readonly Dictionary<string, string> TRADUCCION = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "NetworkName", "Red" }, { "PartFamilyName", "Familia de pieza" }, { "PartSizeName", "Tamaño de pieza" },
            { "InnerDiameterOrWidth", "Diámetro o ancho interior" }, { "OuterDiameterOrWidth", "Diámetro o ancho exterior" },
            { "InnerHeight", "Altura interior" }, { "OuterHeight", "Altura exterior" }, { "WallThickness", "Espesor de pared" },
            { "Slope", "Pendiente" }, { "Length2D", "Longitud 2D" }, { "Length3D", "Longitud 3D" },
            { "Length2DCenterToCenter", "Longitud 2D (centro a centro)" }, { "Length3DCenterToCenter", "Longitud 3D (centro a centro)" },
            { "StartStation", "PK inicial" }, { "EndStation", "PK final" }, { "RimElevation", "Cota de tapa" },
            { "SumpElevation", "Cota de fondo" }, { "SumpDepth", "Profundidad de fondo" }, { "DiameterOrWidth", "Diámetro o ancho" },
            { "StartStructureName", "Estructura inicial" }, { "EndStructureName", "Estructura final" },
            { "ReferenceSurfaceName", "Superficie de referencia" }, { "ReferenceAlignmentName", "Eje de referencia" },
            { "Material", "Material" }, { "Elevation", "Cota" }, { "Rotation", "Rotación" }, { "Height", "Altura" },
            { "Width", "Ancho" }, { "Length", "Longitud" }, { "Thickness", "Espesor" }, { "Cover", "Recubrimiento" },
            { "MinimumElevation", "Cota mínima" }, { "MaximumElevation", "Cota máxima" }, { "IsReferenceObject", "Es referencia" },
            { "RegionLockMode", "Bloqueo de regiones" }, { "CodeSetStyleName", "Estilo de códigos" }
        };

        private static readonly HashSet<string> NO_EXPORTAR = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "Description", "StyleName", "Handle", "ObjectId", "OwnerId", "Id", "Layer", "Color", "Linetype",
            "IsWriteEnabled", "IsReadEnabled", "IsNotifyEnabled", "IsModified", "IsErased", "IsNewObject", "IsTransactionResident",
            "IsUndoing", "IsCancelling", "IsReallyClosing", "IsPersistent", "AutoDelete", "UnmanagedObject", "IsDisposed"
        };

        // Todas las propiedades simples (texto, numero, si/no, enumerado) que define Civil 3D para el objeto:
        // es el equivalente a "todos los parametros" de Revit.
        private void PropiedadesCivil(object obj, Dictionary<string, string> d)
        {
            Type t = obj.GetType();
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    Type decl = p.DeclaringType;
                    if (decl == null || decl.Namespace == null || !decl.Namespace.StartsWith("Autodesk.Civil", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!p.CanRead || p.GetIndexParameters().Length > 0 || NO_EXPORTAR.Contains(p.Name))
                    {
                        continue;
                    }
                    Type pt = p.PropertyType;
                    if (!(pt == typeof(string) || pt == typeof(double) || pt == typeof(float) || pt == typeof(int) || pt == typeof(short)
                        || pt == typeof(long) || pt == typeof(uint) || pt == typeof(bool) || pt.IsEnum))
                    {
                        continue;
                    }
                    object v = p.GetValue(obj, null);
                    string s = Texto(v);
                    if (string.IsNullOrEmpty(s))
                    {
                        continue;
                    }
                    string col;
                    if (!TRADUCCION.TryGetValue(p.Name, out col))
                    {
                        col = p.Name;
                    }
                    if (!d.ContainsKey(col))
                    {
                        Campo(d, col, s);
                    }
                }
                catch
                {
                }
            }
        }

        // Conjuntos de propiedades (Property Sets de AutoCAD/Civil), por reflexion sobre AecPropDataMgd
        private static Type _tPropData;
        private static bool _propDataBuscado;

        private void ConjuntosDePropiedades(DBObject obj, Transaction tr, Dictionary<string, string> d)
        {
            if (!_propDataBuscado)
            {
                _propDataBuscado = true;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (string.Equals(a.GetName().Name, "AecPropDataMgd", StringComparison.OrdinalIgnoreCase))
                        {
                            _tPropData = a.GetType("Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices");
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            if (_tPropData == null)
            {
                return;
            }
            try
            {
                MethodInfo mi = _tPropData.GetMethod("GetPropertySets", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(DBObject) }, null);
                if (mi == null)
                {
                    return;
                }
                ObjectIdCollection sets = mi.Invoke(null, new object[] { obj }) as ObjectIdCollection;
                if (sets == null)
                {
                    return;
                }
                foreach (ObjectId psId in sets)
                {
                    DBObject ps = tr.GetObject(psId, OpenMode.ForRead);
                    string conjunto = Texto(Prop(ps, "PropertySetDefinitionName"));
                    Dictionary<int, string> nombres = new Dictionary<int, string>();
                    object defIdObj = Prop(ps, "PropertySetDefinition");
                    if (defIdObj is ObjectId && !((ObjectId)defIdObj).IsNull)
                    {
                        DBObject def = tr.GetObject((ObjectId)defIdObj, OpenMode.ForRead);
                        IEnumerable defs = Prop(def, "Definitions") as IEnumerable;
                        if (defs != null)
                        {
                            foreach (object pd in defs)
                            {
                                object idp = Prop(pd, "Id");
                                string nom = Texto(Prop(pd, "Name"));
                                if (idp != null && !string.IsNullOrEmpty(nom))
                                {
                                    nombres[Convert.ToInt32(idp, INV)] = nom;
                                }
                            }
                        }
                    }
                    IEnumerable datos = Prop(ps, "PropertySetData") as IEnumerable;
                    if (datos == null)
                    {
                        continue;
                    }
                    foreach (object dt in datos)
                    {
                        object idp = Prop(dt, "Id");
                        string nom = null;
                        if (idp != null)
                        {
                            nombres.TryGetValue(Convert.ToInt32(idp, INV), out nom);
                        }
                        if (string.IsNullOrEmpty(nom))
                        {
                            nom = Texto(Prop(dt, "FieldBucketId"));
                        }
                        if (string.IsNullOrEmpty(nom))
                        {
                            continue;
                        }
                        object val = null;
                        MethodInfo gd = dt.GetType().GetMethod("GetData", Type.EmptyTypes);
                        if (gd != null)
                        {
                            val = gd.Invoke(dt, null);
                        }
                        string s = Texto(val);
                        if (!string.IsNullOrEmpty(s))
                        {
                            Campo(d, (string.IsNullOrEmpty(conjunto) ? "" : conjunto + ": ") + nom, s);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static string BuscarValor(Dictionary<string, string> d, params string[] claves)
        {
            foreach (string c in claves)
            {
                foreach (KeyValuePair<string, string> kv in d)
                {
                    string k = kv.Key;
                    int dp = k.LastIndexOf(": ", StringComparison.Ordinal);
                    string campo = dp >= 0 ? k.Substring(dp + 2) : k;
                    if (string.Equals(campo.Trim(), c, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kv.Value))
                    {
                        return kv.Value;
                    }
                }
            }
            return null;
        }

        // ====================================================================
        // Escritura del elemento
        // ====================================================================

        private string IdDe(AcEntity ent, Contexto cx)
        {
            return cx.Prefijo + ent.Handle.ToString();
        }

        private void Emitir(string id, Malla m, Dictionary<string, string> d, string categoria)
        {
            // Id unico y sin caracteres que haya que escapar en XML
            StringBuilder limpio = new StringBuilder(id.Length);
            foreach (char ch in id)
            {
                limpio.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' || ch == '_' ? ch : '_');
            }
            string baseId = limpio.ToString();
            string uid = baseId;
            int n = 2;
            while (!_idsUsados.Add(uid))
            {
                uid = baseId + "_" + (n++).ToString(INV);
            }

            EscribirGeometria(uid, m);
            _ids.Add(uid);
            _triangulos += m.Triangulos;

            d["ElementId"] = uid;
            d["UniqueId"] = _nombreDibujo + ":" + uid;
            _filas.Add(d);
            int c;
            _porCategoria.TryGetValue(categoria, out c);
            _porCategoria[categoria] = c + 1;
        }

        private void Omitir(string que)
        {
            int c;
            _omitidos.TryGetValue(que, out c);
            _omitidos[que] = c + 1;
        }

        // ====================================================================
        // DAE (Collada), mismo formato que la macro de Revit
        // ====================================================================

        private void EscribirCabeceraDae()
        {
            _dae.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            _dae.WriteLine("<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">");
            _dae.WriteLine("  <asset>");
            _dae.WriteLine("    <contributor><authoring_tool>ExportadorVisor (Civil 3D) " + VERSION + "</authoring_tool></contributor>");
            string ahora = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", INV);
            _dae.WriteLine("    <created>" + ahora + "</created><modified>" + ahora + "</modified>");
            _dae.WriteLine("    <subject>ORIGEN " + _ox.ToString("0.###", INV) + " " + _oy.ToString("0.###", INV) + " "
                + _oz.ToString("0.###", INV) + " SISTEMA compartidas</subject>");
            _dae.WriteLine("    <unit name=\"meter\" meter=\"1\"/>");
            _dae.WriteLine("    <up_axis>Z_UP</up_axis>");
            _dae.WriteLine("  </asset>");
            _dae.WriteLine("  <library_effects><effect id=\"fx\"><profile_COMMON><technique sid=\"common\"><lambert><diffuse><color>0.8 0.8 0.8 1</color></diffuse></lambert></technique></profile_COMMON></effect></library_effects>");
            _dae.WriteLine("  <library_materials><material id=\"mat\" name=\"mat\"><instance_effect url=\"#fx\"/></material></library_materials>");
        }

        private void EscribirGeometria(string id, Malla m)
        {
            string g = "g" + id;
            int nv = m.Vertices;
            _dae.WriteLine("    <geometry id=\"" + g + "\" name=\"" + id + "\"><mesh>");
            _dae.Write("      <source id=\"" + g + "-p\"><float_array id=\"" + g + "-pa\" count=\"" + (nv * 3).ToString(INV) + "\">");
            StringBuilder sb = new StringBuilder(64 * 1024);
            List<double> v = m.V;
            for (int i = 0; i < nv; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append((v[3 * i] - _ox).ToString("0.####", INV)).Append(' ')
                  .Append((v[3 * i + 1] - _oy).ToString("0.####", INV)).Append(' ')
                  .Append((v[3 * i + 2] - _oz).ToString("0.####", INV));
                if (sb.Length > 60000)
                {
                    _dae.Write(sb.ToString());
                    sb.Clear();
                }
            }
            _dae.Write(sb.ToString());
            sb.Clear();
            _dae.WriteLine("</float_array><technique_common><accessor source=\"#" + g + "-pa\" count=\"" + nv.ToString(INV)
                + "\" stride=\"3\"><param name=\"X\" type=\"float\"/><param name=\"Y\" type=\"float\"/><param name=\"Z\" type=\"float\"/></accessor></technique_common></source>");
            _dae.WriteLine("      <vertices id=\"" + g + "-v\"><input semantic=\"POSITION\" source=\"#" + g + "-p\"/></vertices>");
            _dae.Write("      <triangles material=\"mat\" count=\"" + m.Triangulos.ToString(INV) + "\"><input semantic=\"VERTEX\" source=\"#" + g + "-v\" offset=\"0\"/><p>");
            List<int> t = m.T;
            for (int i = 0; i < t.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(t[i].ToString(INV));
                if (sb.Length > 60000)
                {
                    _dae.Write(sb.ToString());
                    sb.Clear();
                }
            }
            _dae.Write(sb.ToString());
            _dae.WriteLine("</p></triangles>");
            _dae.WriteLine("    </mesh></geometry>");
        }

        private void EscribirEscena()
        {
            _dae.WriteLine("  <library_visual_scenes><visual_scene id=\"Scene\" name=\"Scene\">");
            foreach (string id in _ids)
            {
                _dae.WriteLine("    <node id=\"n" + id + "\" name=\"" + id + "\"><instance_geometry url=\"#g" + id
                    + "\"><bind_material><technique_common><instance_material symbol=\"mat\" target=\"#mat\"/></technique_common></bind_material></instance_geometry></node>");
            }
            _dae.WriteLine("  </visual_scene></library_visual_scenes>");
            _dae.WriteLine("  <scene><instance_visual_scene url=\"#Scene\"/></scene>");
            _dae.WriteLine("</COLLADA>");
        }

        // ====================================================================
        // XLSX (mismo escritor que la macro de Revit, sin Excel instalado)
        // ====================================================================

        private static void EscribirXlsx(string ruta, List<string> columnas, List<Dictionary<string, string>> filas, List<string[]> ejes)
        {
            if (File.Exists(ruta))
            {
                File.Delete(ruta);
            }
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
                    "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "</Types>");
                EntradaZip(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                EntradaZip(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"Elementos\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Ejes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
                EntradaZip(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
                    "</Relationships>");

                ZipArchiveEntry hoja = zip.CreateEntry("xl/worksheets/sheet1.xml");
                using (StreamWriter w = new StreamWriter(hoja.Open(), new UTF8Encoding(false)))
                {
                    w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                    w.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                    w.Write("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                    w.Write("<sheetData>");
                    HashSet<int> numericas = new HashSet<int> { columnas.IndexOf(COL_LONG), columnas.IndexOf(COL_AREA), columnas.IndexOf(COL_AREA_PLANTA), columnas.IndexOf(COL_VOL) };
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

                // Hoja "Ejes": Eje, PK, X, Y, Z (m, coordenadas del dibujo) y de donde sale la cota
                ZipArchiveEntry hoja2 = zip.CreateEntry("xl/worksheets/sheet2.xml");
                using (StreamWriter w = new StreamWriter(hoja2.Open(), new UTF8Encoding(false)))
                {
                    w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                    w.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
                    EscribirFila(w, 1, new List<string> { "Eje", "PK", "X", "Y", "Z", "Cota de" }, null);
                    HashSet<int> num = new HashSet<int> { 1, 2, 3, 4 };
                    int r = 2;
                    if (ejes != null)
                    {
                        foreach (string[] fila in ejes)
                        {
                            EscribirFila(w, r++, new List<string>(fila), num);
                        }
                    }
                    w.Write("</sheetData></worksheet>");
                }
            }
        }

        private static void EscribirFila(StreamWriter w, int r, List<string> valores, HashSet<int> numericas)
        {
            w.Write("<row r=\"" + r.ToString(INV) + "\">");
            for (int i = 0; i < valores.Count; i++)
            {
                string v = valores[i];
                if (string.IsNullOrEmpty(v))
                {
                    continue;
                }
                if (numericas != null && numericas.Contains(i))
                {
                    w.Write("<c r=\"" + LetraColumna(i) + r.ToString(INV) + "\"><v>" + v + "</v></c>");
                    continue;
                }
                w.Write("<c r=\"" + LetraColumna(i) + r.ToString(INV) + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + EscaparXml(v) + "</t></is></c>");
            }
            w.Write("</row>");
        }

        private static void EntradaZip(ZipArchive zip, string nombre, string contenido)
        {
            ZipArchiveEntry e = zip.CreateEntry(nombre);
            using (StreamWriter w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
            {
                w.Write(contenido);
            }
        }

        private static string LetraColumna(int indice)
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

        private static string EscaparXml(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '&') sb.Append("&amp;");
                else if (ch == '<') sb.Append("&lt;");
                else if (ch == '>') sb.Append("&gt;");
                else if (ch == '"') sb.Append("&quot;");
                else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;   // no validos en XML
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        // ====================================================================
        // Utilidades
        // ====================================================================

        // Origen: mediana de los centros de los objetos, redondeada al metro (evita que un objeto
        // perdido muy lejos desplace el origen y reste precision al resto)
        private void CalcularOrigen(List<ObjectId> ids, Transaction tr)
        {
            List<double> xs = new List<double>(), ys = new List<double>(), zs = new List<double>();
            int paso = Math.Max(1, ids.Count / 5000);
            for (int i = 0; i < ids.Count; i += paso)
            {
                try
                {
                    AcEntity e = tr.GetObject(ids[i], OpenMode.ForRead, false) as AcEntity;
                    if (e == null || !EsVisible(e))
                    {
                        continue;
                    }
                    Extents3d ex = e.GeometricExtents;
                    xs.Add((ex.MinPoint.X + ex.MaxPoint.X) / 2);
                    ys.Add((ex.MinPoint.Y + ex.MaxPoint.Y) / 2);
                    zs.Add((ex.MinPoint.Z + ex.MaxPoint.Z) / 2);
                }
                catch
                {
                }
            }
            _ox = Math.Round(Mediana(xs) * _k);
            _oy = Math.Round(Mediana(ys) * _k);
            _oz = Math.Round(Mediana(zs) * _k);
        }

        private static double Mediana(List<double> v)
        {
            if (v.Count == 0)
            {
                return 0;
            }
            v.Sort();
            return v[v.Count / 2];
        }

        private static HashSet<ObjectId> CapasOcultas(Database db, Transaction tr)
        {
            HashSet<ObjectId> set = new HashSet<ObjectId>();
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                LayerTableRecord l = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                if (l != null && (l.IsOff || l.IsFrozen))
                {
                    set.Add(id);
                }
            }
            return set;
        }

        private bool EsVisible(AcEntity e)
        {
            try
            {
                return e.Visible && !_capasOcultas.Contains(e.LayerId);
            }
            catch
            {
                return true;
            }
        }

        private static double FactorMetros(Database db, Editor ed)
        {
            switch (db.Insunits)
            {
                case UnitsValue.Millimeters: return 0.001;
                case UnitsValue.Centimeters: return 0.01;
                case UnitsValue.Decimeters: return 0.1;
                case UnitsValue.Kilometers: return 1000.0;
                case UnitsValue.Inches: return 0.0254;
                case UnitsValue.Feet: return 0.3048;
                case UnitsValue.Meters: return 1.0;
                default:
                    ed.WriteMessage("\nUNIDADES del dibujo sin definir: se toman metros.");
                    return 1.0;
            }
        }

        private static string NombreDibujo(Document doc)
        {
            string s = Path.GetFileNameWithoutExtension(doc.Name);
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }
            return string.IsNullOrEmpty(s) ? "Civil3D" : s;
        }

        private static string MensajeReal(System.Exception ex)
        {
            TargetInvocationException tie = ex as TargetInvocationException;
            if (tie != null && tie.InnerException != null)
            {
                return tie.InnerException.Message;
            }
            return ex.Message;
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
            {
                return "";
            }
            return Math.Round(v, 4).ToString("0.####", INV);
        }

        private static string Texto(object v)
        {
            if (v == null)
            {
                return null;
            }
            if (v is string)
            {
                return ((string)v).Trim();
            }
            if (v is double)
            {
                double d = (double)v;
                return double.IsNaN(d) || double.IsInfinity(d) ? null : d.ToString("0.####", ES);
            }
            if (v is float)
            {
                return ((float)v).ToString("0.####", ES);
            }
            if (v is bool)
            {
                return (bool)v ? "Sí" : "No";
            }
            if (v is IFormattable)
            {
                return ((IFormattable)v).ToString(null, ES);
            }
            return v.ToString();
        }

        private static object Prop(object o, string nombre)
        {
            if (o == null)
            {
                return null;
            }
            try
            {
                PropertyInfo p = null;
                try
                {
                    p = o.GetType().GetProperty(nombre, BindingFlags.Public | BindingFlags.Instance);
                }
                catch (AmbiguousMatchException)
                {
                    p = o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == nombre);
                }
                if (p == null || !p.CanRead || p.GetIndexParameters().Length > 0)
                {
                    return null;
                }
                return p.GetValue(o, null);
            }
            catch
            {
                return null;
            }
        }

        private static void PonerProp(object o, string nombre, object valor)
        {
            try
            {
                PropertyInfo p = o.GetType().GetProperty(nombre, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    Type t = p.PropertyType;
                    object v = t.IsEnum ? Enum.ToObject(t, valor) : Convert.ChangeType(valor, t, INV);
                    p.SetValue(o, v, null);
                }
            }
            catch
            {
            }
        }

        private static double PrimerNumero(object o, params string[] nombres)
        {
            foreach (string n in nombres)
            {
                object v = Prop(o, n);
                if (v == null)
                {
                    continue;
                }
                try
                {
                    double d = Convert.ToDouble(v, INV);
                    if (!double.IsNaN(d) && !double.IsInfinity(d))
                    {
                        return d;
                    }
                }
                catch
                {
                }
            }
            return double.NaN;
        }
    }

    // ========================================================================
    // Malla triangulada en metros (coordenadas absolutas del dibujo)
    // ========================================================================
    internal sealed class Malla
    {
        public readonly List<double> V = new List<double>();
        public readonly List<int> T = new List<int>();
        private readonly double _k;
        private readonly Dictionary<(long, long, long, int, int, int), int> _mapa = new Dictionary<(long, long, long, int, int, int), int>();

        public Malla(double factorMetros)
        {
            _k = factorMetros;
        }

        public int Vertices
        {
            get { return V.Count / 3; }
        }

        public int Triangulos
        {
            get { return T.Count / 3; }
        }

        // Vertice compartido si coincide la posicion y la orientacion de la cara: las caras planas
        // quedan lisas y las aristas vivas (como las caras de Revit)
        private int Punto(Point3d p, int nx, int ny, int nz)
        {
            double x = p.X * _k, y = p.Y * _k, z = p.Z * _k;
            (long, long, long, int, int, int) clave = ((long)Math.Round(x * 1e4), (long)Math.Round(y * 1e4), (long)Math.Round(z * 1e4), nx, ny, nz);
            int i;
            if (_mapa.TryGetValue(clave, out i))
            {
                return i;
            }
            i = V.Count / 3;
            V.Add(x);
            V.Add(y);
            V.Add(z);
            _mapa[clave] = i;
            return i;
        }

        // Terrenos: los triangulos comparten vertices (sombreado suave)
        public void TrianguloCompartido(Point3d a, Point3d b, Point3d c)
        {
            int ia = Punto(a, 0, 0, 0), ib = Punto(b, 0, 0, 0), ic = Punto(c, 0, 0, 0);
            if (ia == ib || ib == ic || ia == ic)
            {
                return;
            }
            T.Add(ia);
            T.Add(ib);
            T.Add(ic);
        }

        // Cara plana convexa (abanico de triangulos)
        public void Poligono(IList<Point3d> pts)
        {
            if (pts == null || pts.Count < 3)
            {
                return;
            }
            // Normal de Newell
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Point3d a = pts[i], b = pts[(i + 1) % pts.Count];
                nx += (a.Y - b.Y) * (a.Z + b.Z);
                ny += (a.Z - b.Z) * (a.X + b.X);
                nz += (a.X - b.X) * (a.Y + b.Y);
            }
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-14)
            {
                return;
            }
            int qx = (int)Math.Round(nx / l * 60), qy = (int)Math.Round(ny / l * 60), qz = (int)Math.Round(nz / l * 60);
            int i0 = Punto(pts[0], qx, qy, qz);
            int prev = Punto(pts[1], qx, qy, qz);
            for (int i = 2; i < pts.Count; i++)
            {
                int cur = Punto(pts[i], qx, qy, qz);
                if (i0 != prev && prev != cur && i0 != cur)
                {
                    T.Add(i0);
                    T.Add(prev);
                    T.Add(cur);
                }
                prev = cur;
            }
        }

        public void Cilindro(Point3d a, Point3d b, double radio, int lados)
        {
            Vector3d eje = b - a;
            if (eje.Length < 1e-9 || radio <= 0)
            {
                return;
            }
            Vector3d u = eje.GetPerpendicularVector().GetNormal() * radio;
            Vector3d w = eje.GetNormal().CrossProduct(u.GetNormal()) * radio;
            Point3d[] pa = new Point3d[lados], pb = new Point3d[lados];
            for (int i = 0; i < lados; i++)
            {
                double ang = 2 * Math.PI * i / lados;
                Vector3d off = u * Math.Cos(ang) + w * Math.Sin(ang);
                pa[i] = a + off;
                pb[i] = b + off;
            }
            for (int i = 0; i < lados; i++)
            {
                int j = (i + 1) % lados;
                Poligono(new Point3d[] { pa[i], pa[j], pb[j], pb[i] });
            }
            Point3d[] tapaA = new Point3d[lados], tapaB = new Point3d[lados];
            for (int i = 0; i < lados; i++)
            {
                tapaA[i] = pa[lados - 1 - i];
                tapaB[i] = pb[i];
            }
            Poligono(tapaA);
            Poligono(tapaB);
        }

        public double Area()
        {
            double s = 0;
            for (int t = 0; t < T.Count; t += 3)
            {
                int a = T[t] * 3, b = T[t + 1] * 3, c = T[t + 2] * 3;
                double ux = V[b] - V[a], uy = V[b + 1] - V[a + 1], uz = V[b + 2] - V[a + 2];
                double vx = V[c] - V[a], vy = V[c + 1] - V[a + 1], vz = V[c + 2] - V[a + 2];
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                s += Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2;
            }
            return s;
        }

        public double AreaPlanta()
        {
            double s = 0;
            for (int t = 0; t < T.Count; t += 3)
            {
                int a = T[t] * 3, b = T[t + 1] * 3, c = T[t + 2] * 3;
                double ux = V[b] - V[a], uy = V[b + 1] - V[a + 1];
                double vx = V[c] - V[a], vy = V[c + 1] - V[a + 1];
                s += Math.Abs(ux * vy - uy * vx) / 2;
            }
            return s;
        }

        // Huella: area proyectada en planta (en un solido cerrado, la de las caras de arriba)
        public double AreaHuella()
        {
            double pos = 0, neg = 0;
            for (int t = 0; t < T.Count; t += 3)
            {
                int a = T[t] * 3, b = T[t + 1] * 3, c = T[t + 2] * 3;
                double s = ((V[b] - V[a]) * (V[c + 1] - V[a + 1]) - (V[b + 1] - V[a + 1]) * (V[c] - V[a])) / 2;
                if (s > 0) pos += s; else neg -= s;
            }
            return Math.Max(pos, neg);
        }

        // Dimension mayor en planta, en la direccion principal de los vertices (vale aunque este girado)
        public double LongitudPrincipal()
        {
            int n = Vertices;
            if (n < 2)
            {
                return 0;
            }
            double cx = 0, cy = 0;
            for (int i = 0; i < n; i++) { cx += V[3 * i]; cy += V[3 * i + 1]; }
            cx /= n; cy /= n;
            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double x = V[3 * i] - cx, y = V[3 * i + 1] - cy;
                sxx += x * x; sxy += x * y; syy += y * y;
            }
            double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ux = Math.Cos(ang), uy = Math.Sin(ang);
            double mn = double.MaxValue, mx = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double p = (V[3 * i] - cx) * ux + (V[3 * i + 1] - cy) * uy;
                if (p < mn) mn = p;
                if (p > mx) mx = p;
            }
            return mx - mn;
        }

        // Perimetro: aristas que solo tiene un triangulo (borde de una superficie). Se comparan posiciones,
        // no indices, porque en los solidos las caras con distinta orientacion no comparten vertices.
        public double Perimetro()
        {
            Dictionary<(long, long, long), int> pos = new Dictionary<(long, long, long), int>();
            int[] idp = new int[Vertices];
            int[] rep = new int[Vertices];
            for (int i = 0; i < idp.Length; i++)
            {
                (long, long, long) k = ((long)Math.Round(V[3 * i] * 1e4), (long)Math.Round(V[3 * i + 1] * 1e4), (long)Math.Round(V[3 * i + 2] * 1e4));
                int j;
                if (!pos.TryGetValue(k, out j))
                {
                    j = pos.Count;
                    pos[k] = j;
                }
                idp[i] = j;
                rep[j] = i;
            }
            Dictionary<long, int> cuenta = new Dictionary<long, int>();
            for (int t = 0; t < T.Count; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = idp[T[t + k]], b = idp[T[t + (k + 1) % 3]];
                    if (a == b)
                    {
                        continue;
                    }
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    int c;
                    cuenta.TryGetValue(key, out c);
                    cuenta[key] = c + 1;
                }
            }
            double l = 0;
            foreach (KeyValuePair<long, int> kv in cuenta)
            {
                if (kv.Value != 1)
                {
                    continue;
                }
                int a = rep[(int)(kv.Key >> 32)] * 3, b = rep[(int)(kv.Key & 0xFFFFFFFF)] * 3;
                double dx = V[b] - V[a], dy = V[b + 1] - V[a + 1], dz = V[b + 2] - V[a + 2];
                l += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return l;
        }

        // Volumen encerrado (teorema de la divergencia); solo tiene sentido en solidos cerrados
        public double Volumen()
        {
            if (T.Count == 0)
            {
                return 0;
            }
            double ox = V[0], oy = V[1], oz = V[2], s = 0;
            for (int t = 0; t < T.Count; t += 3)
            {
                int a = T[t] * 3, b = T[t + 1] * 3, c = T[t + 2] * 3;
                double ax = V[a] - ox, ay = V[a + 1] - oy, az = V[a + 2] - oz;
                double bx = V[b] - ox, by = V[b + 1] - oy, bz = V[b + 2] - oz;
                double cx = V[c] - ox, cy = V[c + 1] - oy, cz = V[c + 2] - oz;
                s += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
            }
            return Math.Abs(s) / 6.0;
        }
    }
}
