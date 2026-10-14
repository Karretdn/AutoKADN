namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Lo que se dibuja del calco de la IA (panel «Importar y calcar»). La geometría (manzana, predios, entorno, tubería e
// indicadores) va siempre.
internal sealed class OpcionesCalco
{
    public bool Bloques { get; set; } = true;          // accesorios
    public bool TextosPredio { get; set; } = true;     // números de predio (los rótulos de las casas van con la geometría)
    public bool CotasVerdes { get; set; }              // las de longitud que leyó el calco (sin otra pasada de la IA)

    public void Filtrar(CalcoPlano plano)
    {
        if (!Bloques) plano.Accesorios.Clear();
        if (!TextosPredio) plano.Textos.RemoveAll(t => t.Tipo == CalcoTipoTexto.Predial && Math.Abs(t.Escala - 1.0) < 1e-3);
        if (!CotasVerdes) plano.Cotas.Clear();
    }
}

// Lo que se pone al detallar con la IA.
internal sealed class OpcionesDetalle
{
    public bool CotasVerdes { get; set; } = true;      // longitud de cada tramo (COTA_<diámetro>)
    public bool CotasMagenta { get; set; } = true;     // ubicación (COTAS MAGENTA), cotas de accesorios y LC / LC0.0

    public void Filtrar(CalcoDetalle detalle)
    {
        if (!CotasVerdes) detalle.Largos.Clear();
        if (!CotasMagenta)
        {
            detalle.Ubicaciones.Clear();
            detalle.Accesorios.Clear();
        }
    }
}
