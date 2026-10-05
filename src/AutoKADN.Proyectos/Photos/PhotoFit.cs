namespace AutoKADN.Proyectos.Photos;

/// <summary>
/// Dónde queda una foto y qué parte se muestra. Los recortes son fracciones (0 a 1) del ancho o del alto de
/// la foto que se dejan fuera por cada lado; se guardan en el Excel como recorte de imagen, así la foto original
/// se conserva y se puede reencuadrar desde Excel.
/// </summary>
public readonly record struct Placement(double X, double Y, double W, double H,
    double CropLeft, double CropTop, double CropRight, double CropBottom, bool Letterboxed)
{
    public bool IsCropped => CropLeft > 0 || CropTop > 0 || CropRight > 0 || CropBottom > 0;
}

/// <summary>
/// Reglas para que todas las fotos llenen su espacio aunque no tengan la proporción de la caja (panorámicas,
/// fotos muy altas...): primero se recorta un poco, desde el centro, y solo lo que falte se estira.
/// Si una foto es tan extrema que exigiría deformarla demasiado, se muestra completa dentro de su caja.
/// </summary>
public static class PhotoFit
{
    /// <summary>Recorte preferido: hasta esta fracción del ancho o alto de la foto.</summary>
    public const double MaxCrop = 0.25;

    /// <summary>Recorte máximo cuando recortar lo preferido aún dejaría la foto muy estirada.</summary>
    public const double HardCrop = 0.50;

    /// <summary>Deformación máxima aceptada (1.35 = 35 % más ancha o más alta que la original).</summary>
    public const double MaxStretch = 1.35;

    public static Placement Place(Box box, int photoWidth, int photoHeight)
    {
        double photoAspect = (double)photoWidth / photoHeight;
        double boxAspect = box.W / box.H;
        bool photoWider = photoAspect > boxAspect;
        double mismatch = photoWider ? photoAspect / boxAspect : boxAspect / photoAspect; // >= 1

        if (mismatch < 1.005) return new Placement(box.X, box.Y, box.W, box.H, 0, 0, 0, 0, false);

        // Recorte preferido (sin pasar de lo que corregiría todo el desajuste) y lo que quedaría por estirar.
        double crop = Math.Min(MaxCrop, 1 - 1 / mismatch);
        double stretch = mismatch * (1 - crop);
        if (stretch > MaxStretch)
        {
            crop = 1 - MaxStretch / mismatch;
            stretch = MaxStretch;
        }

        if (crop > HardCrop) return Letterbox(box, photoWidth, photoHeight);

        double side = crop / 2;
        return photoWider
            ? new Placement(box.X, box.Y, box.W, box.H, side, 0, side, 0, false)   // más ancha que la caja: se recortan los lados
            : new Placement(box.X, box.Y, box.W, box.H, 0, side, 0, side, false);  // más alta que la caja: se recortan arriba y abajo
    }

    // Foto extrema: entera, sin deformar y centrada dentro de su caja.
    private static Placement Letterbox(Box box, int photoWidth, int photoHeight)
    {
        double scale = Math.Min(box.W / photoWidth, box.H / photoHeight);
        double w = photoWidth * scale, h = photoHeight * scale;
        return new Placement(box.X + (box.W - w) / 2, box.Y + (box.H - h) / 2, w, h, 0, 0, 0, 0, true);
    }
}
