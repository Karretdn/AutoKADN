using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;
using AutoKADN.Tools.Acotado;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Detalla un anillo que ya está dibujado con lo que dice su croquis (CalcoDetalle), como se hace a mano:
//  · largo de cada tramo nuevo: como COTAK > Longitud, del lado contrario a la cara más cercana, a 5.5.
//  · ubicación de la tubería: cota magenta de la cara a la tubería, perpendicular (COTAK > Ubicación), con el texto del
//    croquis. Se ponen las que quepan: a MinimoEntreUbicaciones de otra del mismo tramo; pegada a un accesorio (a menos de
//    MinimoAAccesorio) o a una esquina de la cara (MargenEsquina) se corre lo justo.
//  · ubicación de un accesorio: cota magenta del accesorio a la cara que tiene adelante en el sentido del tramo (DIMLINEAR
//    pasado a magenta). Si el croquis dice 0.0 va el «LC0.0» de LIMIK con su lineta.
//  · marcas LC: una por lado de cara (entre cruces con otras caras) donde llega una cota magenta, como LIMIK.
// No mueve ni borra nada del dibujo. Todo va en una sola transacción.
internal static class CalcoDetallador
{
    public const string CapaMagenta = "COTAS MAGENTA";
    private const double MinimoEntreUbicaciones = 15.0;
    private const double MinimoAAccesorio = 6.0;
    private const double MargenEsquina = 4.5;
    private const double BuscarCara = 12.0;         // cuánto se corre una ubicación que cae donde no hay cara enfrente        // de una cota magenta a la esquina del lado de cara al que llega
    private const double AlcanceLateral = 25.0;      // qué tan al lado del accesorio puede estar la cara de su cota
    private const double AdentroDeLaCara = 7.6;      // dónde cae la cota del accesorio sobre su cara, desde la punta
    private const double SeparacionLc = 2.0;         // LimiteTool.OffsetFromLine
    private const double LcJuntoACota = 8.0;
    private const double MargenLc = 3.0;
    private const double EscalaLineta = 5.0;         // LimiteTool.DashedLinetypeScale

    public static CalcoResultado Detallar(Database database, ObjectId espacioId, CalcoDetalle detalle, TamanoValores tamanos)
    {
        var resultado = new CalcoResultado();
        using Transaction transaction = database.TransactionManager.StartTransaction();
        var capas = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        ObjectId capaMagenta = CapaMagentaId(transaction, capas);
        var espacio = (BlockTableRecord)transaction.GetObject(espacioId, OpenMode.ForWrite);
        CalcoLectura lectura = CalcoLectorLayout.Leer(transaction, espacio);
        // Cada cota de largo va en la capa de su diámetro (de eso salen los metros por diámetro del Excel): el que diga el
        // detalle, si no el del tramo (cruce y tramitos), si no el del anillo.
        string DiametroDe(CalcoLargo largo, CalcoTramo? tramo) =>
            largo.Diametro.Length > 0 ? largo.Diametro : tramo != null && tramo.Diametro.Length > 0 ? tramo.Diametro : detalle.Diametro;
        List<string> faltan = detalle.Largos.Select(l => CalcoDibujante.CapaCota(DiametroDe(l, lectura.Tramo(l.Tramo)))).Distinct().Where(c => !capas.Has(c)).ToList();
        if (faltan.Count > 0)
        {
            resultado.Error = $"el dibujo no tiene la(s) capa(s) {string.Join(", ", faltan)}; use la plantilla del proyecto";
            return resultado;
        }
        var llegadas = new List<Llegada>();
        // Centro del anillo (la tubería nueva): una cota sin cara al lado (el cruce) va hacia afuera de él.
        List<CalcoTramo> nuevos = lectura.Tramos.Where(t => t.Nuevo).ToList();
        Point3d centro = nuevos.Count == 0 ? Point3d.Origin : new Point3d(
            (nuevos.Min(t => Math.Min(t.Inicio.X, t.Fin.X)) + nuevos.Max(t => Math.Max(t.Inicio.X, t.Fin.X))) / 2.0,
            (nuevos.Min(t => Math.Min(t.Inicio.Y, t.Fin.Y)) + nuevos.Max(t => Math.Max(t.Inicio.Y, t.Fin.Y))) / 2.0, 0.0);

        foreach (CalcoLargo largo in detalle.Largos)
        {
            CalcoTramo? tramo = lectura.Tramo(largo.Tramo);
            if (tramo == null) { resultado.Avisos.Add($"cota de largo «{largo.Texto}»: no hay tramo {largo.Tramo}"); continue; }
            Vector3d normal = Normal(tramo.Direccion);
            double adentro = CaraMasCercana(lectura, tramo.En(0.5), tramo.Direccion, normal, out _, out _);
            Vector3d afuera = double.IsInfinity(adentro) ? ((centro - tramo.En(0.5)).DotProduct(normal) > 0 ? -normal : normal)
                : adentro >= 0 ? -normal : normal;
            var cota = new RotatedDimension(Angulo(tramo.Direccion), tramo.Inicio, tramo.Fin, tramo.En(0.5) + afuera * CotaTool.OffsetFromLine, largo.Texto, database.Dimstyle);
            Preparar(cota, tamanos);
            cota.LayerId = capas[CalcoDibujante.CapaCota(DiametroDe(largo, tramo))];
            Agregar(espacio, transaction, cota);
            resultado.Cotas++;
        }

        var puestas = new List<(CalcoTramo Tramo, double Posicion)>();
        foreach (CalcoUbicacion ubicacion in detalle.Ubicaciones)
        {
            CalcoTramo? tramo = lectura.Tramo(ubicacion.Tramo);
            if (tramo == null) { resultado.Avisos.Add($"ubicación «{ubicacion.Texto}»: no hay tramo {ubicacion.Tramo}"); continue; }
            double posicion = Math.Max(0.0, Math.Min(1.0, ubicacion.En)) * tramo.Largo;
            Point3d enTubo = tramo.Inicio + tramo.Direccion * posicion;
            Vector3d? ladoPedido = Hacia(ubicacion.Hacia);
            double lado = CaraMasCercana(lectura, enTubo, tramo.Direccion, Normal(tramo.Direccion), out Point3d enCara, out CalcoCara? cara, ladoPedido);
            // Si justo ahí no hay cara enfrente (cae pasada la esquina), se busca corriéndola hasta BuscarCara hacia el
            // medio del tramo y luego al otro lado.
            for (int paso = 1; cara == null && paso <= (int)BuscarCara; paso++)
                foreach (double signo in new[] { posicion < tramo.Largo / 2.0 ? 1.0 : -1.0, posicion < tramo.Largo / 2.0 ? -1.0 : 1.0 })
                {
                    double otra = posicion + signo * paso;
                    if (otra < 0.0 || otra > tramo.Largo) continue;
                    lado = CaraMasCercana(lectura, tramo.Inicio + tramo.Direccion * otra, tramo.Direccion, Normal(tramo.Direccion), out enCara, out cara, ladoPedido);
                    if (cara == null) continue;
                    posicion = otra;
                    enTubo = tramo.Inicio + tramo.Direccion * otra;
                    break;
                }
            if (cara == null || double.IsInfinity(lado)) { resultado.Avisos.Add($"ubicación «{ubicacion.Texto}» en {tramo.Id}: no hay cara enfrente"); continue; }

            // Pegada a una esquina de la cara o a un accesorio no se lee: se corre lo justo (como se corre a mano), sin salir
            // del lado de la cara ni del tramo. Si así queda encima de otra cota del mismo tramo, sobra.
            (double desde, double hasta) = Lado(lectura, cara, enCara);
            if (hasta - desde < 2.0 * MargenEsquina) { resultado.Avisos.Add($"ubicación «{ubicacion.Texto}» en {tramo.Id}: el lado de la cara es muy corto"); continue; }
            double sentido = tramo.Direccion.DotProduct(cara.Direccion) >= 0 ? 1.0 : -1.0;
            double pie = (enCara - cara.Inicio).DotProduct(cara.Direccion);
            double a = posicion + (desde + MargenEsquina - pie) * sentido, b = posicion + (hasta - MargenEsquina - pie) * sentido;
            double minimo = Math.Max(0.0, Math.Min(a, b)), maximo = Math.Min(tramo.Largo, Math.Max(a, b));
            var cercanos = lectura.Accesorios
                .Where(x => CalcoLectorLayout.Distancia(x.Posicion, tramo.Inicio - tramo.Direccion * MinimoAAccesorio, tramo.Fin + tramo.Direccion * MinimoAAccesorio) <= 1.0)
                .Select(x => (x.Posicion - tramo.Inicio).DotProduct(tramo.Direccion)).ToList();
            var opciones = new List<double> { Math.Max(minimo, Math.Min(maximo, posicion)) };
            foreach (double s in cercanos) { opciones.Add(s - MinimoAAccesorio); opciones.Add(s + MinimoAAccesorio); }
            double? elegida = opciones
                .Where(o => o >= minimo - 1e-6 && o <= maximo + 1e-6 && cercanos.All(s => Math.Abs(o - s) >= MinimoAAccesorio - 1e-6))
                .OrderBy(o => Math.Abs(o - posicion)).Select(o => (double?)o).FirstOrDefault();
            if (elegida == null || puestas.Any(p => p.Tramo == tramo && Math.Abs(p.Posicion - elegida.Value) < MinimoEntreUbicaciones))
            {
                resultado.Avisos.Add($"ubicación «{ubicacion.Texto}» en {tramo.Id}: no cabe (muy cerca de otra cota, de un accesorio o de una esquina)");
                continue;
            }
            enTubo += tramo.Direccion * (elegida.Value - posicion);
            enCara += tramo.Direccion * (elegida.Value - posicion);
            posicion = elegida.Value;
            var cota = new RotatedDimension(Angulo(enTubo - enCara), enCara, enTubo, enCara + (enTubo - enCara) * 0.5, ubicacion.Texto, database.Dimstyle);
            Preparar(cota, tamanos);
            cota.LayerId = capaMagenta;
            GirarSiNoCabe(cota, ubicacion.Texto, enTubo - enCara, cara.Direccion);
            Agregar(espacio, transaction, cota);
            puestas.Add((tramo, posicion));
            llegadas.Add(new Llegada(cara, enCara, enTubo));
            resultado.Cotas++;
        }

        foreach (CalcoCotaAccesorio cotaAccesorio in detalle.Accesorios)
        {
            CalcoAccesorioLeido? accesorio = lectura.Accesorio(cotaAccesorio.Accesorio);
            if (accesorio == null) { resultado.Avisos.Add($"cota «{cotaAccesorio.Texto}»: no hay accesorio {cotaAccesorio.Accesorio}"); continue; }
            if (EsCero(cotaAccesorio.Texto))
            {
                if (!LcCero(database, transaction, espacio, capaMagenta, lectura, accesorio, tamanos)) resultado.Avisos.Add($"{accesorio.Id}: no hay una cara alineada con el accesorio para el LC0.0");
                else resultado.Textos++;
                continue;
            }
            Vector3d? hacia = Hacia(cotaAccesorio.Hacia);
            if (hacia == null) { resultado.Avisos.Add($"{accesorio.Id}: dirección «{cotaAccesorio.Hacia}» no válida (arriba, abajo, izquierda, derecha)"); continue; }
            // La cara va del lado de la manzana que acompaña la tubería del accesorio (no la de enfrente de la calle).
            Vector3d? haciaManzana = null;
            if (accesorio.Tramo != null)
            {
                double d = CaraMasCercana(lectura, accesorio.Posicion, accesorio.Tramo.Direccion, Normal(accesorio.Tramo.Direccion), out _, out CalcoCara? propia);
                if (propia != null && !double.IsInfinity(d)) haciaManzana = Normal(accesorio.Tramo.Direccion) * Math.Sign(d);
            }
            if (!CaraAdelante(lectura, accesorio.Posicion, hacia.Value, haciaManzana, tamanos.Predial, out Point3d enCara, out CalcoCara? cara))
            {
                resultado.Avisos.Add($"{accesorio.Id}: no hay cara {cotaAccesorio.Hacia} del accesorio para la cota «{cotaAccesorio.Texto}»");
                continue;
            }
            Point3d desde = VerticeDelBloque(transaction, accesorio, enCara, hacia.Value);
            var cota = new RotatedDimension(Angulo(hacia.Value), desde, enCara, enCara, cotaAccesorio.Texto, database.Dimstyle);
            Preparar(cota, tamanos);
            cota.LayerId = capaMagenta;
            Agregar(espacio, transaction, cota);
            TextoAlLado(cota, cotaAccesorio.Texto, accesorio.Posicion, enCara, hacia.Value);
            llegadas.Add(new Llegada(cara!, enCara, accesorio.Posicion));
            resultado.Cotas++;
        }

        resultado.Textos += MarcasLc(database, transaction, espacio, lectura, llegadas, tamanos);
        transaction.Commit();
        return resultado;
    }

    private sealed class Llegada
    {
        public Llegada(CalcoCara cara, Point3d punto, Point3d desde) { Cara = cara; Punto = punto; Desde = desde; }
        public CalcoCara Cara { get; }
        public Point3d Punto { get; }
        public Point3d Desde { get; }
    }

    private static void Preparar(RotatedDimension cota, TamanoValores tamanos)
    {
        // Mismos valores que COTAK (CotaTool / UbicacionTool).
        cota.Dimscale = tamanos.Cotas;
        cota.Dimtad = 0;
        cota.Dimjust = 0;
        cota.Dimtih = true;
        cota.Dimtoh = true;
        cota.ColorIndex = 256;
    }

    // De dónde sale la cota de un accesorio: del vértice del bloque que la línea de extensión toca primero (el canto del
    // bloque del lado de la cota, a la altura de su punto de inserción), para no pasar por encima del bloque.
    private static Point3d VerticeDelBloque(Transaction transaction, CalcoAccesorioLeido accesorio, Point3d enCara, Vector3d hacia)
    {
        if (accesorio.Bloque.IsNull || transaction.GetObject(accesorio.Bloque, OpenMode.ForRead) is not BlockReference bloque) return accesorio.Posicion;
        Extents3d caja;
        try { caja = bloque.GeometricExtents; } catch (Autodesk.AutoCAD.Runtime.Exception) { return accesorio.Posicion; }
        Vector3d lateral = Normal(hacia);
        if ((enCara - accesorio.Posicion).DotProduct(lateral) < 0) lateral = -lateral; // hacia la línea de cota
        double borde = new[] { caja.MinPoint, caja.MaxPoint, new Point3d(caja.MinPoint.X, caja.MaxPoint.Y, 0), new Point3d(caja.MaxPoint.X, caja.MinPoint.Y, 0) }
            .Max(p => (p - accesorio.Posicion).DotProduct(lateral));
        return accesorio.Posicion + lateral * Math.Max(0.0, borde);
    }

    // Cota de accesorio tan corta que su texto no cabe entre las líneas de extensión: el texto va al lado de la línea de
    // cota (sin directriz), del lado del accesorio, que es donde no está la marca LC de la cara.
    private static void TextoAlLado(RotatedDimension cota, string texto, Point3d accesorio, Point3d enCara, Vector3d hacia)
    {
        double alto = cota.Dimtxt * cota.Dimscale, ancho = 0.8 * alto * Math.Max(1, texto.Trim().Length);
        double largo = Math.Abs((enCara - accesorio).DotProduct(hacia));
        bool horizontal = Math.Abs(hacia.X) > Math.Abs(hacia.Y);
        double ocupa = horizontal ? ancho : alto; // el texto va horizontal: a lo largo de la cota ocupa su ancho o su alto
        if (ocupa + 2.0 * cota.Dimgap * cota.Dimscale <= largo) return;
        Vector3d lateral = Normal(hacia);
        double lado = (accesorio - enCara).DotProduct(lateral);
        if (Math.Abs(lado) < 1e-6) lado = -1.0;
        double aparte = (horizontal ? alto : ancho) / 2.0 + cota.Dimgap * cota.Dimscale + 0.5;
        Point3d medio = enCara - hacia * (largo / 2.0);
        cota.Dimtmove = 2; // el texto se mueve sin directriz
        cota.TextPosition = medio + lateral * Math.Sign(lado) * aparte;
    }

    // El texto de la cota va horizontal (Dimtih/Dimtoh). En una cota horizontal angosta (de la tubería a una cara vertical)
    // no cabe entre las dos líneas: se gira 90°, a lo largo de la cara, para que no las tape.
    private static void GirarSiNoCabe(RotatedDimension cota, string texto, Vector3d medida, Vector3d cara)
    {
        double alto = cota.Dimtxt * cota.Dimscale, ancho = 0.8 * alto * Math.Max(1, texto.Trim().Length);
        double hueco = medida.Length - 2.0 * cota.Dimgap * cota.Dimscale;
        double aLoLargo = Math.Abs(medida.GetNormal().X) * ancho + Math.Abs(medida.GetNormal().Y) * alto; // lo que ocupa el texto horizontal en la cota
        if (aLoLargo <= hueco) return;
        double giro = Math.Atan2(cara.Y, cara.X);
        if (giro > Math.PI / 2.0 || giro <= -Math.PI / 2.0) giro += giro > 0.0 ? -Math.PI : Math.PI; // legible
        cota.TextRotation = giro;
    }

    private static void Agregar(BlockTableRecord espacio, Transaction transaction, Entity entidad)
    {
        espacio.AppendEntity(entidad);
        transaction.AddNewlyCreatedDBObject(entidad, true);
    }

    private static Vector3d Normal(Vector3d direccion) => new Vector3d(-direccion.Y, direccion.X, 0.0).GetNormal();

    private static double Angulo(Vector3d v) => Math.Atan2(v.Y, v.X);

    private static bool EsCero(string texto)
    {
        string t = texto.Trim().ToUpperInvariant().Replace("LC", "");
        return t == "0" || t == "0.0" || t == "0.00";
    }

    private static Vector3d? Hacia(string hacia)
    {
        switch (hacia)
        {
            case "arriba": return Vector3d.YAxis;
            case "abajo": return -Vector3d.YAxis;
            case "derecha": return Vector3d.XAxis;
            case "izquierda": return -Vector3d.XAxis;
            default: return null;
        }
    }

    private static bool Paralelas(Vector3d a, Vector3d b) => Math.Abs(a.DotProduct(b)) >= 0.999;

    // Cara paralela al tramo que tiene enfrente el punto, a uno u otro lado. Devuelve la distancia con signo (positiva hacia
    // la normal) o infinito si no hay ninguna.
    private static double CaraMasCercana(CalcoLectura lectura, Point3d punto, Vector3d direccion, Vector3d normal, out Point3d enCara, out CalcoCara? cara, Vector3d? soloHacia = null)
    {
        double mejor = double.PositiveInfinity;
        enCara = punto;
        cara = null;
        foreach (CalcoCara candidata in lectura.Caras)
        {
            if (!Paralelas(candidata.Direccion, direccion)) continue;
            double a = (candidata.Inicio - punto).DotProduct(direccion), b = (candidata.Fin - punto).DotProduct(direccion);
            if (Math.Min(a, b) > 1e-6 || Math.Max(a, b) < -1e-6) continue; // la perpendicular no cae sobre la cara
            double distancia = (candidata.Inicio - punto).DotProduct(normal);
            if (Math.Abs(distancia) < 1e-6 || Math.Abs(distancia) >= Math.Abs(mejor)) continue;
            if (soloHacia is Vector3d h && (normal * distancia).DotProduct(h) <= 0) continue; // del lado que pide el croquis
            mejor = distancia;
            enCara = punto + normal * distancia;
            cara = candidata;
        }
        return mejor;
    }

    // Primera cara perpendicular a «hacia» que el accesorio tiene adelante. La cara puede no llegar hasta la línea del
    // accesorio (la torre empieza más allá de la tubería): vale si está a menos de AlcanceLateral a un lado; la cota cae
    // sobre la cara a AdentroDeLaCara de su punta.
    private static bool CaraAdelante(CalcoLectura lectura, Point3d punto, Vector3d hacia, Vector3d? haciaManzana, double altoTexto, out Point3d enCara, out CalcoCara? cara)
    {
        double mejor = double.MaxValue;
        enCara = punto;
        cara = null;
        Vector3d lateral = Normal(hacia);
        if (haciaManzana is Vector3d m && lateral.DotProduct(m) < 0) lateral = -lateral; // lateral positivo = hacia la manzana
        foreach (CalcoCara candidata in lectura.Caras)
        {
            if (Math.Abs(candidata.Direccion.DotProduct(hacia)) > 0.001) continue;
            double adelante = (candidata.Inicio - punto).DotProduct(hacia);
            if (adelante <= 0.5 || adelante >= mejor) continue;
            double a = (candidata.Inicio - punto).DotProduct(lateral), b = (candidata.Fin - punto).DotProduct(lateral);
            double desde = Math.Min(a, b), hasta = Math.Max(a, b);
            if (haciaManzana != null && hasta < 0.5) continue; // está del otro lado de la tubería (otra manzana)
            double hueco = desde > 0 ? desde : hasta < 0 ? -hasta : 0.0;
            if (hueco > AlcanceLateral) continue;
            double cae = desde > 0 ? Math.Min(hasta, desde + AdentroDeLaCara) : hasta < 0 ? Math.Max(desde, hasta - AdentroDeLaCara) : 0.0;
            if (desde > 0)
            {
                // Dentro de la manzana si no pasa por encima de un texto (se busca más adentro); si no, en el espacio
                // entre la tubería y la manzana.
                bool Libre(double c) => !lectura.Textos.Any(t => Cruza(punto + lateral * c, punto + hacia * adelante + lateral * c, t.Texto, t.Posicion, t.Giro, altoTexto));
                var opciones = new List<double>();
                for (double c = desde + AdentroDeLaCara; c <= hasta - 1.0; c += 2.0) opciones.Add(c);
                opciones.Add(desde / 2.0);
                cae = opciones.Where(Libre).Select(c => (double?)c).FirstOrDefault() ?? cae;
            }
            mejor = adelante;
            enCara = punto + hacia * adelante + lateral * cae;
            cara = candidata;
        }
        return cara != null;
    }

    // ¿El segmento a–b pasa por la caja aproximada de un texto (0.9 × alto por letra, centrada en su punto)?
    private static bool Cruza(Point3d a, Point3d b, string texto, Point3d centro, double giro, double alto)
    {
        double ancho = 0.9 * alto * Math.Max(1, texto.Length) / 2.0 + 0.5, medio = alto / 2.0 + 0.5;
        bool vertical = Math.Abs(Math.Sin(giro)) > 0.7;
        double hx = vertical ? medio : ancho, hy = vertical ? ancho : medio;
        double x0 = centro.X - hx, x1 = centro.X + hx, y0 = centro.Y - hy, y1 = centro.Y + hy;
        for (int i = 0; i <= 20; i++)
        {
            Point3d p = a + (b - a) * (i / 20.0);
            if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1) return true;
        }
        return false;
    }

    // «LC0.0» de LIMIK: el accesorio queda en la prolongación de una cara. Lineta magenta punteada del accesorio a la punta
    // de la cara (2 más corta a cada lado) y el texto sobre la cara, junto a esa punta.
    private static bool LcCero(Database database, Transaction transaction, BlockTableRecord espacio, ObjectId capa, CalcoLectura lectura, CalcoAccesorioLeido accesorio, TamanoValores tamanos)
    {
        Point3d punto = accesorio.Posicion;
        CalcoCara? mejor = null;
        Point3d punta = punto;
        double distancia = AlcanceLateral;
        foreach (CalcoCara cara in lectura.Caras)
        {
            Vector3d d = cara.Direccion;
            if (Math.Abs((punto - cara.Inicio).DotProduct(Normal(d))) > 0.5) continue; // no está en su prolongación
            foreach (Point3d extremo in new[] { cara.Inicio, cara.Fin })
            {
                double a = extremo.DistanceTo(punto);
                if (a >= distancia) continue;
                distancia = a; mejor = cara; punta = extremo;
            }
        }
        if (mejor == null || distancia <= 2.0 * SeparacionLc) return false;

        Vector3d haciaCara = (punta - punto).GetNormal();
        // La lineta arranca en el canto del bloque (no encima de él).
        double canto = SeparacionLc;
        if (!accesorio.Bloque.IsNull && transaction.GetObject(accesorio.Bloque, OpenMode.ForRead) is BlockReference bloque)
        {
            try
            {
                Extents3d caja = bloque.GeometricExtents;
                canto = Math.Max(canto, new[] { caja.MinPoint, caja.MaxPoint }.Max(p => (p - punto).DotProduct(haciaCara)) + 0.5);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
        }
        if (distancia <= canto + SeparacionLc) return false;
        var lineta = new Line(punto + haciaCara * canto, punta - haciaCara * SeparacionLc) { LayerId = capa, ColorIndex = 256, LinetypeScale = EscalaLineta };
        ObjectId punteada = TipoDeLinea(database, transaction, "DASHED");
        if (!punteada.IsNull) lineta.LinetypeId = punteada;
        Agregar(espacio, transaction, lineta);

        Vector3d normal = Normal(haciaCara);
        if (normal.Y < 0 || (Math.Abs(normal.Y) < 1e-9 && normal.X > 0)) normal = -normal; // arriba o a la izquierda
        Point3d centro = punta + haciaCara * (SeparacionLc + 2.0 * tamanos.Limites) + normal * SeparacionLc;
        AgregarTexto(database, transaction, espacio, capa, "LC0.0", centro, haciaCara, tamanos);
        return true;
    }

    // Una LC por lado de cara (el pedazo entre cruces con otras caras) al que llega una cota magenta: entre las cotas de ese
    // lado, o a LcJuntoACota de la única hacia el medio del lado; hacia adentro (el lado con más espacio), como LIMIK.
    private static int MarcasLc(Database database, Transaction transaction, BlockTableRecord espacio, CalcoLectura lectura, List<Llegada> llegadas, TamanoValores tamanos)
    {
        ObjectId capa = database.Clayer;
        var hechas = 0;
        foreach (var grupo in llegadas.GroupBy(l => (l.Cara.Indice, Lado(lectura, l.Cara, l.Punto))))
        {
            CalcoCara cara = grupo.First().Cara;
            (double desde, double hasta) = grupo.Key.Item2;
            Vector3d d = cara.Direccion;
            List<double> pies = grupo.Select(l => (l.Punto - cara.Inicio).DotProduct(d)).OrderBy(p => p).ToList();
            // Con varias cotas en el lado, la LC va entre ellas; con una sola, junto a ella hacia el medio del lado.
            double medio = (desde + hasta) / 2.0;
            double posicion = pies.Count > 1 ? (pies[0] + pies[pies.Count - 1]) / 2.0
                : pies[0] + Math.Sign(medio - pies[0]) * Math.Min(LcJuntoACota, Math.Abs(medio - pies[0]));
            posicion = Math.Max(desde + MargenLc, Math.Min(hasta - MargenLc, posicion));
            Point3d enCara = cara.Inicio + d * posicion;
            Vector3d normal = Normal(d);
            if (Holgura(lectura, cara, enCara, -normal) > Holgura(lectura, cara, enCara, normal)) normal = -normal; // hacia adentro de la torre o manzana
            AgregarTexto(database, transaction, espacio, capa, "LC", enCara + normal * SeparacionLc, d, tamanos);
            hechas++;
        }
        return hechas;
    }

    // Espacio libre a un lado de la cara: hasta la cara paralela o el tramo de tubería más cercano en esa dirección. El lado
    // con más espacio es el de adentro (la torre o la manzana); el otro es la tubería o una franja angosta.
    private static double Holgura(CalcoLectura lectura, CalcoCara cara, Point3d punto, Vector3d hacia)
    {
        double holgura = double.MaxValue;
        IEnumerable<(Point3d A, Point3d B)> lineas = lectura.Caras.Where(c => c != cara).Select(c => (c.Inicio, c.Fin))
            .Concat(lectura.Tramos.Select(t => (t.Inicio, t.Fin)));
        Vector3d d = cara.Direccion;
        foreach ((Point3d a, Point3d b) in lineas)
        {
            Vector3d otra = b - a;
            if (otra.Length < 1e-9 || !Paralelas(otra.GetNormal(), d)) continue;
            double pa = (a - punto).DotProduct(d), pb = (b - punto).DotProduct(d);
            if (Math.Min(pa, pb) > 1e-6 || Math.Max(pa, pb) < -1e-6) continue;
            double distancia = (a - punto).DotProduct(hacia);
            if (distancia > 0.05 && distancia < holgura) holgura = distancia;
        }
        return holgura;
    }

    // Lado de la cara (desde, hasta, medidos desde su inicio) entre los cruces con otras caras que contiene el punto.
    private static (double, double) Lado(CalcoLectura lectura, CalcoCara cara, Point3d punto)
    {
        Vector3d d = cara.Direccion, n = Normal(d);
        var cortes = new List<double> { 0.0, cara.Largo };
        foreach (CalcoCara otra in lectura.Caras)
        {
            if (otra == cara || Paralelas(otra.Direccion, d)) continue;
            double a = (otra.Inicio - cara.Inicio).DotProduct(n), b = (otra.Fin - cara.Inicio).DotProduct(n);
            if (Math.Min(a, b) > 0.05 || Math.Max(a, b) < -0.05) continue; // no toca la línea de la cara
            double t = Math.Abs(b - a) < 1e-9 ? 0.0 : a / (a - b);
            double corte = (otra.Inicio + (otra.Fin - otra.Inicio) * t - cara.Inicio).DotProduct(d);
            if (corte > 0.05 && corte < cara.Largo - 0.05) cortes.Add(corte);
        }
        cortes.Sort();
        double pie = (punto - cara.Inicio).DotProduct(d);
        for (int i = 0; i + 1 < cortes.Count; i++)
            if (pie >= cortes[i] - 1e-6 && pie <= cortes[i + 1] + 1e-6) return (Math.Round(cortes[i], 4), Math.Round(cortes[i + 1], 4));
        return (0.0, Math.Round(cara.Largo, 4));
    }

    private static void AgregarTexto(Database database, Transaction transaction, BlockTableRecord espacio, ObjectId capa, string contenido, Point3d centro, Vector3d direccion, TamanoValores tamanos)
    {
        double giro = Math.Atan2(direccion.Y, direccion.X);
        if (giro > Math.PI / 2.0 || giro <= -Math.PI / 2.0) giro += giro > 0.0 ? -Math.PI : Math.PI; // legible, como LIMIK
        var texto = new DBText();
        texto.SetDatabaseDefaults(database);
        texto.TextString = contenido;
        texto.Height = tamanos.Limites;
        texto.LayerId = capa;
        texto.ColorIndex = 256;
        texto.HorizontalMode = TextHorizontalMode.TextCenter;
        texto.VerticalMode = TextVerticalMode.TextVerticalMid;
        texto.Position = centro;
        texto.AlignmentPoint = centro;
        texto.Rotation = giro;
        Agregar(espacio, transaction, texto);
        texto.AdjustAlignment(database);
        SizeTags.Tag(texto, database, transaction, Tamanos.KindLimite);
    }

    private static ObjectId CapaMagentaId(Transaction transaction, LayerTable capas)
    {
        if (capas.Has(CapaMagenta)) return capas[CapaMagenta];
        capas.UpgradeOpen();
        var capa = new LayerTableRecord { Name = CapaMagenta, Color = Color.FromColorIndex(ColorMethod.ByAci, 6) };
        ObjectId id = capas.Add(capa);
        transaction.AddNewlyCreatedDBObject(capa, true);
        return id;
    }

    private static ObjectId TipoDeLinea(Database database, Transaction transaction, string nombre)
    {
        var tabla = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (tabla.Has(nombre)) return tabla[nombre];
        try
        {
            database.LoadLineTypeFile(nombre, "acad.lin");
            tabla = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            return tabla.Has(nombre) ? tabla[nombre] : ObjectId.Null;
        }
        catch (System.Exception)
        {
            return ObjectId.Null;
        }
    }
}
