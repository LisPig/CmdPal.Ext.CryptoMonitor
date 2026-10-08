// Draws the price chart and encodes it as a PNG, entirely in managed code.
//
// Why hand-rolled: the CmdPal extension API has no chart control (only
// ImageContent/MarkdownContent/PlainTextContent), so an extension has to bring
// its own rasteriser. Doing it here keeps the extension dependency-free (no
// System.Drawing, no WPF, no external chart service) and trim/AOT safe.
//
// Pipeline: draw at 3x into an RGBA canvas, box-downsample for anti-aliasing,
// stamp a 5x7 bitmap font for the labels, then emit a PNG (one IHDR/IDAT/IEND,
// zlib via ZLibStream).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));
}

internal sealed record ChartStyle(string Currency, double Rate, ChartTheme Theme, string RangeTag);

internal enum ChartTheme
{
    /// Transparent background with mid-grey grid/labels.
    ///
    /// This is the variant the extension actually uses: a markdown inline image
    /// cannot know the host theme, and a mid-grey on transparent reads fine on
    /// both light and dark.
    Transparent,

    Light,

    Dark,
}

internal static class ChartRenderer
{
    public const int Width = 660;
    public const int Height = 260;

    private const int Super = 3;

    private static readonly Rgb Up = Rgb.FromHex("#16A34A");
    private static readonly Rgb Down = Rgb.FromHex("#DC2626");
    private static readonly Rgb LightBg = Rgb.FromHex("#FFFFFF");
    private static readonly Rgb LightGrid = Rgb.FromHex("#E6E8EB");
    private static readonly Rgb LightLabel = Rgb.FromHex("#6B7280");
    private static readonly Rgb DarkBg = Rgb.FromHex("#1C1C1C");
    private static readonly Rgb DarkGrid = Rgb.FromHex("#34373B");
    private static readonly Rgb DarkLabel = Rgb.FromHex("#9BA1A9");
    private static readonly Rgb NeutralGrid = Rgb.FromHex("#8A8A8A");
    private static readonly Rgb NeutralLabel = Rgb.FromHex("#909090");

    // Rendering is buffer-heavy: the supersampled canvas alone is
    // 1980x780x4 = 6.2 MB, and the LOH never handed those bytes back once they
    // had been collected (a 20 MB live heap sat on ~48 MB of dead LOH space).
    // The big buffers are therefore reused instead of reallocated, and the
    // PNG is written straight to the caller's stream. The lock makes the reuse
    // safe for any caller; it also keeps two renders from holding two 6 MB
    // canvases at the same time.
    private static readonly object RenderGate = new();
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly MemoryStream DeflateScratch = new(128 * 1024);

    private static byte[]? _superCanvas; // (Width*Super) x (Height*Super) RGBA
    private static byte[]? _smallCanvas; // Width x Height RGBA: downsample target and label surface
    private static byte[]? _scanlines;   // PNG raw scanlines (one filter byte + row)

    /// Writes the chart as a PNG into <paramref name="output"/>.
    public static void RenderPngTo(Stream output, IReadOnlyList<Candle> candles, ChartStyle style)
    {
        lock (RenderGate)
        {
            RenderCore(output, candles, style);
        }
    }

    private static void RenderCore(Stream output, IReadOnlyList<Candle> candles, ChartStyle style)
    {
        var points = candles.Where(c => c.Close > 0).ToList();
        if (points.Count < 2)
        {
            Placeholder(output, style);
            return;
        }

        var transparent = style.Theme == ChartTheme.Transparent;
        var bg = style.Theme == ChartTheme.Dark ? DarkBg : LightBg;
        var grid = style.Theme switch
        {
            ChartTheme.Dark => DarkGrid,
            ChartTheme.Light => LightGrid,
            _ => NeutralGrid,
        };
        var label = style.Theme switch
        {
            ChartTheme.Dark => DarkLabel,
            ChartTheme.Light => LightLabel,
            _ => NeutralLabel,
        };
        var gridAlpha = transparent ? 0.32f : 0.55f;

        var canvas = new Canvas(Width * Super, Height * Super, _superCanvas ??= new byte[Width * Super * Height * Super * 4]);
        if (transparent)
        {
            canvas.Clear(); // reused buffer: transparent means "all zero"
        }
        else
        {
            canvas.Fill(bg);
        }

        // ---- price domain (padded so the line never touches the frame) ----
        double low = points.Min(p => Math.Min(p.Close, p.Low > 0 ? p.Low : p.Close));
        double high = points.Max(p => Math.Max(p.Close, p.High > 0 ? p.High : p.Close));
        if (high - low < double.Epsilon)
        {
            high = low * 1.01 + 0.01;
            low *= 0.99;
        }
        var pad = (high - low) * 0.06;
        low -= pad;
        high += pad;
        var span = high - low;

        var plotLeft = 12 * Super;
        var plotRight = (Width - 12) * Super;
        var plotTop = 28 * Super;
        var plotBottom = (Height - 28) * Super;

        double X(int i) => plotLeft + ((plotRight - plotLeft) * ((double)i / (points.Count - 1)));
        double Y(double price) => plotBottom - (((price - low) / span) * (plotBottom - plotTop));

        // ---- grid ----
        for (var i = 1; i <= 3; i++)
        {
            var y = plotTop + ((plotBottom - plotTop) * i / 4.0);
            canvas.FillRect(plotLeft, y, plotRight - plotLeft, Super, grid, gridAlpha);
        }

        var rising = points[^1].Close >= points[0].Close;
        var line = rising ? Up : Down;

        // ---- area under the line, fading downwards ----
        for (var x = plotLeft; x < plotRight; x++)
        {
            var t = (x - plotLeft) / (double)(plotRight - plotLeft) * (points.Count - 1);
            var i0 = (int)Math.Floor(t);
            var i1 = Math.Min(i0 + 1, points.Count - 1);
            var frac = t - i0;
            var y = Y(points[i0].Close) + ((Y(points[i1].Close) - Y(points[i0].Close)) * frac);
            canvas.VerticalFade(x, y, plotBottom, line, 0.30f, 0.01f);
        }

        // ---- the line itself ----
        for (var i = 1; i < points.Count; i++)
        {
            canvas.Line(X(i - 1), Y(points[i - 1].Close), X(i), Y(points[i].Close), 2.4 * Super, line, 1f);
        }
        canvas.Disc(X(points.Count - 1), Y(points[^1].Close), 3.2 * Super, line, 1f);

        // ---- labels (drawn at final resolution so the font stays crisp) ----
        var small = _smallCanvas ??= new byte[Width * Height * 4];
        canvas.Downsample(Super, Width, Height, small);
        var pixels = new Canvas(Width, Height, small);

        var highText = $"H {Money(style, high - pad)}";
        var lowText = $"L {Money(style, low + pad)}";
        var lastText = Money(style, points[^1].Close);

        pixels.Text(12, 6, highText, label, 2);
        pixels.TextRight(Width - 12, 6, style.RangeTag, label, 2);
        pixels.Text(12, Height - 20, lowText, label, 2);

        // current price, right-aligned on its own plate so the line can't hide it
        var priceWidth = pixels.TextWidth(lastText, 2);
        if (transparent)
        {
            // No opaque background to punch out: use a soft translucent chip.
            pixels.PlateAt(Width - 12 - priceWidth - 6, Height - 22, priceWidth + 8, 18, NeutralGrid, 0.38f);
        }
        else
        {
            pixels.FillRectEx(Width - 12 - priceWidth - 6, Height - 22, priceWidth + 8, 18, bg);
        }
        pixels.TextRight(Width - 12, Height - 20, lastText, line, 2);

        EncodePng(output, Width, Height, small);
    }

    private static string Money(ChartStyle style, double value) => Format.Money(style.Currency, value * style.Rate);

    private static void Placeholder(Stream output, ChartStyle style)
    {
        var transparent = style.Theme == ChartTheme.Transparent;
        var bg = style.Theme == ChartTheme.Dark ? DarkBg : LightBg;
        var label = style.Theme switch
        {
            ChartTheme.Dark => DarkLabel,
            ChartTheme.Light => LightLabel,
            _ => NeutralLabel,
        };
        var pixels = _smallCanvas ??= new byte[Width * Height * 4];
        var canvas = new Canvas(Width, Height, pixels);
        canvas.Clear();
        if (!transparent)
        {
            canvas.Fill(bg);
        }
        var text = "NO DATA";
        var x = (Width - canvas.TextWidth(text, 3)) / 2;
        canvas.Text(x, (Height - 21) / 2, text, label, 3);
        EncodePng(output, Width, Height, pixels);
    }

    // ---- PNG container ----

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static void EncodePng(Stream output, int width, int height, byte[] rgba)
    {
        var stride = width * 4;
        var rawLength = height * (stride + 1);
        var raw = _scanlines is { } cached && cached.Length >= rawLength
            ? cached
            : _scanlines = new byte[rawLength];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0; // filter: none
            Buffer.BlockCopy(rgba, y * stride, raw, (y * (stride + 1)) + 1, stride);
        }

        // IDAT needs its length up front, so the deflate output is buffered (in a
        // reused stream) rather than streamed into the file.
        DeflateScratch.SetLength(0);
        using (var zlib = new ZLibStream(DeflateScratch, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, rawLength);
        }
        var compressed = DeflateScratch.GetBuffer().AsSpan(0, (int)DeflateScratch.Length);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBe(ihdr, 0, width);
        WriteBe(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: RGBA
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace

        output.Write(PngSignature);
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        WriteBe(header, 0, data.Length);
        header[4] = (byte)type[0];
        header[5] = (byte)type[1];
        header[6] = (byte)type[2];
        header[7] = (byte)type[3];
        stream.Write(header);

        // CRC over type + data, fed incrementally so no copy is needed.
        var crc = Crc32Update(0xFFFFFFFFu, header[4..8]);
        stream.Write(data);
        crc = Crc32Update(crc, data) ^ 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        WriteBe(crcBytes, 0, unchecked((int)crc));
        stream.Write(crcBytes);
    }

    private static void WriteBe(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)((value >> 24) & 0xFF);
        target[offset + 1] = (byte)((value >> 16) & 0xFF);
        target[offset + 2] = (byte)((value >> 8) & 0xFF);
        target[offset + 3] = (byte)(value & 0xFF);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32Update(uint c, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c;
    }

    // ---- drawing surface ----

    private sealed class Canvas
    {
        private readonly byte[] _pixels;
        private readonly int _w;
        private readonly int _h;

        /// <param name="buffer">Reused pixel buffer (caller owns it); a fresh
        /// zeroed one is allocated when null.</param>
        public Canvas(int width, int height, byte[]? buffer = null)
        {
            _w = width;
            _h = height;
            _pixels = buffer is { Length: > 0 } ? buffer : new byte[width * height * 4];
        }

        public void Clear() => Array.Clear(_pixels, 0, _w * _h * 4);

        public void Fill(Rgb colour)
        {
            for (var i = 0; i < _pixels.Length; i += 4)
            {
                _pixels[i] = colour.R;
                _pixels[i + 1] = colour.G;
                _pixels[i + 2] = colour.B;
                _pixels[i + 3] = 255;
            }
        }

        public void Blend(int x, int y, Rgb colour, float alpha)
        {
            if (alpha <= 0f || x < 0 || y < 0 || x >= _w || y >= _h)
            {
                return;
            }
            if (alpha > 1f)
            {
                alpha = 1f;
            }

            var i = ((y * _w) + x) * 4;
            var dstA = _pixels[i + 3] / 255f;
            var outA = alpha + (dstA * (1f - alpha));
            if (outA <= 0f)
            {
                return;
            }

            _pixels[i] = Mix(colour.R, _pixels[i], alpha, dstA, outA);
            _pixels[i + 1] = Mix(colour.G, _pixels[i + 1], alpha, dstA, outA);
            _pixels[i + 2] = Mix(colour.B, _pixels[i + 2], alpha, dstA, outA);
            _pixels[i + 3] = (byte)Math.Clamp(outA * 255f, 0f, 255f);
        }

        private static byte Mix(byte src, byte dst, float a, float dstA, float outA) =>
            (byte)Math.Clamp((((src * a) + (dst * dstA * (1f - a))) / outA), 0f, 255f);

        public void FillRect(double x, double y, double w, double h, Rgb colour, float alpha = 1f)
        {
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var x1 = (int)Math.Ceiling(x + w);
            var y1 = (int)Math.Ceiling(y + h);
            for (var py = y0; py < y1; py++)
            {
                for (var px = x0; px < x1; px++)
                {
                    Blend(px, py, colour, alpha);
                }
            }
        }

        /// Semi-transparent rectangle, for the label chip on a transparent chart.
        public void PlateAt(double x, double y, double w, double h, Rgb colour, float alpha) =>
            FillRect(x, y, w, h, colour, alpha);

        /// Opaque rectangle (used for the label plate, where the background is known).
        public void FillRectEx(double x, double y, double w, double h, Rgb colour)
        {
            var x0 = Math.Max(0, (int)Math.Floor(x));
            var y0 = Math.Max(0, (int)Math.Floor(y));
            var x1 = Math.Min(_w, (int)Math.Ceiling(x + w));
            var y1 = Math.Min(_h, (int)Math.Ceiling(y + h));
            for (var py = y0; py < y1; py++)
            {
                for (var px = x0; px < x1; px++)
                {
                    var i = ((py * _w) + px) * 4;
                    _pixels[i] = colour.R;
                    _pixels[i + 1] = colour.G;
                    _pixels[i + 2] = colour.B;
                    _pixels[i + 3] = 255;
                }
            }
        }

        public void VerticalFade(int x, double fromY, double toY, Rgb colour, float topAlpha, float bottomAlpha)
        {
            var y0 = Math.Max(0, (int)Math.Floor(fromY));
            var y1 = Math.Min(_h, (int)Math.Ceiling(toY));
            var total = Math.Max(1.0, toY - fromY);
            for (var py = y0; py < y1; py++)
            {
                var t = Math.Clamp((float)((py - fromY) / total), 0f, 1f);
                Blend(x, py, colour, topAlpha + ((bottomAlpha - topAlpha) * t));
            }
        }

        public void Disc(double cx, double cy, double radius, Rgb colour, float alpha)
        {
            var x0 = (int)Math.Floor(cx - radius - 1);
            var y0 = (int)Math.Floor(cy - radius - 1);
            var x1 = (int)Math.Ceiling(cx + radius + 1);
            var y1 = (int)Math.Ceiling(cy + radius + 1);
            for (var py = y0; py <= y1; py++)
            {
                for (var px = x0; px <= x1; px++)
                {
                    var dx = px + 0.5 - cx;
                    var dy = py + 0.5 - cy;
                    var d = Math.Sqrt((dx * dx) + (dy * dy));
                    var coverage = radius + 0.5 - d;
                    if (coverage > 0)
                    {
                        Blend(px, py, colour, Math.Min(1f, (float)coverage) * alpha);
                    }
                }
            }
        }

        public void Line(double x0, double y0, double x1, double y1, double thickness, Rgb colour, float alpha)
        {
            var radius = thickness / 2.0;
            var dx = x1 - x0;
            var dy = y1 - y0;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            var steps = Math.Max(1, (int)Math.Ceiling(length));
            for (var s = 0; s <= steps; s++)
            {
                var t = (double)s / steps;
                Disc(x0 + (dx * t), y0 + (dy * t), radius, colour, alpha);
            }
        }

        public void Downsample(int factor, int outWidth, int outHeight, byte[] result)
        {
            var samples = factor * factor;
            for (var y = 0; y < outHeight; y++)
            {
                for (var x = 0; x < outWidth; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (var sy = 0; sy < factor; sy++)
                    {
                        var row = ((y * factor) + sy) * _w;
                        for (var sx = 0; sx < factor; sx++)
                        {
                            var i = (row + (x * factor) + sx) * 4;
                            r += _pixels[i];
                            g += _pixels[i + 1];
                            b += _pixels[i + 2];
                            a += _pixels[i + 3];
                        }
                    }
                    var o = ((y * outWidth) + x) * 4;
                    result[o] = (byte)(r / samples);
                    result[o + 1] = (byte)(g / samples);
                    result[o + 2] = (byte)(b / samples);
                    result[o + 3] = (byte)(a / samples);
                }
            }
        }

        // ---- 5x7 bitmap font ----

        public int TextWidth(string text, int scale) => (text.Length * 6 * scale) - scale;

        public void Text(int x, int y, string text, Rgb colour, int scale) => Stamp(x, y, text, colour, scale);

        public void TextRight(int right, int y, string text, Rgb colour, int scale) =>
            Stamp(right - TextWidth(text, scale), y, text, colour, scale);

        private void Stamp(int x, int y, string text, Rgb colour, int scale)
        {
            var cursor = x;
            foreach (var ch in text)
            {
                if (Font.TryGetValue(ch, out var glyph))
                {
                    for (var row = 0; row < 7; row++)
                    {
                        for (var col = 0; col < 5; col++)
                        {
                            if (glyph[row][col] == '#')
                            {
                                FillRectEx(cursor + (col * scale), y + (row * scale), scale, scale, colour);
                            }
                        }
                    }
                }
                cursor += 6 * scale;
            }
        }
    }

    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['0'] = [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
        ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = [".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ['5'] = ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
        ['6'] = ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."],
        ['7'] = ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
        ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."],
        [','] = [".....", ".....", ".....", ".....", "..##.", "..#..", ".#..."],
        ['.'] = [".....", ".....", ".....", ".....", ".....", ".##..", ".##.."],
        ['$'] = ["..#..", ".####", "#.#..", ".###.", "..#.#", "####.", "..#.."],
        ['¥'] = ["#...#", ".#.#.", "..#..", "#####", "..#..", "#####", "..#.."],
        ['-'] = [".....", ".....", ".....", "#####", ".....", ".....", "....."],
        ['+'] = [".....", "..#..", "..#..", "#####", "..#..", "..#..", "....."],
        ['%'] = ["##..#", "##.#.", "...#.", "..#..", ".#...", ".#.##", "#..##"],
        ['H'] = ["#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['L'] = ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
        ['K'] = ["#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"],
        ['M'] = ["#...#", "##.##", "#.#.#", "#...#", "#...#", "#...#", "#...#"],
        ['B'] = ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
        ['N'] = ["#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#"],
        ['O'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['D'] = ["####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."],
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        ['S'] = [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
        [' '] = [".....", ".....", ".....", ".....", ".....", ".....", "....."],
    };
}
