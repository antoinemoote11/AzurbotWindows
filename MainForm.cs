using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Forms;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AzurBot;

public sealed class MainForm : Form
{
    private readonly Button startButton = new() { Text = "DÉMARRER", Width = 150, Height = 50 };
    private readonly Button stopButton = new() { Text = "ARRÊTER", Width = 150, Height = 50, Enabled = false };
    private readonly Label status = new() { Text = "En attente", AutoSize = true };
    private readonly Label hpLabel = new() { Text = "HP : --", AutoSize = true };
    private readonly Label thresholdLabel = new() { Text = "Retour au village sous : 50 %", AutoSize = true };
    private readonly TrackBar thresholdTrackBar = new() { Minimum = 25, Maximum = 100, Value = 50, TickFrequency = 25, SmallChange = 1, LargeChange = 5, Width = 320, Height = 48 };
    private volatile int returnThreshold = 50;
    private CancellationTokenSource? cts;
    private bool busy;

    public MainForm()
    {
        Text = "Azur Bot";
        Width = 390;
        Height = 220;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var title = new Label
        {
            Text = "AZUR BOT",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            AutoSize = true,
            Left = 25, Top = 18
        };

        startButton.Left = 25; startButton.Top = 65;
        stopButton.Left = 190; stopButton.Top = 65;
        status.Left = 25; status.Top = 120;
        thresholdLabel.Left = 25; thresholdLabel.Top = 150;
        thresholdTrackBar.Left = 25; thresholdTrackBar.Top = 172;
        hpLabel.Left = 25; hpLabel.Top = 220;
        thresholdTrackBar.Scroll += (_, _) =>
        {
            int v = thresholdTrackBar.Value;
            returnThreshold = v;
            thresholdLabel.Text = $"Retour au village sous : {v} %";
        };

        startButton.Click += (_, _) => StartBot();
        stopButton.Click += (_, _) => StopBot();
        Height = 305;
        Controls.AddRange([title, startButton, stopButton, status, thresholdLabel, thresholdTrackBar, hpLabel]);

        FormClosing += (_, _) => StopBot();
    }

    private void StartBot()
    {
        if (busy) return;
        busy = true;
        cts = new CancellationTokenSource();
        startButton.Enabled = false;
        stopButton.Enabled = true;
        SetStatus("Démarré. Place le jeu sur Chrome.");
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    private void StopBot()
    {
        cts?.Cancel();
        busy = false;
        startButton.Enabled = true;
        stopButton.Enabled = false;
        SetStatus("Arrêté");
    }

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        BeginInvoke(() => status.Text = text);
    }

    private void SetHp(double? hp)
    {
        if (IsDisposed) return;
        BeginInvoke(() => hpLabel.Text = hp.HasValue ? $"HP : {hp.Value:0} %" : "HP : --");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        // Avoid reacting to a single OCR misread: require three consecutive low-HP readings.
        int lowHpReadings = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var bmp = CaptureVirtualScreen();
                var hp = await DetectHpPercentAsync(bmp);
                SetHp(hp);

                if (hp.HasValue && hp.Value < returnThreshold)
                    lowHpReadings++;
                else
                    lowHpReadings = 0;

                var text = await ReadFrenchTextAsync(bmp);

                // Hero level-up popup: put exactly one point into Force, then confirm.
                if (Contains(text, "CONFIRMER") && Contains(text, "Force"))
                {
                    SetStatus("Niveau du héros : attente avant + Force");
                    await Task.Delay(2000, token);

                    using var levelScreen = CaptureVirtualScreen();
                    bool forceClicked = await ClickForcePlusAsync(levelScreen, token);
                    if (forceClicked)
                    {
                        await Task.Delay(1200, token);
                        using var bmp2 = CaptureVirtualScreen();
                        var text2 = await ReadFrenchTextAsync(bmp2);
                        if (Contains(text2, "CONFIRMER"))
                        {
                            SetStatus("Force ajoutée : clic sur CONFIRMER");
                            await ClickConfirmAsync(bmp2, token);
                        }
                        else
                        {
                            SetStatus("Point Force cliqué, confirmation non détectée");
                        }
                    }
                    else
                    {
                        SetStatus("Niveau détecté, ligne Force introuvable : je n'ai pas confirmé");
                    }
                }
                else if (Contains(text, "COMPRIS"))
                {
                    SetStatus("Météo : compris !");
                    await ClickTextAsync(bmp, text, "COMPRIS", token);
                }
                else if (Contains(text, "SUPER"))
                {
                    SetStatus("Niveau du compagnon : Super !");
                    await ClickTextAsync(bmp, text, "SUPER", token);
                }
                else if (Contains(text, "RENTRER AU VILLAGE") &&
                         hp.HasValue &&
                         hp.Value < returnThreshold &&
                         lowHpReadings >= 3)
                {
                    SetStatus($"PV joueur confirmés sous {returnThreshold} % : retour au village");
                    await ClickTextAsync(bmp, text, "RENTRER AU VILLAGE", token);
                    lowHpReadings = 0;
                }
                else if (Contains(text, "CONTINUER") && hp.HasValue && hp.Value >= returnThreshold)
                {
                    SetStatus("Victoire : continuer");
                    await ClickTextAsync(bmp, text, "CONTINUER", token);
                }
                else if (Contains(text, "REPARTIR"))
                {
                    SetStatus("Rapport : repartir");
                    await ClickTextAsync(bmp, text, "REPARTIR", token);
                }
                else if (Contains(text, "PARTIR"))
                {
                    SetStatus("Préparation : partir");
                    await ClickTextAsync(bmp, text, "PARTIR", token);
                }
                else
                {
                    SetStatus(hp.HasValue
                        ? $"Analyse... PV joueur : {hp.Value:0}%"
                        : "Analyse... PV joueur non lus");
                }

                await Task.Delay(900, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetStatus("Erreur : " + ex.Message);
                await Task.Delay(1500, token);
            }
        }
    }

    private static bool Contains(string text, string wanted)
        => text.Replace("’", "'").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;

    private static Bitmap CaptureVirtualScreen()
    {
        var bounds = SystemInformation.VirtualScreen;
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        return bmp;
    }

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

        // The player's card is on the left. Ignore PV labels in the right-hand
        // enemy card and in the combat log.
        var pvLine = result.Lines
            .Select(l => new { Line = l, Rect = GetLineRect(l) })
            .Where(x => x.Rect.Width > 0
                     && x.Rect.X < bmp.Width * 0.60
                     && Regex.IsMatch(x.Line.Text, @"\bP\s*V\b", RegexOptions.IgnoreCase))
            .OrderBy(x => x.Rect.X)
            .ThenBy(x => x.Rect.Y)
            .FirstOrDefault();

        if (pvLine is null) return null;

        var pvRect = pvLine.Rect;
        double pvCenterY = pvRect.Y + pvRect.Height / 2;
        double maxDistanceY = Math.Max(24, pvRect.Height * 2.2);
        double maxDistanceX = bmp.Width * 0.28;

        // Prefer a current/max fraction close to the player's PV label,
        // and to its right. This excludes enemy HP fractions on the right.
        var fractions = result.Lines
            .Select(l => new { Rect = GetLineRect(l), Pair = ParseHpPair(l.Text) })
            .Where(x => x.Pair.HasValue && x.Rect.Width > 0)
            .Select(x => new
            {
                x.Rect,
                Pair = x.Pair!.Value,
                DistanceY = Math.Abs((x.Rect.Y + x.Rect.Height / 2) - pvCenterY)
            })
            .Where(x =>
                x.Rect.X >= pvRect.X &&
                x.Rect.X <= pvRect.X + maxDistanceX &&
                x.Rect.X < bmp.Width * 0.60 &&
                x.DistanceY <= maxDistanceY &&
                x.Pair.Maximum > 0 &&
                x.Pair.Current >= 0 &&
                x.Pair.Current <= x.Pair.Maximum)
            .OrderBy(x => x.DistanceY)
            .ThenBy(x => x.Rect.X)
            .ToList();

        if (fractions.Count > 0)
        {
            var pair = fractions[0].Pair;
            return Math.Clamp(100.0 * pair.Current / pair.Maximum, 0, 100);
        }

        // Fallback: estimate the green HP bar directly under the player's PV
        // label. Keep the scan narrow so it cannot reach the enemy card.
        int x0 = Math.Clamp((int)pvRect.X - 2, 0, bmp.Width - 1);
        int x1 = Math.Clamp(x0 + (int)(bmp.Width * 0.25), x0 + 1, bmp.Width);
        int y0 = Math.Clamp((int)(pvRect.Y + pvRect.Height), 0, bmp.Height - 1);
        int y1 = Math.Min(bmp.Height, y0 + 42);

        int bestRun = 0;
        int expectedBarWidthSamples = Math.Max(1, (x1 - x0) / 2);

        for (int y = y0; y < y1; y += 2)
        {
            int run = 0;
            for (int x = x0; x < x1; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                bool green = c.G > 65 && c.G > c.R * 1.25 && c.G > c.B * 1.08;

                if (green)
                {
                    run++;
                    bestRun = Math.Max(bestRun, run);
                }
                else
                {
                    run = 0;
                }
            }
        }

        if (bestRun < 8) return null;

        return Math.Clamp(100.0 * bestRun / expectedBarWidthSamples, 0, 100);
    }

    private static Windows.Foundation.Rect GetLineRect(OcrLine line)
    {
        var words = line.Words;
        if (words.Count == 0)
            return new Windows.Foundation.Rect(0, 0, 0, 0);

        double left = words.Min(w => w.BoundingRect.X);
        double top = words.Min(w => w.BoundingRect.Y);
        double right = words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
        double bottom = words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
        return new Windows.Foundation.Rect(left, top, right - left, bottom - top);
    }

    private static (int Current, int Maximum)? ParseHpPair(string text)
    {
        var match = Regex.Match(text, @"(?<cur>\d[\d\s.,]{0,12})\s*/\s*(?<max>\d[\d\s.,]{0,12})");
        if (!match.Success) return null;

        static bool TryDigits(string value, out int number)
        {
            var digits = Regex.Replace(value, @"[^0-9]", "");
            return int.TryParse(digits, out number);
        }

        if (!TryDigits(match.Groups["cur"].Value, out int current) ||
            !TryDigits(match.Groups["max"].Value, out int maximum) ||
            maximum <= 0 || current > maximum)
            return null;

        return (current, maximum);
    }

    private static async Task<string> ReadFrenchTextAsync(Bitmap bmp)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(ms.ToArray().AsBuffer());
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        var sb = await decoder.GetSoftwareBitmapAsync();
        var engine = OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();

        if (engine is null) return "";
        var result = await engine.RecognizeAsync(sb);
        return result.Text ?? "";
    }

    private static async Task<bool> ClickForcePlusAsync(Bitmap bmp, CancellationToken token)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(ms.ToArray().AsBuffer());
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        var sb = await decoder.GetSoftwareBitmapAsync();
        var engine = OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return false;

        var result = await engine.RecognizeAsync(sb);

        foreach (var line in result.Lines)
        {
            if (line.Text.IndexOf("Force", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var rects = line.Words.Select(w => w.BoundingRect).ToArray();
            if (rects.Length == 0) continue;

            double top = rects.Min(r => r.Y);
            double bottom = rects.Max(r => r.Y + r.Height);
            int y = (int)((top + bottom) / 2);
            int x = (int)(bmp.Width * 0.68);

            NativeClick.Click(x + SystemInformation.VirtualScreen.Left,
                              y + SystemInformation.VirtualScreen.Top);
            await Task.Delay(300, token);
            return true;
        }

        return false;
    }

    private static async Task ClickConfirmAsync(Bitmap bmp, CancellationToken token)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(ms.ToArray().AsBuffer());
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        var sb = await decoder.GetSoftwareBitmapAsync();
        var engine = OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return;

        var result = await engine.RecognizeAsync(sb);

        foreach (var line in result.Lines)
        {
            if (line.Text.IndexOf("CONFIRMER", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var rects = line.Words.Select(w => w.BoundingRect).ToArray();
            if (rects.Length == 0) continue;

            int x = (int)((rects.Min(r => r.X) + rects.Max(r => r.X + r.Width)) / 2);
            int y = (int)((rects.Min(r => r.Y) + rects.Max(r => r.Y + r.Height)) / 2);

            NativeClick.Click(x + SystemInformation.VirtualScreen.Left,
                              y + SystemInformation.VirtualScreen.Top);
            await Task.Delay(1400, token);
            return;
        }

        // Existing fallback for the supplied portrait layout.
        int fallbackX = (int)(bmp.Width * 0.50);
        int fallbackY = (int)(bmp.Height * 0.91);
        NativeClick.Click(fallbackX + SystemInformation.VirtualScreen.Left,
                          fallbackY + SystemInformation.VirtualScreen.Top);
        await Task.Delay(1400, token);
    }

    private static async Task ClickTextAsync(Bitmap bmp, string fullText, string wanted, CancellationToken token)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(ms.ToArray().AsBuffer());
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        var sb = await decoder.GetSoftwareBitmapAsync();
        var engine = OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return;

        var result = await engine.RecognizeAsync(sb);
        var normalizedWanted = wanted.Replace("!", "").Trim();

        foreach (var line in result.Lines)
        {
            var lineText = line.Text.Replace("!", "").Trim();
            if (lineText.IndexOf(normalizedWanted, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var rect = line.Words.Select(w => w.BoundingRect).ToArray();
            if (rect.Length == 0) continue;

            double left = rect.Min(r => r.X);
            double top = rect.Min(r => r.Y);
            double right = rect.Max(r => r.X + r.Width);
            double bottom = rect.Max(r => r.Y + r.Height);

            int x = (int)((left + right) / 2);
            int y = (int)((top + bottom) / 2);

            if (x < 5 || y < 5 || x >= bmp.Width - 5 || y >= bmp.Height - 5) return;

            NativeClick.Click(x + SystemInformation.VirtualScreen.Left,
                              y + SystemInformation.VirtualScreen.Top);
            await Task.Delay(1400, token);
            return;
        }
    }
}
