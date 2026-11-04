private static async Task<double?> DetectHpPercentAsync(Bitmap bmp)
{
    using var ms = new System.IO.MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    ms.Position = 0;

    using var ras = new InMemoryRandomAccessStream();
    await ras.WriteAsync(ms.ToArray().AsBuffer());
    ras.Seek(0);

    var decoder = await BitmapDecoder.CreateAsync(ras);
    var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

    var engine = OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                 ?? OcrEngine.TryCreateFromUserProfileLanguages();

    if (engine is null) return null;

    var result = await engine.RecognizeAsync(softwareBitmap);

    // Recherche PV uniquement dans la partie haute de l'écran.
    // Le journal de combat, situé plus bas, est ainsi ignoré.
    var pvLabels = result.Lines
        .Select(l => new { Line = l, Rect = GetLineRect(l) })
        .Where(x => x.Rect.Width > 0
                 && x.Rect.X < bmp.Width * 0.80
                 && x.Rect.Y < bmp.Height * 0.68
                 && Regex.IsMatch(
                     x.Line.Text,
                     @"\bP\s*V\b",
                     RegexOptions.IgnoreCase))
        .OrderBy(x => x.Rect.Y)
        .ThenBy(x => x.Rect.X)
        .ToList();

    if (pvLabels.Count == 0)
        return null;

    // On prend le libellé PV de la fiche du joueur.
    var pvRect = pvLabels[0].Rect;
    double pvCenterY = pvRect.Y + pvRect.Height / 2;

    // Recherche les nombres actuels / maximum à droite du libellé PV.
    var fractions = result.Lines
        .Select(l => new
        {
            Rect = GetLineRect(l),
            Pair = ParseHpPair(l.Text)
        })
        .Where(x => x.Pair.HasValue && x.Rect.Width > 0)
        .Select(x => new
        {
            x.Rect,
            Pair = x.Pair!.Value,
            DistanceY = Math.Abs(
                (x.Rect.Y + x.Rect.Height / 2) - pvCenterY)
        })
        .Where(x =>
            x.Rect.X >= pvRect.X - 20 &&
            x.Rect.X <= pvRect.X + bmp.Width * 0.48 &&
            x.Rect.X < bmp.Width * 0.88 &&
            x.Rect.Y < bmp.Height * 0.68 &&
            x.DistanceY <= Math.Max(42, pvRect.Height * 2.8) &&
            x.Pair.Maximum > 0 &&
            x.Pair.Current >= 0 &&
            x.Pair.Current <= x.Pair.Maximum)
        .OrderBy(x => x.DistanceY)
        .ThenBy(x => x.Rect.X)
        .ToList();

    if (fractions.Count == 0)
        return null;

    var pair = fractions[0].Pair;

    return Math.Clamp(
        100.0 * pair.Current / pair.Maximum,
        0,
        100);
}
