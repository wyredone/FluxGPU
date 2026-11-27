// Generates the FluxGPU logo set (SVG, PNG, ICO) from one set of vector shapes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

class Shape
{
    public string D, Fill, Fill2, Stroke;
    public double Width, Opacity = 1;
}

static class BrandGen
{
    const string Cyan = "#12E9FF";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Coordinates below follow the 1024px reference art; the icon canvas is 512 x 512.
    static string P(double x, double y)
    {
        const double s = 512.0 / 600.0;
        return string.Format(Inv, "{0:0.##},{1:0.##}", (x - 212) * s, (y - 212) * s);
    }

    static string Path(string commands, params double[] xy)
    {
        string[] c = commands.Split(' ');
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < c.Length; i++) b.Append(c[i]).Append(P(xy[2 * i], xy[2 * i + 1])).Append(' ');
        return b.ToString().Trim();
    }

    static string Round(double x, double y, double w, double h, double r)
    {
        return string.Format(Inv,
            "M{0},{1} L{2},{1} Q{3},{1} {3},{4} L{3},{5} Q{3},{6} {2},{6} L{0},{6} Q{7},{6} {7},{5} L{7},{4} Q{7},{1} {0},{1} Z",
            x + r, y, x + w - r, x + w, y + r, y + h - r, y + h, x);
    }

    static string Circle(double cx, double cy, double r)
    {
        return string.Format(Inv, "M{0},{1} A{2},{2} 0 1 1 {3},{1} A{2},{2} 0 1 1 {0},{1} Z", cx - r, cy, r, cx + r);
    }

    static List<Shape> Icon(int level)   // 0: tiny sizes, 1: small, 2: full detail
    {
        List<Shape> s = new List<Shape>();
        double inset = level < 2 ? 12 : 42, size = 512 - 2 * inset, radius = level < 2 ? 110 : 80;
        if (level == 2) s.Add(new Shape { D = Round(inset, inset + 8, size, size, radius), Fill = "#000000", Opacity = 0.16 });
        s.Add(new Shape { D = Round(inset, inset, size, size, radius), Fill = level < 2 ? "#97A2B6" : "#6B7484", Fill2 = level < 2 ? "#4A5264" : "#262B35" });
        double rim = level < 2 ? 14 : 6;
        s.Add(new Shape { D = Round(inset + rim, inset + rim, size - 2 * rim, size - 2 * rim, radius - rim), Fill = "#343A46", Fill2 = "#181C24" });
        if (level == 2)
        {
            string[] traces = {
                Path("M L L", 356, 300, 428, 300, 446, 322),
                Path("M L L", 468, 304, 490, 282, 566, 282),
                Path("M L L L", 590, 324, 638, 324, 660, 302, 680, 302),
                Path("M L L L", 292, 352, 292, 432, 314, 456, 314, 486),
                Path("M L L L", 292, 546, 292, 610, 314, 634, 314, 668),
                Path("M L L L", 732, 352, 732, 424, 710, 448, 710, 482),
                Path("M L L L", 732, 546, 732, 604, 710, 628, 710, 662),
                Path("M L L L", 346, 724, 434, 724, 456, 702, 522, 702),
                Path("M L L", 540, 722, 556, 722, 576, 746),
                Path("M L L L", 590, 702, 640, 702, 662, 724, 678, 724) };
            foreach (string t in traces) s.Add(new Shape { D = t, Stroke = "#5A6373", Width = 3, Opacity = 0.75 });
            double[] dots = { 446, 322, 566, 282, 590, 324, 314, 486, 314, 668, 710, 482, 522, 702, 576, 746 };
            for (int i = 0; i < dots.Length; i += 2)
                s.Add(new Shape { D = Circle((dots[i] - 212) * 512 / 600.0, (dots[i + 1] - 212) * 512 / 600.0, 4.5), Fill = "#5A6373" });
        }
        if (level == 2)
        {
            double x = (344 - 212) * 512 / 600.0, w = 336 * 512 / 600.0;
            if (level == 2) s.Add(new Shape { D = Round(x, x + 9, w, w, 30), Fill = "#000000", Opacity = 0.3 });
            s.Add(new Shape { D = Round(x, x, w, w, 30), Fill = "#5A6271", Fill2 = "#252A33" });
            s.Add(new Shape { D = Round(x + 3.5, x + 3.5, w - 7, w - 7, 27), Fill = "#303641", Fill2 = "#1B1F27" });
        }
        if (level == 2)
        {
            double[] screws = { 318, 319, 707, 319, 318, 705, 707, 705 };
            string[] ring = { "#C9E89A", Cyan, "#7BE0CF", "#E6D58A" };
            for (int i = 0; i < 4; i++)
            {
                double cx = (screws[2 * i] - 212) * 512 / 600.0, cy = (screws[2 * i + 1] - 212) * 512 / 600.0;
                s.Add(new Shape { D = Circle(cx, cy, 14), Fill = "#0E1117" });
                s.Add(new Shape { D = Circle(cx, cy, 8), Stroke = ring[i], Width = 3.5 });
            }
        }
        string pulse = level < 2
            ? "M50,256 L146,256 L214,106 L304,406 L366,256 L462,256"
            : Path("M L L L L L L L L", 245, 512, 388, 512, 410, 471, 441, 564, 489, 397, 533, 626, 578, 465, 600, 512, 779, 512);
        double line = level == 0 ? 54 : level == 1 ? 40 : 14;
        if (level == 2)   // soft glow built from many faint strokes
            for (double k = 4.0; k > 1.2; k -= 0.28)
                s.Add(new Shape { D = pulse, Stroke = Cyan, Width = line * k, Opacity = 0.028 });
        s.Add(new Shape { D = pulse, Stroke = Cyan, Width = line });
        return s;
    }

    static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

    static void Draw(DrawingContext dc, IEnumerable<Shape> shapes)
    {
        foreach (Shape s in shapes)
        {
            Brush fill = null;
            if (s.Fill != null) fill = s.Fill2 == null ? (Brush)new SolidColorBrush(C(s.Fill)) : new LinearGradientBrush(C(s.Fill), C(s.Fill2), 90);
            Pen pen = null;
            if (s.Stroke != null)
                pen = new Pen(new SolidColorBrush(C(s.Stroke)), s.Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.PushOpacity(s.Opacity);
            dc.DrawGeometry(fill, pen, Geometry.Parse("F1 " + s.D));
            dc.Pop();
        }
    }

    static string Svg(IEnumerable<Shape> shapes, double w, double h)
    {
        StringBuilder defs = new StringBuilder(), body = new StringBuilder();
        int g = 0;
        foreach (Shape s in shapes)
        {
            string fill = s.Fill ?? "none";
            if (s.Fill2 != null)
            {
                string id = "g" + (g++);
                defs.AppendFormat("<linearGradient id=\"{0}\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"{1}\"/><stop offset=\"1\" stop-color=\"{2}\"/></linearGradient>", id, s.Fill, s.Fill2);
                fill = "url(#" + id + ")";
            }
            body.AppendFormat(Inv, "<path d=\"{0}\" fill=\"{1}\"", s.D, fill);
            if (s.Stroke != null) body.AppendFormat(Inv, " stroke=\"{0}\" stroke-width=\"{1:0.##}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"", s.Stroke, s.Width);
            if (s.Opacity < 1) body.AppendFormat(Inv, " opacity=\"{0}\"", s.Opacity);
            body.Append("/>\n");
        }
        return string.Format(Inv, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {0:0} {1:0}\" width=\"{0:0}\" height=\"{1:0}\">\n<title>FluxGPU</title>\n<defs>{2}</defs>\n{3}</svg>\n", w, h, defs, body);
    }

    static RenderTargetBitmap Render(int w, int h, Action<DrawingContext> paint)
    {
        DrawingVisual v = new DrawingVisual();
        using (DrawingContext dc = v.RenderOpen()) paint(dc);
        RenderTargetBitmap b = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        b.Render(v);
        return b;
    }

    static RenderTargetBitmap IconBitmap(int size)
    {
        int level = size <= 24 ? 0 : size <= 48 ? 1 : 2;
        return Render(size, size, delegate(DrawingContext dc)
        {
            dc.PushTransform(new ScaleTransform(size / 512.0, size / 512.0));
            Draw(dc, Icon(level));
            dc.Pop();
        });
    }

    static byte[] Png(BitmapSource b)
    {
        PngBitmapEncoder e = new PngBitmapEncoder();
        e.Frames.Add(BitmapFrame.Create(b));
        using (MemoryStream m = new MemoryStream()) { e.Save(m); return m.ToArray(); }
    }

    static byte[] Dib(BitmapSource source)
    {
        FormatConvertedBitmap b = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int n = b.PixelWidth, stride = n * 4, mask = ((n + 31) / 32) * 4;
        byte[] px = new byte[stride * n];
        b.CopyPixels(px, stride, 0);
        using (MemoryStream m = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(m))
        {
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(stride * n + mask * n); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--) w.Write(px, y * stride, stride);
            w.Write(new byte[mask * n]);
            w.Flush();
            return m.ToArray();
        }
    }

    static void Ico(string file, int[] sizes)
    {
        List<byte[]> images = new List<byte[]>();
        foreach (int s in sizes) images.Add(s >= 256 ? Png(IconBitmap(s)) : Dib(IconBitmap(s)));
        using (BinaryWriter w = new BinaryWriter(File.Create(file)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }
    }

    // Wordmark as outlines, so the logo does not depend on installed fonts.
    static List<Shape> Lockup(string flux, string gpu, out double width)
    {
        Typeface face = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        FormattedText a = new FormattedText("Flux", Inv, FlowDirection.LeftToRight, face, 290, Brushes.Black);
        FormattedText b = new FormattedText("GPU", Inv, FlowDirection.LeftToRight, face, 290, Brushes.Black);
        Rect cap = b.BuildGeometry(new Point(0, 0)).Bounds;
        double y = 256 - cap.Height / 2 - cap.Top, x = 560;
        Geometry ga = a.BuildGeometry(new Point(x, y));
        double x2 = x + a.WidthIncludingTrailingWhitespace + 4;
        Geometry gb = b.BuildGeometry(new Point(x2, y));
        width = Math.Ceiling(gb.Bounds.Right + 48);
        List<Shape> s = Icon(2);
        s.Add(new Shape { D = Outline(ga), Fill = flux });
        s.Add(new Shape { D = Outline(gb), Fill = gpu });
        return s;
    }

    static string Outline(Geometry g)
    {
        string d = g.GetOutlinedPathGeometry().ToString(Inv);
        return d.StartsWith("F") ? d.Substring(2).Trim() : d;
    }

    static void Main(string[] args)
    {
        if (args.Length < 1 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: BrandGen.exe <existing output folder>   (build.cmd brand runs it for you)");
            Environment.ExitCode = 2;
            return;
        }
        string dir = args[0];
        File.WriteAllText(System.IO.Path.Combine(dir, "fluxgpu-icon.svg"), Svg(Icon(2), 512, 512));
        foreach (int n in new[] { 256, 512, 1024 }) File.WriteAllBytes(System.IO.Path.Combine(dir, "fluxgpu-icon-" + n + ".png"), Png(IconBitmap(n)));
        File.WriteAllBytes(System.IO.Path.Combine(dir, "mark.png"), Png(IconBitmap(48)));
        Ico(System.IO.Path.Combine(dir, "FluxGPU.ico"), new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });

        string[][] variants = { new[] { "dark", "#F4F7FB", Cyan }, new[] { "light", "#141924", "#007F96" } };
        List<RenderTargetBitmap> logos = new List<RenderTargetBitmap>();
        foreach (string[] v in variants)
        {
            double w;
            List<Shape> s = Lockup(v[1], v[2], out w);
            File.WriteAllText(System.IO.Path.Combine(dir, "fluxgpu-logo-for-" + v[0] + "-backgrounds.svg"), Svg(s, w, 512));
            RenderTargetBitmap bmp = Render((int)w, 512, delegate(DrawingContext dc) { Draw(dc, s); });
            File.WriteAllBytes(System.IO.Path.Combine(dir, "fluxgpu-logo-for-" + v[0] + "-backgrounds.png"), Png(bmp));
            logos.Add(bmp);
        }

        RenderTargetBitmap sheet = Render(1200, 900, delegate(DrawingContext dc)
        {
            dc.DrawRectangle(new SolidColorBrush(C("#0D1017")), null, new Rect(0, 0, 1200, 360));
            dc.DrawRectangle(new SolidColorBrush(C("#F3F3F3")), null, new Rect(0, 360, 1200, 360));
            dc.DrawRectangle(new SolidColorBrush(C("#202020")), null, new Rect(0, 720, 1200, 180));
            for (int i = 0; i < 2; i++)
            {
                double w = 960, h = w * logos[i].PixelHeight / logos[i].PixelWidth;
                dc.DrawImage(logos[i], new Rect((1200 - w) / 2, i * 360 + (360 - h) / 2, w, h));
            }
            double x = 150;
            foreach (int n in new[] { 128, 64, 48, 32, 24, 20, 16 })
            {
                dc.DrawImage(IconBitmap(n), new Rect(x, 810 - n / 2, n, n));
                x += n + 70;
            }
        });
        File.WriteAllBytes(System.IO.Path.Combine(dir, "preview.png"), Png(sheet));
    }
}
