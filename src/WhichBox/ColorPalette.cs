using System.Security.Cryptography;
using System.Text;
using Windows.UI;

namespace WhichBox;

/// <summary>
/// A palette of muted, gently colorful background colors for machine identification.
/// </summary>
internal static class ColorPalette
{
    /// <summary>
    /// Muted pastel colors -- desaturated enough to not be distracting,
    /// colorful enough to distinguish machines at a glance.
    /// HSL: saturation ~30-40%, lightness ~55-65%.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> Colors { get; } =
    [
        new("Dusty Rose",    Color.FromArgb(0xFF, 0xBC, 0x8F, 0x95)),
        new("Sage Green",    Color.FromArgb(0xFF, 0x8F, 0xAF, 0x8F)),
        new("Slate Blue",    Color.FromArgb(0xFF, 0x8F, 0x9F, 0xBC)),
        new("Warm Taupe",    Color.FromArgb(0xFF, 0xB0, 0xA0, 0x8E)),
        new("Muted Teal",    Color.FromArgb(0xFF, 0x7F, 0xAF, 0xAB)),
        new("Soft Coral",    Color.FromArgb(0xFF, 0xC4, 0x96, 0x88)),
        new("Lavender",      Color.FromArgb(0xFF, 0xA8, 0x96, 0xBC)),
        new("Moss",          Color.FromArgb(0xFF, 0x9A, 0xA8, 0x82)),
        new("Steel",         Color.FromArgb(0xFF, 0x92, 0x9E, 0xA8)),
        new("Apricot",       Color.FromArgb(0xFF, 0xC4, 0xA8, 0x82)),
        new("Powder Blue",   Color.FromArgb(0xFF, 0x8A, 0xAE, 0xC0)),
        new("Mauve",         Color.FromArgb(0xFF, 0xB3, 0x94, 0xA8)),
    ];

    /// <summary>
    /// Deterministically pick a default color based on the machine name.
    /// </summary>
    public static PaletteEntry GetDefaultColor(string machineName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(machineName.ToUpperInvariant()));
        var index = BitConverter.ToUInt32(hash, 0) % (uint)Colors.Count;
        return Colors[(int)index];
    }

    /// <summary>
    /// Returns a foreground color (white or dark gray) that contrasts well
    /// with the given background color.
    /// </summary>
    public static Color GetContrastForeground(Color background)
    {
        var luminance = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B;
        return luminance > 140
            ? Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)
            : Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0);
    }
}

internal sealed record PaletteEntry(string Name, Color Color);
