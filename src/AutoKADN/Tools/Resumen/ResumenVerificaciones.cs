using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using AutoKADN.Core;
using static AutoKADN.Core.Naming;

namespace AutoKADN.Tools.Resumen;

// Verificaciones del resumen: cada una mira lo que se leyó del plano y deja hallazgos (error, aviso o info) con lo
// que hay que corregir. Se registran en una lista: para sumar una nueva basta una clase que implemente
// IResumenVerificacion (o una línea con Verificacion.De) y agregarla a ResumenVerificaciones.Todas.
internal interface IResumenVerificacion
{
    string Nombre { get; }
    void Ejecutar(ResumenProyecto proyecto, List<ResumenHallazgo> hallazgos);
}

internal sealed class Verificacion : IResumenVerificacion
{
    private readonly Action<ResumenProyecto, List<ResumenHallazgo>> _ejecutar;

    private Verificacion(string nombre, Action<ResumenProyecto, List<ResumenHallazgo>> ejecutar)
    {
        Nombre = nombre;
        _ejecutar = ejecutar;
    }

    public string Nombre { get; private set; }

    public void Ejecutar(ResumenProyecto proyecto, List<ResumenHallazgo> hallazgos) => _ejecutar(proyecto, hallazgos);

    public static Verificacion De(string nombre, Action<ResumenProyecto, List<ResumenHallazgo>> ejecutar) =>
        new Verificacion(nombre, ejecutar);
}

internal static class ResumenVerificaciones
{
    private const double Tolerancia = 0.01;

    public static readonly List<IResumenVerificacion> Todas = new List<IResumenVerificacion>
    {
        Verificacion.De("Nombres de layout reconocidos", NombresDeLayout),
        Verificacion.De("Cada anillo tiene DETALLE y UC", ParejaDetalleUc),
        Verificacion.De("Numeración de anillos", Numeracion),
        Verificacion.De("Cotas UC con terreno y medida", CotasOmitidas),
        Verificacion.De("Bloques de material reconocidos", BloquesOmitidos),
        Verificacion.De("Restas dentro de las cotas", RestasContraCotas),
        Verificacion.De("Actividades y materiales con cotas UC", SinCotas),
        Verificacion.De("Tubería = cotas netas + espiral + pruebas", TuberiaCoherente),
        Verificacion.De("Suma de anillos = consolidado", AnillosContraConsolidado),
        Verificacion.De("Cruce de arroyo igual en DETALLE y UC", ArroyoDetalleContraUc),
        Verificacion.De("Cajetín igual en todos los planos", CajetinUniforme),
        Verificacion.De("Diámetro del título = diámetro de las cotas", TituloContraCotas),
        Verificacion.De("Cajetín diligenciado", CajetinCompleto),
        Verificacion.De("Totales = informes de RESUMENOBRA", ContraInformes),
        Verificacion.De("Nombre del contratista", Contratista),
    };

    // Corre todas las verificaciones; las que no encuentran nada dejan una línea "OK" para que se vea qué se revisó.
    public static void Ejecutar(ResumenProyecto proyecto)
    {
        foreach (IResumenVerificacion verificacion in Todas)
        {
            var encontrados = new List<ResumenHallazgo>();
            try { verificacion.Ejecutar(proyecto, encontrados); }
            catch (Exception ex)
            {
                encontrados.Add(Nuevo(ResumenSeveridad.Info, string.Empty, verificacion.Nombre,
                    "No se pudo ejecutar esta verificación: " + ex.Message, "Avisa para revisarla; el resto del resumen no se ve afectado."));
            }

            if (encontrados.Count == 0)
            {
                proyecto.Hallazgos.Add(Nuevo(ResumenSeveridad.Ok, string.Empty, verificacion.Nombre, "Sin hallazgos.", string.Empty));
                continue;
            }
            foreach (ResumenHallazgo h in encontrados)
            {
                if (string.IsNullOrEmpty(h.Verificacion)) h.Verificacion = verificacion.Nombre;
                proyecto.Hallazgos.Add(h);
            }
        }
    }

    private static ResumenHallazgo Nuevo(ResumenSeveridad severidad, string anillo, string verificacion, string detalle, string correccion) =>
        new ResumenHallazgo { Severidad = severidad, Anillo = anillo, Verificacion = verificacion, Detalle = detalle, Correccion = correccion };

    private static string Ml(double valor) => valor.ToString("0.0##", CultureInfo.InvariantCulture);

    private static string NombreUc(string diametro, string terreno) =>
        ResumenCatalogo.Diametro(diametro) + " · " + ResumenCatalogo.Terreno(terreno);

    // ---------- verificaciones ----------

    private static void NombresDeLayout(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (string nombre in p.LayoutsNoReconocidos)
            h.Add(Nuevo(ResumenSeveridad.Aviso, string.Empty, string.Empty,
                "El layout «" + nombre + "» parece de un anillo, pero su nombre no es ANILLO n DETALLE, ANILLO n UC, TRONCAL DETALLE ni TRONCAL UC: no entra en ningún cálculo.",
                "Renómbralo con el formato exacto (por ejemplo ANILLO 3 DETALLE) o ignora este aviso si no es un plano de anillo."));
    }

    private static void ParejaDetalleUc(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (ResumenAnillo a in p.Anillos)
        {
            if (!a.TieneDetalle)
                h.Add(Nuevo(ResumenSeveridad.Error, a.Clave, string.Empty,
                    "Tiene layout UC pero no DETALLE: no se leen accesorios, actividades, espiral ni material de prueba de este anillo.",
                    "Crea el layout " + a.Clave + " DETALLE (NUEVOANILLO / RECORTEANILLOS) y vuelve a anotar."));
            if (!a.TieneUc)
                h.Add(Nuevo(ResumenSeveridad.Error, a.Clave, string.Empty,
                    "Tiene layout DETALLE pero no UC: no hay cotas UC, así que la tubería de este anillo sale en cero.",
                    "Clona el detalle a UC (CLONARUC) o crea el layout " + a.Clave + " UC."));
        }
    }

    private static void Numeracion(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        var numeros = new HashSet<int>(p.Anillos.Where(a => !a.EsTroncal && a.Numero > 0).Select(a => a.Numero));
        if (numeros.Count == 0) return;
        int maximo = numeros.Max();
        var faltan = new List<string>();
        for (int n = 1; n < maximo; n++)
            if (!numeros.Contains(n)) faltan.Add(n.ToString(CultureInfo.InvariantCulture));
        if (faltan.Count > 0)
            h.Add(Nuevo(ResumenSeveridad.Aviso, string.Empty, string.Empty,
                "Faltan los anillos " + string.Join(", ", faltan) + " (hay hasta el " + maximo + ").",
                "Si es intencional, ignora este aviso; si no, crea los layouts que faltan o renumera."));
    }

    private static void CotasOmitidas(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (IGrouping<string, ResumenOmitido> grupo in p.Omitidos.Where(o => o.Tipo == "Cota")
            .GroupBy(o => o.Layout + "|" + o.Diametro + "|" + o.Motivo, StringComparer.OrdinalIgnoreCase))
        {
            ResumenOmitido primero = grupo.First();
            string textos = string.Join(", ", grupo.Select(o => string.IsNullOrWhiteSpace(o.Elemento) ? "(vacía)" : o.Elemento).Take(6));
            string causa = primero.Motivo == "sin terreno"
                ? "la cota no tiene terreno asignado (ni en su dato ni por el color)"
                : "el texto de la cota no trae una medida legible";
            h.Add(Nuevo(ResumenSeveridad.Error, primero.Anillo, string.Empty,
                grupo.Count() + " cota(s) de " + ResumenCatalogo.Diametro(primero.Diametro) + " en «" + primero.Layout + "» no suman: " + causa + " (" + textos + ").",
                primero.Motivo == "sin terreno"
                    ? "Vuelve a acotar con COTA eligiendo el terreno, o ponle a la cota el color del terreno."
                    : "Corrige el texto de la cota para que muestre la medida en metros."));
        }
    }

    private static void BloquesOmitidos(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (IGrouping<string, ResumenOmitido> grupo in p.Omitidos.Where(o => o.Tipo == "Bloque")
            .GroupBy(o => o.Layout + "|" + o.Elemento + "|" + o.Diametro + "|" + o.Motivo, StringComparer.OrdinalIgnoreCase))
        {
            ResumenOmitido primero = grupo.First();
            string diametro = string.IsNullOrWhiteSpace(primero.Diametro) ? string.Empty : " Ø" + ResumenCatalogo.Diametro(NormalizeDiameter(primero.Diametro));
            string correccion;
            switch (primero.Motivo)
            {
                case "sin terreno":
                    correccion = "Dale terreno al bloque (propiedad UC del bloque o el color del terreno) para que cuente en la UC.";
                    break;
                case "fuera del catálogo":
                    correccion = "El nombre o el diámetro no coincide con el catálogo de materiales. Si es un accesorio real, usa el bloque estándar; si no es material, ignora este aviso.";
                    break;
                default:
                    correccion = "Revisa el diámetro del bloque: no corresponde a ninguna UC (1/2\", 3/4\", 2\", 3\", 4\" o 6\").";
                    break;
            }
            h.Add(Nuevo(ResumenSeveridad.Aviso, primero.Anillo, string.Empty,
                grupo.Count() + " bloque(s) «" + primero.Elemento + "»" + diametro + " en «" + primero.Layout + "» no se cuentan como material (" + primero.Motivo + ").",
                correccion));
        }
    }

    private static void RestasContraCotas(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (ResumenAnillo a in p.Anillos)
            foreach (ResumenUc uc in a.Ucs)
            {
                if (uc.EsCruceArroyo) continue;
                // Sin cotas de esa UC en el anillo lo avisa «Actividades y materiales con cotas UC».
                if (uc.Cotas <= Tolerancia && uc.CotasNetas <= Tolerancia) continue;
                double arroyo = -uc.Arroyo;
                if (arroyo > Tolerancia)
                {
                    if (uc.Cotas <= Tolerancia)
                        h.Add(Nuevo(ResumenSeveridad.Error, a.Clave, string.Empty,
                            "CRUCE DE ARROYO de " + Ml(arroyo) + " ML anotado en " + NombreUc(uc.Diametro, uc.Terreno) + ", pero este anillo no tiene cotas UC de ese diámetro y terreno: no se descontó de ninguna UC.",
                            "Revisa el diámetro y el terreno de la anotación, o agrega las cotas UC que faltan."));
                    else if (arroyo > uc.Cotas + Tolerancia)
                        h.Add(Nuevo(ResumenSeveridad.Error, a.Clave, string.Empty,
                            "CRUCE DE ARROYO de " + Ml(arroyo) + " ML supera las cotas de " + NombreUc(uc.Diametro, uc.Terreno) + " (" + Ml(uc.Cotas) + " ML): la UC queda en cero y la UC especial recibe " + Ml(arroyo) + " ML.",
                            "Corrige los metros del cruce o las cotas UC de ese terreno."));
                }

                double restas = uc.Camisa + uc.CruceTopo;
                if (restas > Tolerancia && restas > uc.CotasNetas + Tolerancia)
                    h.Add(Nuevo(ResumenSeveridad.Error, a.Clave, string.Empty,
                        "Camisa (" + Ml(uc.Camisa) + " ML) + cruce con topo (" + Ml(uc.CruceTopo) + " ML) = " + Ml(restas) + " ML supera las cotas de " + NombreUc(uc.Diametro, uc.Terreno) +
                        " (" + Ml(uc.CotasNetas) + " ML): la canalización queda en " + Ml(uc.CotasNetas - restas) + " ML.",
                        "Revisa los metros de la camisa y del cruce con topo, o el terreno donde los anotaste."));
            }
    }

    private static void SinCotas(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (ResumenAnillo a in p.Anillos)
            foreach (ResumenUc uc in a.Ucs)
            {
                if (uc.EsCruceArroyo || uc.Cotas > Tolerancia) continue;
                var partes = new List<string>();
                if (uc.Camisa > Tolerancia) partes.Add("camisa " + Ml(uc.Camisa) + " ML");
                if (uc.CruceTopo > Tolerancia) partes.Add("cruce con topo " + Ml(uc.CruceTopo) + " ML");
                if (uc.Pantalla > Tolerancia) partes.Add("pantalla " + Ml(uc.Pantalla) + " ML");
                if (uc.VigaConcreto > Tolerancia) partes.Add("viga en concreto " + Ml(uc.VigaConcreto) + " ML");
                if (uc.Empedrado > Tolerancia) partes.Add("empedrado " + Ml(uc.Empedrado) + " ML");
                if (uc.Espiral > Tolerancia) partes.Add("espiral " + Ml(uc.Espiral) + " ML");
                if (uc.Accesorios > Tolerancia) partes.Add(Ml(uc.Accesorios) + " pieza(s) de material");
                if (partes.Count == 0) continue;
                // Camisa o cruce con topo sin cotas dejan la canalización en negativo: eso sí cambia los números.
                bool resta = uc.Camisa > Tolerancia || uc.CruceTopo > Tolerancia;
                h.Add(Nuevo(resta ? ResumenSeveridad.Error : ResumenSeveridad.Aviso, a.Clave, string.Empty,
                    "En " + NombreUc(uc.Diametro, uc.Terreno) + " hay " + string.Join(", ", partes) + ", pero este anillo no tiene cotas UC de ese diámetro y terreno" +
                    (resta ? ": la canalización queda en " + Ml(uc.CotasNetas - uc.Camisa - uc.CruceTopo) + " ML." : "."),
                    "Revisa que el terreno y el diámetro de esas anotaciones o bloques sean los de las cotas; si las cotas faltan, acótalas."));
            }
    }

    private static void TuberiaCoherente(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (ResumenAnillo a in p.Anillos)
            foreach (ResumenUc uc in a.Ucs)
            {
                double esperado = uc.CotasNetas + uc.Espiral + uc.Pruebas;
                if (Math.Abs(uc.Tuberia - esperado) <= Tolerancia) continue;
                h.Add(Nuevo(ResumenSeveridad.Aviso, a.Clave, string.Empty,
                    "La tubería de " + NombreUc(uc.Diametro, uc.Terreno) + " es " + Ml(uc.Tuberia) + " ML y cotas netas + espiral + pruebas suman " + Ml(esperado) + " ML.",
                    "Revisa el material de prueba y el espiral de esa UC (unidad y terreno); el Excel de legalización usa el primero."));
            }
    }

    private static void AnillosContraConsolidado(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        var porUc = new Dictionary<UcKey, double[]>();
        foreach (ResumenAnillo a in p.Anillos)
            foreach (ResumenUc uc in a.Ucs)
            {
                double[] suma;
                if (!porUc.TryGetValue(uc.Clave, out suma)) { suma = new double[2]; porUc[uc.Clave] = suma; }
                suma[0] += uc.Tuberia;
                suma[1] += uc.Canalizacion;
            }

        foreach (ResumenUc consolidado in p.Consolidado.Ucs)
        {
            double[] suma;
            if (!porUc.TryGetValue(consolidado.Clave, out suma)) suma = new double[2];
            if (Math.Abs(suma[0] - consolidado.Tuberia) > Tolerancia)
                h.Add(Nuevo(ResumenSeveridad.Aviso, string.Empty, string.Empty,
                    NombreUc(consolidado.Diametro, consolidado.Terreno) + ": la tubería por anillos suma " + Ml(suma[0]) + " ML y el consolidado (como GENERAREXCEL) da " + Ml(consolidado.Tuberia) + " ML.",
                    "Suele pasar cuando un cruce de arroyo o una camisa supera las cotas de su anillo mientras otro anillo tiene cotas de la misma UC: revisa las restas de esa UC."));
            if (Math.Abs(suma[1] - consolidado.Canalizacion) > Tolerancia)
                h.Add(Nuevo(ResumenSeveridad.Aviso, string.Empty, string.Empty,
                    NombreUc(consolidado.Diametro, consolidado.Terreno) + ": la canalización por anillos suma " + Ml(suma[1]) + " ML y el consolidado da " + Ml(consolidado.Canalizacion) + " ML.",
                    "Revisa las restas (camisa, cruce con topo y cruce de arroyo) de esa UC en cada anillo."));
        }
    }

    private static void ArroyoDetalleContraUc(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (ResumenAnillo a in p.Anillos)
        {
            if (!a.TieneDetalle || !a.TieneUc) continue;
            Dictionary<UcKey, double> detalle, enUc;
            p.ArroyoEnDetalle.TryGetValue(a.Clave, out detalle);
            p.ArroyoEnUc.TryGetValue(a.Clave, out enUc);
            detalle = detalle ?? new Dictionary<UcKey, double>();
            enUc = enUc ?? new Dictionary<UcKey, double>();

            var claves = new HashSet<UcKey>(detalle.Keys);
            claves.UnionWith(enUc.Keys);
            foreach (UcKey uc in claves)
            {
                double d, u;
                detalle.TryGetValue(uc, out d);
                enUc.TryGetValue(uc, out u);
                if (Math.Abs(d - u) <= Tolerancia) continue;
                string donde = d <= Tolerancia ? "solo está en el UC (" + Ml(u) + " ML)"
                    : u <= Tolerancia ? "solo está en el DETALLE (" + Ml(d) + " ML)"
                    : "es " + Ml(d) + " ML en el DETALLE y " + Ml(u) + " ML en el UC";
                h.Add(Nuevo(ResumenSeveridad.Aviso, a.Clave, string.Empty,
                    "CRUCE DE ARROYO en " + NombreUc(uc.Diameter, uc.Surface) + " " + donde + ". El Excel de legalización usa el DETALLE; el RESUMEN UC usa el layout UC.",
                    "Deja la misma anotación en los dos layouts (clona la anotación del detalle al UC)."));
            }
        }
    }

    private static void CajetinUniforme(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (KeyValuePair<string, Dictionary<string, List<string>>> campo in p.CajetinPorLayout)
        {
            if (campo.Value.Count <= 1) continue;
            List<KeyValuePair<string, List<string>>> valores = campo.Value.OrderByDescending(v => v.Value.Count).ToList();
            string comun = valores[0].Key;
            foreach (KeyValuePair<string, List<string>> distinto in valores.Skip(1))
                h.Add(Nuevo(ResumenSeveridad.Aviso, string.Empty, string.Empty,
                    campo.Key.ToUpperInvariant() + ": «" + distinto.Key + "» en " + string.Join(", ", distinto.Value.Take(4)) + (distinto.Value.Count > 4 ? " y " + (distinto.Value.Count - 4) + " más" : string.Empty) +
                    ", mientras que en los demás (" + valores[0].Value.Count + " layout" + (valores[0].Value.Count == 1 ? string.Empty : "s") + ") dice «" + comun + "».",
                    "Unifica el cajetín con RELLENARDATOS."));
        }
    }

    // El título del plano (ANILLO 1 - 3/4") dice el diámetro del anillo: debe ser uno de los diámetros de sus cotas.
    // (El campo TUBERIA del cajetín es la marca del tubo, no el diámetro.)
    private static void TituloContraCotas(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        foreach (ResumenAnillo anillo in p.Anillos)
        {
            HashSet<string> titulo;
            if (!p.DiametrosTitulo.TryGetValue(anillo.Clave, out titulo) || titulo.Count == 0) continue;
            var cotas = new HashSet<string>(anillo.Ucs.Where(u => u.Cotas > Tolerancia && !u.EsCruceArroyo).Select(u => u.Diametro), StringComparer.OrdinalIgnoreCase);
            if (cotas.Count == 0 || cotas.Overlaps(titulo)) continue;
            h.Add(Nuevo(ResumenSeveridad.Aviso, anillo.Clave, string.Empty,
                "El título del plano dice " + string.Join(", ", titulo.OrderBy(ResumenCatalogo.OrdenDiametro).Select(d => ResumenCatalogo.Diametro(d))) +
                ", pero las cotas UC de este anillo son de " + string.Join(", ", cotas.OrderBy(ResumenCatalogo.OrdenDiametro).Select(d => ResumenCatalogo.Diametro(d))) + ".",
                "Corrige el diámetro del título (RELLENARDATOS) o el de las cotas."));
        }
    }

    private static void CajetinCompleto(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        if (p.Anillos.Count == 0) return;
        string[][] requeridos = { new[] { "MUNICIPIO" }, new[] { "PROYECTO", "OBRA" }, new[] { "INTERVENTOR" } };
        foreach (string[] opciones in requeridos)
        {
            if (opciones.Any(o => p.Cajetin.ContainsKey(o))) continue;
            h.Add(Nuevo(ResumenSeveridad.Aviso, string.Empty, string.Empty,
                "El cajetín no tiene " + opciones[0] + " diligenciado en ningún plano.",
                "Usa RELLENARDATOS para llenar el cajetín con los datos del proyecto."));
        }
    }

    private static void ContraInformes(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        if (p.Informes == null)
        {
            h.Add(Nuevo(ResumenSeveridad.Info, string.Empty, string.Empty,
                "No se pudo comparar con los informes de RESUMENOBRA" + (string.IsNullOrEmpty(p.InformesProblema) ? "." : ": " + p.InformesProblema),
                string.Empty));
            return;
        }

        foreach (ResumenMaterial m in p.Consolidado.Materiales)
        {
            double proyecto = p.Informes.Quantity(p.Informes.Project, m.Descripcion, m.Diametro);
            double pruebas = p.Informes.Quantity(p.Informes.Tests, m.Descripcion, m.Diametro);
            if (Math.Abs(proyecto - m.Proyecto) <= Tolerancia && Math.Abs(pruebas - m.Pruebas) <= Tolerancia) continue;
            h.Add(Nuevo(ResumenSeveridad.Aviso, string.Empty, string.Empty,
                ResumenCatalogo.Material(m.Descripcion) + " " + ResumenCatalogo.Diametro(m.Diametro) + ": este resumen da " + Ml(m.Proyecto) + " (proyecto) / " + Ml(m.Pruebas) +
                " (pruebas) y RESUMENOBRA da " + Ml(proyecto) + " / " + Ml(pruebas) + ".",
                "Vuelve a generar RESUMENOBRA después de corregir el plano; si persiste, revisa esa UC."));
        }
    }

    private static void Contratista(ResumenProyecto p, List<ResumenHallazgo> h)
    {
        if (p.Anillos.Count == 0 || !string.IsNullOrEmpty(p.Contratista)) return;
        h.Add(Nuevo(ResumenSeveridad.Info, string.Empty, string.Empty,
            "No se encontró el nombre del contratista en el cajetín" + (string.IsNullOrEmpty(p.ContratistaProblema) ? "." : ": " + p.ContratistaProblema + "."),
            "Los formatos que lo piden lo reportarán como dato faltante."));
    }
}
