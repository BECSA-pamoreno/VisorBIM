// ============================================================================
// BECSATools - Visor BIM para Civil 3D 2027  v1.4  (2026-10-23)
// ----------------------------------------------------------------------------
// - Se carga solo al abrir Civil 3D (bundle en %APPDATA%\Autodesk\ApplicationPlugins)
// - Anade el panel "Visor BIM" a la pestana "BECSATools" de la cinta (la misma que usa
//   BecsaTerrenoC3D). Si la pestana no existe, la crea.
//     [Exportar visor]  -> BECSA_EXPORTARVISOR
// - El panel se pone en el primer momento libre tras arrancar, para que el addin de
//   Terreno haya creado ya su pestana y no se pisen.
// - Comando BECSA_CINTA_VISOR: vuelve a poner el panel si desaparece
// Iconos dibujados en codigo (WPF), sin ficheros PNG.
// ============================================================================

using System;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;
using WpfDrawingVisual = System.Windows.Media.DrawingVisual;
using WpfGeometry = System.Windows.Media.Geometry;
using WpfImageSource = System.Windows.Media.ImageSource;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPen = System.Windows.Media.Pen;
using WpfPixelFormats = System.Windows.Media.PixelFormats;
using WpfPoint = System.Windows.Point;
using WpfRenderBitmap = System.Windows.Media.Imaging.RenderTargetBitmap;
using WpfScale = System.Windows.Media.ScaleTransform;

[assembly: ExtensionApplication(typeof(BecsaTools.AppVisorC3D))]
[assembly: CommandClass(typeof(BecsaTools.AppVisorC3D))]

namespace BecsaTools
{
    public class AppVisorC3D : IExtensionApplication
    {
        // Mismo Id y titulo de pestana que BecsaTerrenoC3D: los dos addins comparten la pestana
        private const string TAB_ID = "BECSATOOLS_C3D_TAB";
        private const string TAB_TITULO = "BECSATools";
        private const string PANEL_ID = "BECSATOOLS_C3D_VISOR";

        public void Initialize()
        {
            try
            {
                AcApp.Idle += AlPrimerIdle;
                AcApp.SystemVariableChanged += AlCambiarVariable;
            }
            catch
            {
            }
        }

        public void Terminate()
        {
        }

        [CommandMethod("BECSA_CINTA_VISOR")]
        public void ComandoCinta()
        {
            RibbonControl cinta = ComponentManager.Ribbon;
            if (cinta == null)
            {
                Escribir("\nLa cinta no esta activa (comando CINTA / RIBBON para mostrarla).");
                return;
            }
            RibbonTab tab = cinta.FindTab(TAB_ID);
            if (tab != null)
            {
                foreach (RibbonPanel p in tab.Panels)
                {
                    if (p.Source != null && p.Source.Id == PANEL_ID)
                    {
                        tab.Panels.Remove(p);
                        break;
                    }
                }
            }
            PonerPanel();
            tab = cinta.FindTab(TAB_ID);
            if (tab != null)
            {
                tab.IsActive = true;
            }
            Escribir("\nPanel Visor BIM " + ExportadorVisorC3D.VERSION + " en la pestana " + TAB_TITULO + ".\n");
        }

        // ====================================================================
        // Eventos
        // ====================================================================

        private void AlPrimerIdle(object sender, EventArgs e)
        {
            if (ComponentManager.Ribbon == null)
            {
                return;   // la cinta aun no esta: se intenta en el siguiente momento libre
            }
            AcApp.Idle -= AlPrimerIdle;
            try
            {
                PonerPanel();
            }
            catch
            {
            }
        }

        // Al cambiar de espacio de trabajo Civil 3D reconstruye la cinta: se vuelve a poner el panel
        private void AlCambiarVariable(object sender, SystemVariableChangedEventArgs e)
        {
            if (!string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            AcApp.Idle -= AlPrimerIdle;
            AcApp.Idle += AlPrimerIdle;
        }

        // ====================================================================
        // Cinta
        // ====================================================================

        private static void PonerPanel()
        {
            RibbonControl cinta = ComponentManager.Ribbon;
            if (cinta == null)
            {
                return;
            }
            RibbonTab tab = cinta.FindTab(TAB_ID);
            if (tab == null)
            {
                tab = new RibbonTab();
                tab.Title = TAB_TITULO;
                tab.Id = TAB_ID;
                cinta.Tabs.Add(tab);
            }
            foreach (RibbonPanel p in tab.Panels)
            {
                if (p.Source != null && p.Source.Id == PANEL_ID)
                {
                    return;   // ya esta
                }
            }

            RibbonPanelSource fuente = new RibbonPanelSource();
            fuente.Title = "Visor BIM " + ExportadorVisorC3D.VERSION;
            fuente.Id = PANEL_ID;
            RibbonPanel panel = new RibbonPanel();
            panel.Source = fuente;
            tab.Panels.Add(panel);

            fuente.Items.Add(Boton("Exportar visor", "BECSA_EXPORTARVISOR",
                "Exportar al Visor BIM",
                "Exporta lo visible del dibujo (o una selección) a DAE + XLSX. Pregunta dónde guardarlos y recuerda la última carpeta (la misma que en Revit). "
                + "como la macro ExportarVisor de Revit: superficies, sólidos de obras lineales, redes de tuberías "
                + "y a presión, sólidos, mallas, bloques y referencias externas, con sus propiedades.\n\n"
                + "Ábrelos en VisorBIM > Visor Revit junto a los modelos de Revit: caen en su sitio si el proyecto "
                + "de Revit adquirió las coordenadas de este dibujo.",
                IconoVisor(32), IconoVisor(16)));
        }

        private static RibbonButton Boton(string texto, string comando, string tituloAyuda, string ayuda,
            WpfImageSource grande, WpfImageSource pequena)
        {
            RibbonButton b = new RibbonButton();
            b.Text = texto;
            b.Id = "BECSATOOLS_C3D_" + comando;
            b.ShowText = true;
            b.ShowImage = true;
            b.Size = RibbonItemSize.Large;
            b.Orientation = WpfOrientation.Vertical;
            b.LargeImage = grande;
            b.Image = pequena;
            b.CommandParameter = comando;
            b.CommandHandler = new ManejadorComando();

            RibbonToolTip tt = new RibbonToolTip();
            tt.Title = tituloAyuda;
            tt.Content = ayuda;
            tt.Command = comando;
            b.ToolTip = tt;
            return b;
        }

        // Lanza el comando en el documento activo (cancela antes lo que hubiera en curso)
        private class ManejadorComando : System.Windows.Input.ICommand
        {
            public event EventHandler CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object parameter)
            {
                return true;
            }

            public void Execute(object parameter)
            {
                RibbonCommandItem item = parameter as RibbonCommandItem;
                string comando = (item != null) ? item.CommandParameter as string : null;
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null || string.IsNullOrEmpty(comando))
                {
                    return;
                }
                doc.SendStringToExecute("\x03\x03_" + comando + " ", true, false, true);
            }
        }

        // ====================================================================
        // Icono: cubo (modelo) con flecha de salida
        // ====================================================================

        private static WpfImageSource IconoVisor(int px)
        {
            return Pintar(px, dc =>
            {
                WpfPen borde = new WpfPen(new WpfBrush(WpfColor.FromRgb(40, 50, 60)), 1.4);
                // Cubo
                dc.DrawGeometry(new WpfBrush(WpfColor.FromRgb(242, 183, 5)), borde, WpfGeometry.Parse("M4,11 L13,6 L22,11 L13,16 Z"));
                dc.DrawGeometry(new WpfBrush(WpfColor.FromRgb(200, 150, 10)), borde, WpfGeometry.Parse("M4,11 L13,16 L13,27 L4,22 Z"));
                dc.DrawGeometry(new WpfBrush(WpfColor.FromRgb(160, 120, 15)), borde, WpfGeometry.Parse("M22,11 L13,16 L13,27 L22,22 Z"));
                // Flecha hacia el visor
                WpfPen flecha = new WpfPen(new WpfBrush(WpfColor.FromRgb(30, 120, 200)), 2.6);
                dc.DrawLine(flecha, new WpfPoint(18, 26), new WpfPoint(29, 15));
                dc.DrawGeometry(new WpfBrush(WpfColor.FromRgb(30, 120, 200)), null, WpfGeometry.Parse("M30.5,13.5 L22,14.5 L29.5,22 Z"));
            });
        }

        private static WpfImageSource Pintar(int px, Action<System.Windows.Media.DrawingContext> dibujo)
        {
            WpfDrawingVisual dv = new WpfDrawingVisual();
            using (System.Windows.Media.DrawingContext dc = dv.RenderOpen())
            {
                double k = px / 32.0;
                dc.PushTransform(new WpfScale(k, k));
                dibujo(dc);
                dc.Pop();
            }
            WpfRenderBitmap bmp = new WpfRenderBitmap(px, px, 96, 96, WpfPixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        private static void Escribir(string texto)
        {
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage(texto);
                }
            }
            catch
            {
            }
        }
    }
}
