using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;
using AutoKADN.Tools.Acotado;
using AutoKADN.Tools.Bloques;
using AutoKADN.Tools.Layouts;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Dibuja un CalcoPlano en el espacio de un layout ANILLO n DETALLE con las mismas convenciones que dejan las herramientas,
// para que CLONAR UC, DEBUGDETALLES, LISTABLOQUES y GENERAREXCEL lo reconozcan igual que un dibujo hecho a mano:
//  · manzanas, predios, construcciones, casas y vecinos: polilíneas en la capa Mat, color por capa.
//  · cercas: capa Lineas, color por capa, linea_X_linea a escala 0.8 (como las de los planos).
//  · tubería nueva: capa Lineas, azul, continua; existente: azul, HIDDEN2 a escala 1.5 (la leyenda de la plantilla).
//  · textos: centrados en la capa Mat, alto de TAMAÑOS (Vial o Predial) y su marca para TAMAÑOS, como NOMENK.
//  · cotas de longitud: como COTAK > Longitud (Dimscale de TAMAÑOS, capa COTA_<diámetro>, a 5.5 de la tubería).
//  · accesorios: bloque dinámico insertado de NUEVO (una copia pierde el vínculo dinámico y no se cuenta), capa Mat, verde,
//    escala de TAMAÑOS, DIAMETRO, UC, visibilidad ANILLO, giro en su parámetro de ángulo y el XData MATERIAL de LISTABLOQUES.
//  · indicador de anillo: copia del que trae la plantilla junto al marco (o uno igual dibujado de cero si no lo hay).
// Todo va en una sola transacción: si falta una capa imprescindible no se dibuja nada.
internal static class CalcoDibujante
{
    public const string CapaDibujo = "Mat";
    public const string CapaTuberia = "Lineas";
    public const short ColorTuberia = 5;            // azul
    public const string LineaExistente = "HIDDEN2";
    public const double EscalaLineaExistente = 1.5;
    public const string LineaCerca = "linea_X_linea";
    public const double EscalaLineaCerca = 0.8;
    public const short ColorAccesorios = 3;         // verde, como en los planos
    // Indicador de la plantilla (por si el dibujo no trae uno para copiar).
    private const double RadioIndicador = 6.746;
    private const double AltoNumeroIndicador = 4.8;
    private const double AltoDiametroIndicador = 2.88;
    private const double SeparacionViviendas = 2.2;  // del borde del círculo al centro del texto de viviendas

    private static readonly Regex Digitos = new Regex(@"\d+");
    private static readonly Regex DiametroIndicador = new Regex(@"[∅Øø]\s*(?<d>[0-9][0-9/xX\-]*)");

    // conCotas: false en «Calcar con IA» (las cotas las pone «Detallar con IA»); CALCOPLANO las dibuja.
    public static CalcoResultado Dibujar(Database database, ObjectId espacioId, string layout, CalcoPlano plano, TamanoValores tamanos, bool conCotas = true)
    {
        var resultado = new CalcoResultado();
        using Transaction transaction = database.TransactionManager.StartTransaction();

        var capas = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var necesarias = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { CapaDibujo };
        if (plano.Lineas.Any(l => EsTuberia(l.Tipo) || l.Tipo == CalcoTipoLinea.Cerca)) necesarias.Add(CapaTuberia);
        if (conCotas) foreach (CalcoCota cota in plano.Cotas) necesarias.Add(CapaCota(cota.Diametro));
        List<string> faltan = necesarias.Where(capa => !capas.Has(capa)).ToList();
        if (faltan.Count > 0)
        {
            resultado.Error = $"el dibujo no tiene la(s) capa(s) {string.Join(", ", faltan)}; use la plantilla del proyecto";
            return resultado;
        }

        var espacio = (BlockTableRecord)transaction.GetObject(espacioId, OpenMode.ForWrite);
        // El indicador se busca antes de agregar nada, para copiar el que ya está en el layout.
        List<Entity> indicadorBase = BuscarIndicador(transaction, espacio, out Point3d centroBase);
        ObjectId capaDibujo = capas[CapaDibujo];

        DibujarLineas(database, transaction, espacio, capas, plano, resultado);
        DibujarTextos(database, transaction, espacio, capaDibujo, plano, tamanos, resultado);
        if (conCotas) DibujarCotas(database, transaction, espacio, capas, plano, tamanos, resultado);
        DibujarAccesorios(database, transaction, espacio, capaDibujo, layout, plano, tamanos, resultado);
        DibujarIndicadores(database, transaction, espacio, capaDibujo, indicadorBase, centroBase, plano, tamanos, resultado);
        AvisarFueraDelMarco(plano, resultado);

        transaction.Commit();
        return resultado;
    }

    public static string CapaCota(string diametro) => "COTA_" + Naming.NormalizeDiameter(diametro).Replace('/', '-');

    private static bool EsTuberia(CalcoTipoLinea tipo) => tipo == CalcoTipoLinea.TuberiaNueva || tipo == CalcoTipoLinea.TuberiaExistente;

    private static Point3d P(CalcoPunto punto) => new Point3d(punto.X, punto.Y, 0.0);

    private static void Agregar(BlockTableRecord espacio, Transaction transaction, Entity entidad)
    {
        espacio.AppendEntity(entidad);
        transaction.AddNewlyCreatedDBObject(entidad, true);
    }

    private static void DibujarLineas(Database database, Transaction transaction, BlockTableRecord espacio, LayerTable capas, CalcoPlano plano, CalcoResultado resultado)
    {
        ObjectId existente = ObjectId.Null;
        if (plano.Lineas.Any(l => l.Tipo == CalcoTipoLinea.TuberiaExistente))
        {
            existente = TipoDeLinea(database, transaction, LineaExistente);
            if (existente.IsNull) resultado.Avisos.Add($"no se encontró el tipo de línea {LineaExistente}: la tubería existente quedó continua");
        }
        ObjectId cerca = ObjectId.Null;
        if (plano.Lineas.Any(l => l.Tipo == CalcoTipoLinea.Cerca))
        {
            cerca = TipoDeLinea(database, transaction, LineaCerca);
            if (cerca.IsNull) resultado.Avisos.Add($"no se encontró el tipo de línea {LineaCerca} (el de la plantilla): las cercas quedaron continuas");
        }

        // Cada lado va suelto (una polilínea de 2 vértices por segmento, no un rectángulo unido): así se dibuja a mano y es
        // más fácil de editar.
        foreach (CalcoLinea linea in plano.Lineas)
        {
            if (linea.Puntos.Count < 2) { resultado.Avisos.Add("se omitió una línea con menos de 2 puntos"); continue; }
            int segmentos = linea.Cerrada ? linea.Puntos.Count : linea.Puntos.Count - 1;
            for (int s = 0; s < segmentos; s++)
            {
                CalcoPunto a = linea.Puntos[s], b = linea.Puntos[(s + 1) % linea.Puntos.Count];
                if (Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9) continue;
                var polilinea = new Polyline();
                polilinea.SetDatabaseDefaults(database);
                polilinea.AddVertexAt(0, new Point2d(a.X, a.Y), 0.0, 0.0, 0.0);
                polilinea.AddVertexAt(1, new Point2d(b.X, b.Y), 0.0, 0.0, 0.0);
                Estilo(polilinea, linea.Tipo, capas, existente, cerca);
                Agregar(espacio, transaction, polilinea);
            }
            resultado.Lineas++;
        }
    }

    private static void Estilo(Polyline polilinea, CalcoTipoLinea tipo, LayerTable capas, ObjectId existente, ObjectId cerca)
    {
        if (tipo == CalcoTipoLinea.Cerca)
        {
            polilinea.LayerId = capas[CapaTuberia];
            polilinea.ColorIndex = 256;
            if (!cerca.IsNull)
            {
                polilinea.LinetypeId = cerca;
                polilinea.LinetypeScale = EscalaLineaCerca;
            }
            return;
        }
        if (!EsTuberia(tipo))
        {
            polilinea.LayerId = capas[CapaDibujo];
            polilinea.ColorIndex = 256;
            return;
        }
        polilinea.LayerId = capas[CapaTuberia];
        polilinea.ColorIndex = ColorTuberia;
        if (tipo == CalcoTipoLinea.TuberiaExistente && !existente.IsNull)
        {
            polilinea.LinetypeId = existente;
            polilinea.LinetypeScale = EscalaLineaExistente;
        }
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
            return ObjectId.Null; // sin acad.lin o sin ese tipo de línea en él
        }
    }

    private static void DibujarTextos(Database database, Transaction transaction, BlockTableRecord espacio, ObjectId capa, CalcoPlano plano, TamanoValores tamanos, CalcoResultado resultado)
    {
        foreach (CalcoTexto texto in plano.Textos)
        {
            if (string.IsNullOrWhiteSpace(texto.Texto)) continue;
            string? tipo = texto.Tipo == CalcoTipoTexto.Vial ? Tamanos.KindVial : texto.Tipo == CalcoTipoTexto.Predial ? Tamanos.KindPredial : null;
            double alto = (texto.Tipo == CalcoTipoTexto.Vial ? tamanos.Vial : tamanos.Predial) * (texto.Escala > 0 ? texto.Escala : 1.0);
            DBText entidad = CrearTexto(database, capa, texto.Texto.Trim(), P(texto.Centro), texto.Giro, alto);
            Agregar(espacio, transaction, entidad);
            entidad.AdjustAlignment(database);
            if (tipo != null && Math.Abs(texto.Escala - 1.0) < 1e-3) SizeTags.Tag(entidad, database, transaction, tipo);
            resultado.Textos++;
        }
    }

    // Texto de una línea centrado en su punto, como lo deja NOMENK.
    private static DBText CrearTexto(Database database, ObjectId capa, string contenido, Point3d centro, double giro, double alto)
    {
        var texto = new DBText();
        texto.SetDatabaseDefaults(database);
        texto.TextString = contenido;
        texto.Height = alto;
        texto.LayerId = capa;
        texto.ColorIndex = 256;
        texto.HorizontalMode = TextHorizontalMode.TextCenter;
        texto.VerticalMode = TextVerticalMode.TextVerticalMid;
        texto.Position = centro;
        texto.AlignmentPoint = centro;
        texto.Rotation = giro;
        return texto;
    }

    private static void DibujarCotas(Database database, Transaction transaction, BlockTableRecord espacio, LayerTable capas, CalcoPlano plano, TamanoValores tamanos, CalcoResultado resultado)
    {
        Point3d centroManzana = P(plano.CentroManzana);
        foreach (CalcoCota cota in plano.Cotas)
        {
            Point3d a = P(cota.A), b = P(cota.B);
            Vector3d tramo = b - a;
            if (tramo.Length <= Tolerance.Global.EqualPoint) { resultado.Avisos.Add($"se omitió la cota «{cota.Texto}»: el tramo no tiene largo"); continue; }

            Point3d medio = a + tramo * 0.5;
            Vector3d normal = new Vector3d(-tramo.Y, tramo.X, 0.0).GetNormal();
            if (cota.Lado.HasValue) { if ((P(cota.Lado.Value) - medio).DotProduct(normal) < 0.0) normal = -normal; }
            else if ((centroManzana - medio).DotProduct(normal) > 0.0) normal = -normal;

            // Mismos valores que CotaTool.CreateDimension (COTAK > Longitud).
            var dimension = new RotatedDimension(Math.Atan2(tramo.Y, tramo.X), a, b, medio + normal * CotaTool.OffsetFromLine, string.Empty, database.Dimstyle)
            {
                Dimscale = tamanos.Cotas,
                Dimtad = 0,
                Dimjust = 0,
                Dimtih = true,
                Dimtoh = true,
            };
            dimension.LayerId = capas[CapaCota(cota.Diametro)];
            dimension.ColorIndex = 256;
            if (!string.IsNullOrWhiteSpace(cota.Texto)) dimension.DimensionText = cota.Texto.Trim();
            Agregar(espacio, transaction, dimension);
            resultado.Cotas++;
        }
    }

    private static void DibujarAccesorios(Database database, Transaction transaction, BlockTableRecord espacio, ObjectId capa, string layout, CalcoPlano plano, TamanoValores tamanos, CalcoResultado resultado)
    {
        var bloques = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (CalcoAccesorio accesorio in plano.Accesorios)
        {
            if (accesorio.Nombre.Length == 0 || !bloques.Has(accesorio.Nombre))
            {
                resultado.Avisos.Add($"no se dibujó {(accesorio.Nombre.Length == 0 ? "un accesorio sin nombre" : "el accesorio " + accesorio.Nombre)}: el bloque no está en el dibujo");
                continue;
            }

            var definicion = (BlockTableRecord)transaction.GetObject(bloques[accesorio.Nombre], OpenMode.ForRead);
            var bloque = new BlockReference(P(accesorio.Posicion), definicion.ObjectId);
            bloque.SetDatabaseDefaults(database);
            bloque.LayerId = capa;
            bloque.ColorIndex = ColorAccesorios;
            bloque.ScaleFactors = new Scale3d(tamanos.Bloques);
            Agregar(espacio, transaction, bloque);
            AgregarAtributos(transaction, definicion, bloque);

            string diametro = Naming.NormalizeDiameter(accesorio.Diametro);
            string superficie = Naming.NormalizeSurface(accesorio.Superficie);
            if (!FijarOpcion(bloque, "DIAMETRO", diametro + "\"", valor => Naming.NormalizeDiameter(valor) == diametro))
                resultado.Avisos.Add($"{accesorio.Nombre}: el bloque no tiene el diámetro {diametro}\"");
            if (!FijarOpcion(bloque, "UC", superficie, valor => Naming.NormalizeSurface(valor) == superficie))
                resultado.Avisos.Add($"{accesorio.Nombre}: el bloque no tiene la UC {superficie}");
            if (!FijarOpcion(bloque, "Visibility1", "ANILLO", valor => valor.Trim().ToUpperInvariant() == "ANILLO"))
                FijarOpcion(bloque, "Visibility1", "ANILLO", valor => valor.ToUpperInvariant().Contains("ANILLO")); // VALVULA: «VALVULA ANILLO»
            FijarAngulo(bloque, AnguloDelBloque(accesorio.Nombre, accesorio.Direccion));

            ListaBloquesTool.AttachMaterialXData(database, transaction, bloque, accesorio.Nombre, diametro, superficie, layout);
            resultado.Accesorios++;
        }
    }

    private static void AgregarAtributos(Transaction transaction, BlockTableRecord definicion, BlockReference bloque)
    {
        if (!definicion.HasAttributeDefinitions) return;
        foreach (ObjectId id in definicion)
        {
            if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition atributo) || atributo.Constant) continue;
            var referencia = new AttributeReference();
            referencia.SetAttributeFromBlock(atributo, bloque.BlockTransform);
            bloque.AttributeCollection.AppendAttribute(referencia);
            transaction.AddNewlyCreatedDBObject(referencia, true);
        }
    }

    // Giro del parámetro de ángulo (Ángulo1 o Angle1) según cómo está hecho cada bloque (medido en los planos reales):
    // TAPON = dirección hacia la tubería + 90°; REDUCCION = hacia el lado de diámetro menor (la punta) + 90°;
    // TEE = dirección del ramal + 180°; UNION = hacia donde crece su cuerpo (en la transición a la red existente, hacia
    // dentro de la tubería nueva).
    private static double AnguloDelBloque(string nombre, double direccion)
    {
        switch (nombre)
        {
            case "TAPON":
            case "REDUCCION": return Normalizar(direccion + Math.PI / 2.0);
            case "TEE": return Normalizar(direccion + Math.PI);
            case "UNION": return Normalizar(direccion); // el cuerpo del bloque crece hacia ese lado: hacia la tubería nueva
            default: return Normalizar(direccion);
        }
    }

    private static double Normalizar(double angulo)
    {
        double vuelta = 2.0 * Math.PI;
        angulo %= vuelta;
        if (angulo < 0.0) angulo += vuelta;
        return angulo > vuelta - 1e-9 ? 0.0 : angulo;
    }

    // Propiedad dinámica de lista (DIAMETRO, UC, visibilidad): se escoge el valor permitido que acepte la condición; si la
    // propiedad no tiene lista se escribe el valor tal cual. false si el bloque no la tiene o ningún valor sirve.
    private static bool FijarOpcion(BlockReference bloque, string propiedad, string valor, Func<string, bool> acepta)
    {
        if (!bloque.IsDynamicBlock) return false;
        foreach (DynamicBlockReferenceProperty dinamica in bloque.DynamicBlockReferencePropertyCollection)
        {
            if (!string.Equals(dinamica.PropertyName, propiedad, StringComparison.OrdinalIgnoreCase) || dinamica.ReadOnly) continue;
            object[] permitidos = dinamica.GetAllowedValues();
            try
            {
                if (permitidos.Length == 0) { dinamica.Value = valor; return true; }
                foreach (object permitido in permitidos)
                {
                    if (permitido == null || !acepta(permitido.ToString())) continue;
                    dinamica.Value = permitido;
                    return true;
                }
            }
            catch (System.Exception)
            {
                return false; // el bloque no acepta ese valor
            }
            return false;
        }
        return false;
    }

    private static void FijarAngulo(BlockReference bloque, double angulo)
    {
        if (!bloque.IsDynamicBlock) { bloque.Rotation = angulo; return; }
        foreach (DynamicBlockReferenceProperty dinamica in bloque.DynamicBlockReferencePropertyCollection)
        {
            string nombre = dinamica.PropertyName ?? string.Empty;
            if (dinamica.ReadOnly || !(nombre.StartsWith("Ángulo", StringComparison.OrdinalIgnoreCase) || nombre.StartsWith("Angle", StringComparison.OrdinalIgnoreCase))) continue;
            dinamica.Value = angulo;
            return;
        }
        bloque.Rotation = angulo;
    }

    // El indicador que trae la plantilla junto al marco: un círculo con un texto de número y otro de diámetro (∅) adentro,
    // y la raya que los separa. Se prefiere el que está fuera del marco del DETALLE (la paleta de la plantilla).
    private static List<Entity> BuscarIndicador(Transaction transaction, BlockTableRecord espacio, out Point3d centro)
    {
        var circulos = new List<Circle>();
        var textos = new List<MText>();
        var rayas = new List<Curve>();
        foreach (ObjectId id in espacio)
        {
            DBObject objeto = transaction.GetObject(id, OpenMode.ForRead);
            if (objeto is Circle circulo) circulos.Add(circulo);
            else if (objeto is MText mtext) textos.Add(mtext);
            else if (objeto is Line || objeto is Polyline) rayas.Add((Curve)objeto);
        }

        List<Entity>? elegido = null;
        centro = Point3d.Origin;
        foreach (Circle circulo in circulos.OrderBy(c => DentroDelMarco(c.Center) ? 1 : 0))
        {
            double r = circulo.Radius;
            List<MText> adentro = textos.Where(t => t.Location.DistanceTo(circulo.Center) < r).ToList();
            MText? numero = adentro.FirstOrDefault(t => Regex.IsMatch(t.Text.Trim(), @"^\d+$"));
            MText? diametro = adentro.FirstOrDefault(t => DiametroIndicador.IsMatch(t.Text));
            if (numero == null || diametro == null) continue;

            elegido = new List<Entity> { circulo, numero, diametro };
            elegido.AddRange(rayas.Where(c => c.StartPoint.DistanceTo(circulo.Center) <= r + 0.01 && c.EndPoint.DistanceTo(circulo.Center) <= r + 0.01));
            centro = circulo.Center;
            break;
        }
        return elegido ?? new List<Entity>();
    }

    // Con una tolerancia mínima: lo que llega justo al borde (un lado abierto de la manzana, la red que sigue) está dentro.
    private static bool DentroDelMarco(Point3d punto) =>
        punto.X >= ClonarUcTool.FrameMinX - 0.01 && punto.X <= ClonarUcTool.FrameMaxX + 0.01 && punto.Y >= ClonarUcTool.DetalleFrameMinY - 0.01 && punto.Y <= ClonarUcTool.DetalleFrameMaxY + 0.01;

    private static void DibujarIndicadores(Database database, Transaction transaction, BlockTableRecord espacio, ObjectId capa, List<Entity> modelo, Point3d centroModelo,
        CalcoPlano plano, TamanoValores tamanos, CalcoResultado resultado)
    {
        if (plano.Indicadores.Count > 0 && modelo.Count == 0)
            resultado.Avisos.Add("el layout no trae un indicador de anillo para copiar: se dibujó uno con las medidas de la plantilla");

        foreach (CalcoIndicador indicador in plano.Indicadores)
        {
            Point3d centro = P(indicador.Centro);
            string diametro = Naming.NormalizeDiameter(indicador.Diametro);
            double radio = RadioIndicador;
            if (modelo.Count > 0)
            {
                Matrix3d mover = Matrix3d.Displacement(centro - centroModelo);
                foreach (Entity parte in modelo)
                {
                    var copia = (Entity)parte.Clone();
                    copia.TransformBy(mover);
                    if (copia is MText texto) CambiarTextoIndicador(texto, indicador.Numero, diametro);
                    if (copia is Circle circulo) radio = circulo.Radius;
                    Agregar(espacio, transaction, copia);
                }
            }
            else
            {
                var circulo = new Circle(centro, Vector3d.ZAxis, radio);
                circulo.SetDatabaseDefaults(database);
                Agregar(espacio, transaction, circulo);
                var raya = new Line(centro - Vector3d.XAxis * radio, centro + Vector3d.XAxis * radio);
                raya.SetDatabaseDefaults(database);
                Agregar(espacio, transaction, raya);
                Agregar(espacio, transaction, CrearMText(database, indicador.Numero, new Point3d(centro.X, centro.Y + radio * 0.5, 0.0), AltoNumeroIndicador));
                Agregar(espacio, transaction, CrearMText(database, "\\U+2205" + diametro + "\"", new Point3d(centro.X, centro.Y - radio * 0.4, 0.0), AltoDiametroIndicador));
            }

            if (!string.IsNullOrWhiteSpace(indicador.Viviendas))
            {
                DBText viviendas = CrearTexto(database, capa, indicador.Viviendas.Trim(), new Point3d(centro.X, centro.Y - radio - SeparacionViviendas, 0.0), 0.0, tamanos.Predial);
                Agregar(espacio, transaction, viviendas);
                viviendas.AdjustAlignment(database);
            }
            resultado.Indicadores++;
        }
    }

    // En la copia cambia el número del anillo o el diámetro sin tocar el formato del texto: se reemplaza la última vez que
    // aparece el valor visible dentro del contenido (los códigos de formato van antes).
    private static void CambiarTextoIndicador(MText texto, string numero, string diametro)
    {
        string visible = texto.Text.Trim();
        string actual, nuevo;
        Match medida = DiametroIndicador.Match(visible);
        if (medida.Success) { actual = medida.Groups["d"].Value; nuevo = diametro; }
        else if (Regex.IsMatch(visible, @"^\d+$")) { actual = visible; nuevo = numero.Trim(); }
        else return;

        string contenido = texto.Contents ?? string.Empty;
        int donde = contenido.LastIndexOf(actual, StringComparison.Ordinal);
        if (donde < 0 || nuevo.Length == 0) return;
        texto.Contents = contenido.Substring(0, donde) + nuevo + contenido.Substring(donde + actual.Length);
    }

    private static MText CrearMText(Database database, string contenido, Point3d punto, double alto)
    {
        var texto = new MText();
        texto.SetDatabaseDefaults(database);
        texto.Contents = contenido;
        texto.TextHeight = alto;
        texto.Attachment = AttachmentPoint.MiddleCenter;
        texto.Location = punto;
        return texto;
    }

    private static void AvisarFueraDelMarco(CalcoPlano plano, CalcoResultado resultado)
    {
        var puntos = new List<CalcoPunto>();
        foreach (CalcoLinea linea in plano.Lineas) puntos.AddRange(linea.Puntos);
        puntos.AddRange(plano.Textos.Select(t => t.Centro));
        puntos.AddRange(plano.Accesorios.Select(a => a.Posicion));
        puntos.AddRange(plano.Indicadores.Select(i => i.Centro));
        int fuera = puntos.Count(p => !DentroDelMarco(P(p)));
        if (fuera > 0) resultado.Avisos.Add($"{fuera} punto(s) quedaron fuera del marco del DETALLE");
    }
}

internal sealed class CalcoResultado
{
    public int Lineas { get; set; }
    public int Textos { get; set; }
    public int Cotas { get; set; }
    public int Accesorios { get; set; }
    public int Indicadores { get; set; }
    public List<string> Avisos { get; } = new List<string>();
    public string? Error { get; set; }
    public bool Ok => Error == null;
}
