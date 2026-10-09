using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using AutoKADN.Core;
using Autodesk.AutoCAD.EditorInput;

namespace AutoKADN.Tools.Excel;

// Consolidado de TODO el proyecto (sin separar por UC): anillos, tubería por diámetro y accesorios por diámetro,
// con el material de prueba aparte. Lo recogen GenerarExcelTool.BuildProjectSummary (que reutiliza los mismos
// escaneos del Excel de legalización) y ResumenObraTool lo escribe en la carpeta raíz del proyecto (resumen_obra.json) para que la app
// AutoKADN.Proyectos rellene los formatos de interventoría.
public sealed class ProjectSummary
{
    // Layouts "ANILLO n DETALLE".
    public int RingDetailLayouts;
    // Empresa contratista, leída del cajetín del primer plano de detalles ("" si no se encontró). Ver ContratistaReader.
    public string Contractor = string.Empty;
    // Anillos (layouts "ANILLO n UC") que traen cotas de cada diámetro (1/2, 3/4).
    public readonly Dictionary<string, int> RingsByDiameter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    // Material del proyecto y de prueba por "DESCRIPCION|DIAMETRO" (tubería en ML, el resto en unidades).
    public readonly Dictionary<string, double> Project = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, double> Tests = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public void AddRing(string diameter)
    {
        int current; RingsByDiameter.TryGetValue(diameter, out current); RingsByDiameter[diameter] = current + 1;
    }

    // Tubería por anillo ("ANILLO n" / "TRONCAL") y por unidad constructiva (diámetro + terreno): cotas de las UC,
    // espiral y pruebas, las mismas cantidades que suma el RESUMEN UC y el Excel de legalización.
    public readonly Dictionary<string, Dictionary<UcKey, double>> RingPipes = new Dictionary<string, Dictionary<UcKey, double>>(StringComparer.OrdinalIgnoreCase);
    // Tubería de espiral de válvula por anillo (ya incluida en RingPipes).
    public readonly Dictionary<string, double> RingSpiral = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    // Actividades especiales por anillo: "CAMISA", "CRUCE CON TOPO"... (ML).
    public readonly Dictionary<string, Dictionary<string, double>> RingActivities = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

    // Solo las cotas UC por anillo (sin espiral ni pruebas) y lo que no se excava (camisa y cruce con topo) por
    // anillo y UC: con eso se arma el control de excavación.
    public readonly Dictionary<string, Dictionary<UcKey, double>> RingCotas = new Dictionary<string, Dictionary<UcKey, double>>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Dictionary<UcKey, double>> RingExcluded = new Dictionary<string, Dictionary<UcKey, double>>(StringComparer.OrdinalIgnoreCase);

    public void AddRingCota(string ring, UcKey uc, double ml) => AddTo(RingCotas, ring, uc, ml);
    public void AddRingExcluded(string ring, UcKey uc, double ml) => AddTo(RingExcluded, ring, uc, ml);

    private static void AddTo(Dictionary<string, Dictionary<UcKey, double>> target, string ring, UcKey uc, double ml)
    {
        Dictionary<UcKey, double> perUc;
        if (!target.TryGetValue(ring, out perUc)) { perUc = new Dictionary<UcKey, double>(); target[ring] = perUc; }
        double current; perUc.TryGetValue(uc, out current); perUc[uc] = current + ml;
    }

    public void AddRingPipe(string ring, UcKey uc, double ml)
    {
        Dictionary<UcKey, double> pipes;
        if (!RingPipes.TryGetValue(ring, out pipes)) { pipes = new Dictionary<UcKey, double>(); RingPipes[ring] = pipes; }
        double current; pipes.TryGetValue(uc, out current); pipes[uc] = current + ml;
    }

    // CRUCE DE ARROYO anotado en el plano de detalle de ese anillo: esos metros salen de la UC (diámetro + terreno)
    // en la tubería y en las cotas, y pasan a la UC "CRUCE DE ARROYO" del mismo diámetro, que los formatos tratan
    // como un terreno más. Lo anotado manda: si supera lo que hay, la UC queda en cero (no negativa).
    public void MoveToCruceArroyo(string ring, UcKey uc, double ml)
    {
        if (ml <= 0.0) return;
        UcKey arroyo = new UcKey(uc.Diameter, Naming.CruceArroyoSurface);
        Subtract(RingPipes, ring, uc, ml);
        Subtract(RingCotas, ring, uc, ml);
        AddRingPipe(ring, arroyo, ml);
        AddRingCota(ring, arroyo, ml);
    }

    private static void Subtract(Dictionary<string, Dictionary<UcKey, double>> target, string ring, UcKey uc, double ml)
    {
        Dictionary<UcKey, double> perUc; double current;
        if (!target.TryGetValue(ring, out perUc) || !perUc.TryGetValue(uc, out current)) return;
        perUc[uc] = Math.Max(0.0, current - ml);
    }

    public void AddRingSpiral(string ring, UcKey uc, double ml)
    {
        double current; RingSpiral.TryGetValue(ring, out current); RingSpiral[ring] = current + ml;
    }

    public void AddRingActivity(string ring, string label, double ml)
    {
        Dictionary<string, double> activities;
        if (!RingActivities.TryGetValue(ring, out activities)) { activities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); RingActivities[ring] = activities; }
        string key = label.Trim().ToUpperInvariant();
        double current; activities.TryGetValue(key, out current); activities[key] = current + ml;
    }

    public void AddProject(string description, string diameter, double quantity) => Add(Project, description, diameter, quantity);
    public void AddTest(string description, string diameter, double quantity) => Add(Tests, description, diameter, quantity);

    private static void Add(Dictionary<string, double> target, string description, string diameter, double quantity)
    {
        string key = description + "|" + diameter;
        double current; target.TryGetValue(key, out current); target[key] = current + quantity;
    }

    public double Quantity(Dictionary<string, double> source, string description, string diameter)
    {
        double value; return source.TryGetValue(description + "|" + diameter, out value) ? value : 0.0;
    }

    // Diámetros que aparecen para una descripción (en el proyecto o en las pruebas).
    public List<string> Diameters(string description)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in Project.Keys.Concat(Tests.Keys))
        {
            int bar = key.IndexOf('|');
            if (bar > 0 && string.Equals(key.Substring(0, bar), description, StringComparison.OrdinalIgnoreCase)) set.Add(key.Substring(bar + 1));
        }
        return set.OrderBy(DiameterSortKey).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly string[] DiameterOrderList = { "1/2", "3/4", "2", "3", "4", "6" };
    public static int DiameterSortKey(string diameter)
    {
        string first = diameter.Split('x', 'X')[0].Trim();
        int index = Array.IndexOf(DiameterOrderList, first);
        return index < 0 ? int.MaxValue : index;
    }
}

public sealed class ResumenObraTool
{
    public const string FileName = "resumen_obra.json";

    private static readonly string[] RingDiameters = { "1/2", "3/4" };
    private static readonly string[] TroncalDiameters = { "2", "3", "4", "6" };

    // (clave en el JSON, descripción del material). Los codos no están en el catálogo de materiales todavía:
    // si algún bloque se llama CODO se contará; si no, la lista queda vacía.
    private static readonly string[][] Accessories =
    {
        new[] { "union", "UNION" }, new[] { "tee", "TEE" }, new[] { "tapon", "TAPON" },
        new[] { "reduccion", "REDUCCION" }, new[] { "silleta", "SILLETA" }, new[] { "codos", "CODO" },
    };

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        try
        {
            string drawingPath = database.Filename;
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath))
            {
                editor.WriteMessage("\n[RESUMENOBRA] Guarda el dibujo primero: el resumen se escribe en la carpeta del proyecto, a partir de la ubicación del DWG.\n");
                return;
            }

            ProjectSummary summary = GenerarExcelTool.BuildProjectSummary(database);
            // El contratista es un dato aparte: si no se puede leer, el resto del resumen sale igual.
            var contractor = new ContractorReading();
            try { contractor = ContratistaReader.Read(database); }
            catch (Exception ex) { contractor.Problem = "no se pudo leer (" + ex.Message + ")"; }
            summary.Contractor = contractor.Name;
            string json = BuildJson(summary, ReadGasificado(drawingPath));
            string target = Path.Combine(ProjectRoot(drawingPath), FileName);
            File.WriteAllText(target, json, new UTF8Encoding(false));

            editor.WriteMessage("\n[RESUMENOBRA] " + target + "\n");
            editor.WriteMessage("  Anillos (layouts DETALLE): " + summary.RingDetailLayouts + "\n");
            editor.WriteMessage(contractor.Name.Length > 0
                ? "  Contratista: " + contractor.Name + " (de " + contractor.Layout + ")\n"
                : "  Contratista: " + contractor.Problem + "; no se escribió en el resumen.\n");
            foreach (string diameter in RingDiameters)
                editor.WriteMessage("  Tubería anillos " + diameter + "\": " + Ml(Pipe(summary, diameter)) + " ML\n");
            foreach (string diameter in TroncalDiameters)
            {
                double ml = Pipe(summary, diameter);
                if (ml > 0.0) editor.WriteMessage("  Tubería troncal " + diameter + "\": " + Ml(ml) + " ML\n");
            }
            foreach (string[] accessory in Accessories.Concat(new[] { new[] { "valvulas", "VALVULA" } }))
            {
                double project = 0.0, test = 0.0;
                foreach (string diameter in summary.Diameters(accessory[1]))
                {
                    project += summary.Quantity(summary.Project, accessory[1], diameter);
                    test += summary.Quantity(summary.Tests, accessory[1], diameter);
                }
                if (project > 0.0 || test > 0.0)
                    editor.WriteMessage("  " + accessory[1] + ": proyecto " + Count(project) + ", pruebas " + Count(test) + "\n");
            }
        }
        catch (Exception ex)
        {
            editor.WriteMessage("\n[RESUMENOBRA] ERROR: " + ex.Message + "\n");
        }
    }

    // Fecha de gasificado del proyecto (datos_proyecto.json, en la raíz del proyecto o junto al DWG); vacío si no está.
    private static string ReadGasificado(string drawingPath)
    {
        try
        {
            string candidate = Path.Combine(ProjectRoot(drawingPath), "datos_proyecto.json");
            if (!File.Exists(candidate)) candidate = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(drawingPath)), "datos_proyecto.json");
            if (!File.Exists(candidate)) return string.Empty;
            Dictionary<string, string> data = AutoKADN.Core.FlatJson.Parse(File.ReadAllText(candidate, Encoding.UTF8));
            string value; return data.TryGetValue("fechaGasificado", out value) ? value : string.Empty;
        }
        catch (Exception) { return string.Empty; }
    }

    // Carpeta raíz del proyecto: si el DWG está en PLANOS, la de arriba; si no, la del propio DWG.
    internal static string ProjectRoot(string drawingPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(drawingPath));
        if (string.Equals(Path.GetFileName(directory), "PLANOS", StringComparison.OrdinalIgnoreCase))
        {
            string parent = Path.GetDirectoryName(directory);
            if (!string.IsNullOrEmpty(parent)) return parent;
        }
        return directory;
    }

    private static double Pipe(ProjectSummary s, string diameter) =>
        s.Quantity(s.Project, "TUBERIA", diameter) + s.Quantity(s.Tests, "TUBERIA", diameter);

    // ---------- JSON (a mano: el plugin también compila para .NET Framework y no depende de librerías) ----------

    private static string Ml(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);
    private static string Count(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Label(string diameter) => diameter + "\"";

    internal static string BuildJson(ProjectSummary s, string gasificadoIso = "")
    {
        var props = new List<string>();
        props.Add(Prop("version", "1"));
        props.Add(Prop("generado", DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)));
        props.Add(Prop("anillos.total", s.RingDetailLayouts.ToString(CultureInfo.InvariantCulture)));
        // Sin contratista no se escribe la clave: así los formatos lo reportan como dato que falta en vez de dejarlo vacío a propósito.
        if (!string.IsNullOrEmpty(s.Contractor)) props.Add(Prop("contratista", s.Contractor));

        props.Add(List("anillos.realizados", RingDiameters.Where(d => s.RingsByDiameter.ContainsKey(d))
            .Select(d => Item("diametro", Label(d), "cantidad", s.RingsByDiameter[d].ToString(CultureInfo.InvariantCulture)))));

        // Filas fijas (el formato trae impresos Ø 1/2" y Ø 3/4"): siempre los dos diámetros, en ese orden, con la
        // cantidad vacía si no hay. Así cada cantidad cae en la fila de su diámetro.
        props.Add(List("anillos.fijo", RingDiameters.Select(d => Item("diametro", Label(d),
            "cantidad", s.RingsByDiameter.ContainsKey(d) ? s.RingsByDiameter[d].ToString(CultureInfo.InvariantCulture) : ""))));
        props.Add(List("tuberia.anillos.fijo", RingDiameters.Select(d => Item("diametro", Label(d),
            "cantidad", Pipe(s, d) > 0.0 ? Ml(Pipe(s, d)) : ""))));

        AddPipe(props, s, "tuberia.anillos", RingDiameters);
        AddPipe(props, s, "tuberia.troncal", TroncalDiameters);

        // Polivalvulas = válvulas (proyecto + prueba) por diámetro.
        double valveTotal = 0.0;
        var valves = new List<string>();
        foreach (string diameter in s.Diameters("VALVULA"))
        {
            double q = s.Quantity(s.Project, "VALVULA", diameter) + s.Quantity(s.Tests, "VALVULA", diameter);
            if (q <= 0.0) continue;
            valveTotal += q;
            valves.Add(Item("diametro", Label(diameter), "cantidad", Count(q)));
        }
        props.Add(List("polivalvulas", valves));
        props.Add(Prop("polivalvulas.total", Count(valveTotal)));

        foreach (string[] accessory in Accessories)
        {
            var project = new List<string>();
            var tests = new List<string>();
            foreach (string diameter in s.Diameters(accessory[1]))
            {
                double p = s.Quantity(s.Project, accessory[1], diameter), t = s.Quantity(s.Tests, accessory[1], diameter);
                if (p <= 0.0 && t <= 0.0) continue;
                // En "accesorios" una cantidad en cero se deja vacía para que la celda quede en blanco.
                project.Add("{" + Pair("diametro", Label(diameter)) + "," + Pair("cantidad", p > 0.0 ? Count(p) : "") + "," +
                            Pair("pruebas", t > 0.0 ? Count(t) : "") + "," + Pair("total", Count(p + t)) + "}");
                // Las pruebas van en la MISMA fila que el diámetro del proyecto (así el total de la fila cuadra):
                // las dos listas tienen el mismo orden y largo, con la fila vacía donde no hubo pruebas.
                tests.Add(t > 0.0 ? Item("diametro", Label(diameter), "cantidad", Count(t)) : Item("diametro", "", "cantidad", ""));
            }
            props.Add(List("accesorios." + accessory[0], project));
            props.Add(List("pruebas." + accessory[0], tests));
        }

        // Hojas del formato FT-T-127 (totales de tubería por plano, terreno y diámetro).
        props.Add(TotalesTuberiaBuilder.BuildProperty(s, gasificadoIso));

        // Hojas del formato FT-O-108 (control de excavación por plano, terreno y diámetro).
        props.Add(ControlExcavacionBuilder.BuildProperty(s));

        return "{\n  " + string.Join(",\n  ", props) + "\n}\n";
    }

    private static void AddPipe(List<string> props, ProjectSummary s, string key, string[] diameters)
    {
        double total = 0.0;
        var items = new List<string>();
        foreach (string diameter in diameters)
        {
            double ml = Pipe(s, diameter);
            if (ml <= 0.0) continue;
            total += ml;
            items.Add(Item("diametro", Label(diameter), "cantidad", Ml(ml)));
        }
        props.Add(List(key, items));
        props.Add(Prop(key + ".total", Ml(total)));
    }

    private static string Prop(string key, string value) => Quote(key) + ": " + Quote(value);
    private static string Pair(string key, string value) => Quote(key) + ":" + Quote(value);
    private static string Item(string k1, string v1, string k2, string v2) => "{" + Pair(k1, v1) + "," + Pair(k2, v2) + "}";
    private static string List(string key, IEnumerable<string> items) => Quote(key) + ": [" + string.Join(", ", items) + "]";

    internal static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value ?? string.Empty)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                default: if (c < ' ') sb.Append(' '); else sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }
}
