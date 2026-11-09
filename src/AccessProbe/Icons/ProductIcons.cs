using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace AccessProbe;

/// <summary>Product and vendored Fluent vector artwork shared by all product windows.</summary>
public static class ProductIcons
{
    private sealed record Part(Geometry Geometry, bool Fill, bool Stroke, double Width, double Opacity, Brush? Color);
    private sealed record Artwork(string Label, string Group, string[] Colors, string LabelColor, string SoftColor, Part[] Parts, Drawing? ColorDrawing);
    private static readonly Geometry TileGeometry;
    private static readonly IReadOnlyDictionary<string, Artwork> Catalog;
    private static readonly Dictionary<string, DrawingImage> Tiles = new(StringComparer.Ordinal);

    static ProductIcons()
    {
        using var stream = typeof(ProductIcons).Assembly.GetManifestResourceStream("AccessProbe.Icons.catalog.json")
            ?? throw new InvalidOperationException("Product icon artwork is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        TileGeometry = Geometry.Parse(root.GetProperty("tilePath").GetString()!);
        TileGeometry.Freeze();
        var fluent = new ResourceDictionary
        {
            Source = new Uri("/AccessProbe;component/Assets/Icons/Fluent/FluentIcons.xaml", UriKind.Relative)
        };
        var brand = new ResourceDictionary
        {
            Source = new Uri("/AccessProbe;component/Assets/Icons/Brand/BrandIcon.xaml", UriKind.Relative)
        };
        Catalog = root.GetProperty("icons").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("key").GetString()!,
            entry => new Artwork(
                entry.GetProperty("label").GetString()!,
                entry.GetProperty("group").GetString()!,
                entry.TryGetProperty("colors", out var colors) ? colors.EnumerateArray().Select(color => color.GetString()!).ToArray() : [],
                entry.TryGetProperty("labelColor", out var labelColor) ? labelColor.GetString()! : "#5E6B80",
                entry.TryGetProperty("softColor", out var softColor) ? softColor.GetString()! : "#EDF0F5",
                entry.GetProperty("parts").EnumerateArray().Select(part =>
                {
                    var fillRule = part.TryGetProperty("fillRule", out var rule) && rule.GetString() == "nonzero" ? "F1 " : "F0 ";
                    var geometry = Geometry.Parse(fillRule + part.GetProperty("path").GetString()!);
                    geometry.Freeze();
                    return new Part(geometry,
                        part.TryGetProperty("fill", out var fill) && fill.GetBoolean(),
                        part.TryGetProperty("stroke", out var stroke) && stroke.GetBoolean(),
                        part.TryGetProperty("width", out var width) ? width.GetDouble() : 1.9,
                        part.TryGetProperty("opacity", out var opacity) ? opacity.GetDouble() : 1,
                        part.TryGetProperty("color", out var color) ? Brush(color.GetString()!) : null);
                }).ToArray(),
                entry.TryGetProperty("brandResource", out var brandResource)
                    ? ((DrawingImage)brand[brandResource.GetString()!]).Drawing
                    : entry.TryGetProperty("colorResource", out var colorResource)
                        ? ((DrawingImage)fluent[colorResource.GetString()!]).Drawing : null), StringComparer.Ordinal);
    }

    public static IReadOnlyList<string> Keys => Catalog.Keys.ToArray();
    public static ImageSource AppImage => Tile("app");
    public static string LabelColor(string key) => Find(key).LabelColor;
    public static string SoftColor(string key) => Find(key).SoftColor;

    public static DrawingImage Tile(string key)
    {
        if (Tiles.TryGetValue(key, out var cached)) return cached;
        var drawing = CreateDrawing(key, true, Brushes.White, false);
        var image = new DrawingImage(drawing);
        image.Freeze();
        Tiles[key] = image;
        return image;
    }

    public static DrawingGroup CreateDrawing(string key, bool tile, Brush foreground, bool highContrast)
    {
        var art = Find(key);
        var drawing = new DrawingGroup();
        // Keep a fixed artboard so silhouettes retain identical scale and optical padding.
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
        if (tile && !highContrast && art.ColorDrawing is not null)
        {
            drawing.Children.Add(art.ColorDrawing);
            if (drawing.CanFreeze) drawing.Freeze();
            return drawing;
        }
        if (tile)
        {
            var palette = art.Colors.Length >= 2 ? art.Colors : Catalog["document"].Colors;
            Brush background;
            if (highContrast)
                background = SystemColors.WindowBrush;
            else
            {
                var gradient = new LinearGradientBrush { StartPoint = new Point(0.1, 0), EndPoint = new Point(0.9, 1) };
                for (var index = 0; index < palette.Length; index++)
                    gradient.GradientStops.Add(new GradientStop(Color(palette[index]), index / (double)(palette.Length - 1)));
                gradient.Freeze();
                background = gradient;
            }
            drawing.Children.Add(new GeometryDrawing(background,
                new Pen(highContrast ? SystemColors.WindowTextBrush : Brush("#200C284D"), 0.45), TileGeometry));
            if (!highContrast)
            {
                var rim = Geometry.Parse("M 2 10 C 2 4.6 4.6 2 10 2 H 22 C 27.4 2 30 4.6 30 10");
                drawing.Children.Add(new GeometryDrawing(null, Pen(Brush("#55FFFFFF"), 0.65), rim));
            }
            foreground = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        }
        foreach (var part in art.Parts)
        {
            var color = tile && !highContrast ? part.Color ?? foreground : foreground;
            var layer = new DrawingGroup { Opacity = highContrast ? 1 : part.Opacity };
            layer.Children.Add(new GeometryDrawing(part.Fill ? color : null, part.Stroke ? Pen(color, part.Width) : null, part.Geometry));
            drawing.Children.Add(layer);
        }
        if (drawing.CanFreeze) drawing.Freeze();
        return drawing;
    }

    private static Artwork Find(string key) => Catalog.TryGetValue(key, out var art) ? art : Catalog["document"];
    private static Color Color(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static Brush Brush(string value)
    {
        var brush = new SolidColorBrush(Color(value));
        brush.Freeze();
        return brush;
    }
    private static Pen Pen(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }
}
