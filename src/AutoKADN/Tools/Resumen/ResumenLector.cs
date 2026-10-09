using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using AutoKADN.Core;
using AutoKADN.Tools.Anotaciones;
using AutoKADN.Tools.Excel;
using AutoKADN.Tools.Layouts;
using static AutoKADN.Core.Naming;

namespace AutoKADN.Tools.Resumen;

// Lee del dibujo lo que el plano ya arroja y lo ordena por anillo y consolidado. No inventa cálculos: usa los mismos
// escaneos del Excel de legalización (GenerarExcelTool), con un filtro de layout para sacar cada anillo por separado,
// y repite en el mismo orden la secuencia de GENERAREXCEL (cotas, accesorios, espiral, pruebas, actividades, cruce de
// arroyo, tubería) para que los números coincidan con los de ese Excel.
internal static class ResumenLector
{
    private static readonly Regex TituloDiametro = new Regex("(1/2|3/4)\"");
    private static readonly string[] OrdenMateriales = { "TUBERIA", "VALVULA", "UNION", "TEE", "TAPON", "REDUCCION", "SILLETA", "CODO" };

    private sealed class InfoLayout
    {
        public string Nombre = string.Empty;
        public string Clave = string.Empty;
        public bool Detalle;
        public int Orden;
    }

    public static ResumenProyecto Leer(Database database, string rutaDibujo)
    {
        var proyecto = new ResumenProyecto();
        if (!string.IsNullOrWhiteSpace(rutaDibujo))
        {
            proyecto.Dibujo = Path.GetFileName(rutaDibujo);
            proyecto.Carpeta = Path.GetDirectoryName(Path.GetFullPath(rutaDibujo)) ?? string.Empty;
        }
        try { proyecto.Version = typeof(ResumenLector).Assembly.GetName().Version?.ToString() ?? string.Empty; }
        catch (Exception) { proyecto.Version = string.Empty; }

        List<InfoLayout> layouts = ExplorarLayouts(database, proyecto);

        // Los anillos que existen, en orden (ANILLO 1, 2, ... y al final el TRONCAL).
        foreach (IGrouping<string, InfoLayout> grupo in layouts.GroupBy(l => l.Clave, StringComparer.OrdinalIgnoreCase))
        {
            var anillo = new ResumenAnillo { Clave = grupo.Key, EsTroncal = grupo.Key == "TRONCAL" };
            Match numero = Regex.Match(grupo.Key, @"ANILLO\s+(\d+)", RegexOptions.IgnoreCase);
            if (numero.Success) anillo.Numero = int.Parse(numero.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            foreach (InfoLayout layout in grupo)
            {
                if (layout.Detalle) { anillo.TieneDetalle = true; anillo.LayoutDetalle = layout.Nombre; }
                else { anillo.TieneUc = true; anillo.LayoutUc = layout.Nombre; }
            }
            proyecto.Anillos.Add(anillo);
        }
        proyecto.Anillos.Sort((a, b) =>
        {
            if (a.EsTroncal != b.EsTroncal) return a.EsTroncal ? 1 : -1;
            return a.Numero.CompareTo(b.Numero);
        });

        foreach (ResumenAnillo anillo in proyecto.Anillos)
        {
            string clave = anillo.Clave;
            Procesar(database, anillo, nombre => string.Equals(ClaveDeLayout(nombre), clave, StringComparison.OrdinalIgnoreCase), proyecto, true);
        }

        // El consolidado repite el proceso con todo el dibujo a la vez, como lo hace GENERAREXCEL.
        proyecto.Consolidado.TieneDetalle = proyecto.Anillos.Any(a => a.TieneDetalle);
        proyecto.Consolidado.TieneUc = proyecto.Anillos.Any(a => a.TieneUc);
        Procesar(database, proyecto.Consolidado, null, proyecto, false);

        try { proyecto.Informes = GenerarExcelTool.BuildProjectSummary(database); }
        catch (Exception ex) { proyecto.InformesProblema = ex.Message; }

        try
        {
            ContractorReading lectura = ContratistaReader.Read(database);
            proyecto.Contratista = lectura.Name;
            proyecto.ContratistaProblema = lectura.Problem;
        }
        catch (Exception ex) { proyecto.ContratistaProblema = "no se pudo leer (" + ex.Message + ")"; }

        return proyecto;
    }

    // "ANILLO 3 UC" / "anillo  3  detalle" -> "ANILLO 3"; "TRONCAL UC" -> "TRONCAL".
    internal static string ClaveDeLayout(string nombre)
    {
        string limpio = Regex.Replace((nombre ?? string.Empty).Trim().ToUpperInvariant(), @"\s+", " ");
        return Regex.Replace(limpio, @"\s(UC|DETALLE)$", string.Empty);
    }

    // ---------- layouts: cajetín, cruce de arroyo anotado en el UC y nombres que no se reconocen ----------

    private static List<InfoLayout> ExplorarLayouts(Database database, ResumenProyecto proyecto)
    {
        var resultado = new List<InfoLayout>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var diccionario = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            var todos = new List<Layout>();
            foreach (DBDictionaryEntry entrada in diccionario)
            {
                var layout = transaction.GetObject(entrada.Value, OpenMode.ForRead) as Layout;
                if (layout != null) todos.Add(layout);
            }

            foreach (Layout layout in todos.OrderBy(l => l.TabOrder))
            {
                string nombre = layout.LayoutName.Trim();
                if (string.Equals(nombre, "Model", StringComparison.OrdinalIgnoreCase)) continue;
                bool detalle = GenerarExcelTool.IsDetailLayout(nombre);
                bool uc = GenerarExcelTool.IsUcLayout(nombre);
                if (!detalle && !uc)
                {
                    if (Regex.IsMatch(nombre, @"ANILLO|TRONCAL", RegexOptions.IgnoreCase)) proyecto.LayoutsNoReconocidos.Add(nombre);
                    continue;
                }

                string clave = ClaveDeLayout(nombre);
                resultado.Add(new InfoLayout { Nombre = nombre, Clave = clave, Detalle = detalle, Orden = layout.TabOrder });

                var espacio = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in espacio)
                {
                    if (id.ObjectClass.DxfName != "MTEXT") continue;
                    var texto = transaction.GetObject(id, OpenMode.ForRead) as MText;
                    if (texto == null) continue;

                    string prefijo, etiqueta, valor;
                    if (CajetinRules.TryParseField(texto.Contents, out prefijo, out etiqueta, out valor))
                    {
                        if (string.Equals(etiqueta, "FECHA", StringComparison.OrdinalIgnoreCase)) continue; // varía por diseño
                        string limpio = Regex.Replace(valor ?? string.Empty, @"\s+", " ").Trim();
                        if (limpio.Length == 0) continue;
                        if (!proyecto.Cajetin.ContainsKey(etiqueta)) proyecto.Cajetin[etiqueta] = limpio;
                        Dictionary<string, List<string>> porValor;
                        if (!proyecto.CajetinPorLayout.TryGetValue(etiqueta, out porValor))
                        {
                            porValor = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            proyecto.CajetinPorLayout[etiqueta] = porValor;
                        }
                        List<string> donde;
                        if (!porValor.TryGetValue(limpio, out donde)) { donde = new List<string>(); porValor[limpio] = donde; }
                        if (!donde.Contains(nombre)) donde.Add(nombre);
                        continue;
                    }

                    // Los títulos del plano llevan el diámetro (ANILLO 1 - 3/4", %%C3/4"): solo textos sin datos del plugin.
                    if (!ClonarUcTool.HasDetalleXData(texto))
                    {
                        foreach (Match m in TituloDiametro.Matches(texto.Contents ?? string.Empty))
                        {
                            HashSet<string> diametros;
                            if (!proyecto.DiametrosTitulo.TryGetValue(clave, out diametros))
                            {
                                diametros = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                proyecto.DiametrosTitulo[clave] = diametros;
                            }
                            diametros.Add(m.Groups[1].Value);
                        }
                    }

                    if (!uc) continue;
                    foreach (KeyValuePair<UcKey, double> arroyo in ActividadXData.ReadQuantities(texto, CruceArroyoLabel))
                        Sumar(proyecto.ArroyoEnUc, clave, arroyo.Key, arroyo.Value);
                }
            }
            transaction.Commit();
        }
        return resultado;
    }

    private static void Sumar(Dictionary<string, Dictionary<UcKey, double>> destino, string anillo, UcKey uc, double valor)
    {
        Dictionary<UcKey, double> porUc;
        if (!destino.TryGetValue(anillo, out porUc)) { porUc = new Dictionary<UcKey, double>(); destino[anillo] = porUc; }
        double actual; porUc.TryGetValue(uc, out actual);
        porUc[uc] = actual + valor;
    }

    // ---------- un anillo (o todo el proyecto si filtro es null) ----------

    private static void Procesar(Database database, ResumenAnillo destino, Func<string, bool> filtro, ResumenProyecto proyecto, bool registrarOmitidos)
    {
        string etiqueta = destino.Clave;
        Action<string, string, string, string> cotaOmitida = null, bloqueOmitido = null;
        if (registrarOmitidos)
        {
            cotaOmitida = (layout, diametro, texto, motivo) => proyecto.Omitidos.Add(new ResumenOmitido
            { Anillo = ClaveDeLayout(layout), Layout = layout, Tipo = "Cota", Elemento = texto, Diametro = diametro, Motivo = motivo });
            bloqueOmitido = (layout, nombre, diametro, motivo) => proyecto.Omitidos.Add(new ResumenOmitido
            { Anillo = ClaveDeLayout(layout), Layout = layout, Tipo = "Bloque", Elemento = nombre, Diametro = diametro, Motivo = motivo });
        }

        Dictionary<UcKey, double> cotas = GenerarExcelTool.ScanUcs(database, null, filtro, cotaOmitida);
        var totales = new Dictionary<UcKey, double>(cotas);
        Dictionary<UcKey, Dictionary<MaterialKey, double>> accesorios = GenerarExcelTool.ScanAccessories(database, filtro, bloqueOmitido);
        Dictionary<UcKey, Dictionary<MaterialKey, double>> espiral = GenerarExcelTool.ScanSpiral(database, null, filtro);
        Dictionary<UcKey, Dictionary<MaterialKey, double>> pruebas = GenerarExcelTool.ScanMaterialTest(database, null, filtro);
        Dictionary<UcKey, GenerarExcelTool.ActivityAgg> actividades = GenerarExcelTool.ScanActivities(database, null, filtro);

        if (registrarOmitidos)
            foreach (KeyValuePair<UcKey, GenerarExcelTool.ActivityAgg> actividad in actividades)
                if (actividad.Value.CruceArroyo > 0.0) Sumar(proyecto.ArroyoEnDetalle, etiqueta, actividad.Key, actividad.Value.CruceArroyo);

        // Igual que GENERAREXCEL: el cruce de arroyo sale de la UC antes de convertir los metros a TUBERIA.
        GenerarExcelTool.MoveCruceArroyoToOwnUc(null, totales, actividades);
        var proyectoMateriales = new Dictionary<UcKey, Dictionary<MaterialKey, double>>();
        GenerarExcelTool.MergeMaterialQuantities(proyectoMateriales, accesorios);
        GenerarExcelTool.MergeMaterialQuantities(proyectoMateriales, espiral);
        GenerarExcelTool.MergeMaterialQuantities(proyectoMateriales, GenerarExcelTool.ConvertUcTotalsToPipeMaterials(totales));
        var todosMateriales = new Dictionary<UcKey, Dictionary<MaterialKey, double>>();
        GenerarExcelTool.MergeMaterialQuantities(todosMateriales, proyectoMateriales);
        GenerarExcelTool.MergeMaterialQuantities(todosMateriales, pruebas);

        var claves = new HashSet<UcKey>(totales.Keys);
        claves.UnionWith(todosMateriales.Keys);
        claves.UnionWith(actividades.Keys);
        List<UcKey> ordenadas = claves
            .OrderBy(k => GetSurfaceOrder(k.Surface)).ThenBy(k => ResumenCatalogo.OrdenDiametro(k.Diameter))
            .ThenBy(k => k.Surface, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (UcKey uc in ordenadas)
        {
            double antes; cotas.TryGetValue(uc, out antes);
            double despues; totales.TryGetValue(uc, out despues);
            GenerarExcelTool.ActivityAgg agg; actividades.TryGetValue(uc, out agg);
            bool esArroyo = IsCruceArroyo(uc);

            var fila = new ResumenUc
            {
                Anillo = etiqueta,
                Diametro = uc.Diameter,
                Terreno = uc.Surface,
                Cotas = antes,
                CotasNetas = despues,
                Camisa = agg == null ? 0.0 : agg.Camisa,
                CruceTopo = agg == null ? 0.0 : agg.CruceTopo,
                Pantalla = agg == null ? 0.0 : agg.Pantalla,
                VigaConcreto = agg == null ? 0.0 : agg.VigaConcreto,
                Empedrado = agg == null ? 0.0 : agg.Empedrado,
                Espiral = SumaTuberia(espiral, uc),
                Pruebas = SumaTuberia(pruebas, uc),
            };
            fila.Arroyo = esArroyo ? despues : -(agg == null ? 0.0 : agg.CruceArroyo);
            Dictionary<MaterialKey, double> piezas;
            if (todosMateriales.TryGetValue(uc, out piezas))
                foreach (KeyValuePair<MaterialKey, double> pieza in piezas)
                    if (pieza.Key.Description != NormalizeToken("TUBERIA")) fila.Accesorios += pieza.Value;

            double tuberia;
            Dictionary<string, double> codigos = GenerarExcelTool.BuildActivityQuantities(uc, totales, todosMateriales, actividades, out tuberia);
            fila.Tuberia = tuberia;
            if (esArroyo)
            {
                // La UC especial no usa canalización ni tendido: solo el cruce de arroyo (anillo o troncal) y los planos as-built.
                codigos = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                bool troncal = Array.IndexOf(GenerarExcelTool.TroncalDiameters, uc.Diameter) >= 0;
                if (despues > 0.0) codigos[troncal ? GenerarExcelTool.CruceArroyoTroncalCode : GenerarExcelTool.CruceArroyoRingCode] = despues;
                codigos[GenerarExcelTool.PlanosAsBuiltCode] = despues;
            }
            foreach (KeyValuePair<string, double> codigo in codigos) fila.Actividades[codigo.Key] = codigo.Value;

            bool esTroncal = Array.IndexOf(GenerarExcelTool.TroncalDiameters, uc.Diameter) >= 0;
            Dictionary<string, string> canalizacion = esTroncal ? GenerarExcelTool.CanalizacionTroncalCodeBySurface : GenerarExcelTool.CanalizacionCodeBySurface;
            string codigoCanalizacion;
            if (!esArroyo && canalizacion.TryGetValue(uc.Surface, out codigoCanalizacion) && fila.Actividades.TryGetValue(codigoCanalizacion, out double metros))
                fila.Canalizacion = metros;

            // Excavación: lo mismo que el control de excavación (FT-O-108): cotas netas menos camisa y cruce con topo, sin negativos.
            double excavacion = despues - fila.Camisa - fila.CruceTopo;
            fila.Excavacion = excavacion > 0.0 ? excavacion : 0.0;
            // Por anillo la zanja es ML x factor sin truncar; el formato FT-O-108 trunca la de cada UC ya sumada entre anillos.
            fila.Volumen = filtro != null ? fila.Excavacion * ResumenCatalogo.FactorZanja(uc.Diameter) : ControlExcavacionBuilder.Volume(uc.Diameter, fila.Excavacion);
            destino.Ucs.Add(fila);
        }

        var acumulado = new Dictionary<MaterialKey, ResumenMaterial>();
        AcumularMateriales(acumulado, proyectoMateriales, false, etiqueta);
        AcumularMateriales(acumulado, pruebas, true, etiqueta);
        destino.Materiales.AddRange(acumulado.Values
            .OrderBy(m => OrdenMaterial(m.Descripcion)).ThenBy(m => ResumenCatalogo.OrdenDiametro(m.Diametro))
            .ThenBy(m => m.Diametro, StringComparer.OrdinalIgnoreCase));
    }

    private static double SumaTuberia(Dictionary<UcKey, Dictionary<MaterialKey, double>> origen, UcKey uc)
    {
        Dictionary<MaterialKey, double> materiales;
        if (!origen.TryGetValue(uc, out materiales)) return 0.0;
        double total = 0.0;
        foreach (KeyValuePair<MaterialKey, double> item in materiales)
            if (item.Key.Description == NormalizeToken("TUBERIA")) total += item.Value;
        return total;
    }

    private static void AcumularMateriales(Dictionary<MaterialKey, ResumenMaterial> acumulado,
        Dictionary<UcKey, Dictionary<MaterialKey, double>> origen, bool pruebas, string anillo)
    {
        foreach (KeyValuePair<UcKey, Dictionary<MaterialKey, double>> uc in origen)
            foreach (KeyValuePair<MaterialKey, double> item in uc.Value)
            {
                ResumenMaterial material;
                if (!acumulado.TryGetValue(item.Key, out material))
                {
                    material = new ResumenMaterial
                    { Anillo = anillo, Descripcion = item.Key.Description, Diametro = item.Key.Diameter, Unidad = item.Key.Unit, Codigo = item.Key.Code };
                    acumulado[item.Key] = material;
                }
                if (pruebas) material.Pruebas += item.Value; else material.Proyecto += item.Value;
            }
    }

    private static int OrdenMaterial(string descripcion)
    {
        int indice = Array.IndexOf(OrdenMateriales, descripcion);
        return indice < 0 ? OrdenMateriales.Length : indice;
    }
}
