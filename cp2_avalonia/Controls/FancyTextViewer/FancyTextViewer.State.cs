/*
 * Copyright 2026 Lydian Scale Software
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using Avalonia.Media;
using FileConv;
using FancyTextDocument = FileConv.FancyText;

namespace cp2_avalonia.Controls.FancyTextViewer;

internal static class ViewerLayoutConstants
{
    public const float DefaultPageWidth = 8.5f;
    public const float DefaultLeftMargin = 0f;
    public const float DefaultRightMargin = 0f;
    private const float MinLeftMargin = 0f;
    private const float MinRightMargin = 0f;
    private const float MinMarginGap = 0.5f;
    private const float MinPageWidth = 0.5f;

    /// <summary>
    /// Clamps a page width to a viewer-safe minimum.
    /// </summary>
    /// <param name="pageWidth">The requested page width in inches.</param>
    /// <returns>The normalized page width.</returns>
    public static float NormalizePageWidth(float pageWidth)
    {
        return Math.Max(MinPageWidth, pageWidth);
    }

    /// <summary>
    /// Normalizes a left margin while keeping the right margin offset fixed.
    /// </summary>
    /// <param name="leftMargin">The requested left margin in inches.</param>
    /// <param name="rightMarginOffset">The right margin offset from the page edge in inches.</param>
    /// <param name="pageWidth">The page width in inches.</param>
    /// <returns>The normalized left margin in inches.</returns>
    public static float NormalizeLeftMargin(float leftMargin, float rightMarginOffset, float pageWidth)
    {
        pageWidth = NormalizePageWidth(pageWidth);
        rightMarginOffset = Math.Max(MinRightMargin, rightMarginOffset);
        leftMargin = Math.Max(MinLeftMargin, leftMargin);
        float maxLeftMargin = Math.Max(MinLeftMargin, pageWidth - rightMarginOffset - MinMarginGap);
        return Math.Min(leftMargin, maxLeftMargin);
    }

    /// <summary>
    /// Normalizes a right margin offset while keeping the left margin fixed.
    /// </summary>
    /// <param name="leftMargin">The left margin in inches.</param>
    /// <param name="rightMarginOffset">The requested right margin offset from the page edge in inches.</param>
    /// <param name="pageWidth">The page width in inches.</param>
    /// <returns>The normalized right margin offset in inches.</returns>
    public static float NormalizeRightMarginOffset(
            float leftMargin, float rightMarginOffset, float pageWidth)
    {
        pageWidth = NormalizePageWidth(pageWidth);
        leftMargin = Math.Max(MinLeftMargin, leftMargin);
        rightMarginOffset = Math.Max(MinRightMargin, rightMarginOffset);
        float maxRightMargin = Math.Max(MinRightMargin, pageWidth - leftMargin - MinMarginGap);
        return Math.Min(rightMarginOffset, maxRightMargin);
    }
}

/// <summary>
/// Identifies optional text rendering effects that may be applied to a fragment.
/// </summary>
[Flags]
public enum Effects
{
    /// <summary>
    /// No additional effect is applied.
    /// </summary>
    None = 0,
    /// <summary>
    /// Glyphs are drawn with an outline.
    /// </summary>
    Outline = 1,
    /// <summary>
    /// Glyphs are drawn with a shadow offset.
    /// </summary>
    Shadow = 2,
    /// <summary>
    /// Glyphs are underlined.
    /// </summary>
    Underline = 4
}

/// <summary>
/// Describes the vertical positioning mode for a fragment.
/// </summary>
public enum VertType
{
    /// <summary>
    /// Text is rendered on the normal baseline.
    /// </summary>
    Normal = 0,
    /// <summary>
    /// Text is rendered as superscript.
    /// </summary>
    Super = 1,
    /// <summary>
    /// Text is rendered as subscript.
    /// </summary>
    Subs = 2
}

/// <summary>
/// Stores the active source-document formatting while the logical document is being built.
/// </summary>
public class SourceTextState
{
    /// <summary>
    /// Gets or sets the active font family.
    /// </summary>
    public FancyTextDocument.FontFamily FontFamily { get; set; } = FancyTextDocument.DEFAULT_FONT;
    /// <summary>
    /// Gets or sets the active font size in points.
    /// </summary>
    public int FontSizePoints { get; set; } = 10;
    /// <summary>
    /// Gets or sets whether bold formatting is active.
    /// </summary>
    public bool Bold { get; set; }
    /// <summary>
    /// Gets or sets whether italic formatting is active.
    /// </summary>
    public bool Italic { get; set; }
    /// <summary>
    /// Gets or sets whether outline rendering is active.
    /// </summary>
    public bool Outline { get; set; }
    /// <summary>
    /// Gets or sets whether underline formatting is active.
    /// </summary>
    public bool Underline { get; set; }
    /// <summary>
    /// Gets or sets whether shadow rendering is active.
    /// </summary>
    public bool Shadow { get; set; }
    /// <summary>
    /// Gets or sets whether superscript formatting is active.
    /// </summary>
    public bool Superscript { get; set; }
    /// <summary>
    /// Gets or sets whether subscript formatting is active.
    /// </summary>
    public bool Subscript { get; set; }
    /// <summary>
    /// Gets or sets the active foreground color value (0xRRGGBB).
    /// </summary>
    public int ForeColor { get; set; } = ConvUtil.MakeRGB(0x00, 0x00, 0x00);
    /// <summary>
    /// Gets or sets the active background color value (0xRRGGBB).
    /// </summary>
    public int BackColor { get; set; } = ConvUtil.MakeRGB(0xff, 0xff, 0xff);

}

/// <summary>
/// Captures the resolved rendering style for a logical text fragment.
/// </summary>
public class TextFragmentStyle
{
    /// <summary>
    /// Gets or sets the fragment background color.
    /// </summary>
    public Color Background { get; set; } = Colors.White;
    /// <summary>
    /// Gets or sets the fragment foreground color.
    /// </summary>
    public Color Foreground { get; set; } = Colors.Black;
    /// <summary>
    /// Gets the resolved typeface used to render the fragment.
    /// </summary>
    public Typeface Typeface { get; private set; } = new("Menlo, Consolas, DejaVu Sans Mono, Courier New, monospace");
    /// <summary>
    /// Gets the resolved font size used to render the fragment.
    /// </summary>
    public double FontSize { get; private set; } = 10.0;
    private Effects Effects { get; set; } = Effects.None;
    private VertType VertType { get; set; } = VertType.Normal;

    /// <summary>
    /// Gets a value indicating whether underline rendering is enabled.
    /// </summary>
    public bool IsUnderline => Effects.HasFlag(Effects.Underline);
    /// <summary>
    /// Gets a value indicating whether outline rendering is enabled.
    /// </summary>
    public bool IsOutline => Effects.HasFlag(Effects.Outline);
    /// <summary>
    /// Gets a value indicating whether shadow rendering is enabled.
    /// </summary>
    public bool IsShadow => Effects.HasFlag(Effects.Shadow);
    /// <summary>
    /// Gets a value indicating whether superscript positioning is enabled.
    /// </summary>
    public bool IsSuperscript => VertType == VertType.Super;
    /// <summary>
    /// Gets a value indicating whether subscript positioning is enabled.
    /// </summary>
    public bool IsSubscript => VertType == VertType.Subs;

    /// <summary>
    /// Copies the current source formatting state into this resolved style.
    /// </summary>
    /// <param name="sourceState">The source formatting state to apply.</param>
    public void ApplySourceTextState(SourceTextState sourceState)
    {
        Background = ToAvaloniaColor(sourceState.BackColor);
        Foreground = ToAvaloniaColor(sourceState.ForeColor);

        var fontFamily = ResolveFontFamily(sourceState.FontFamily);
        Typeface = new Typeface(
            fontFamily,
            sourceState.Italic ? FontStyle.Italic : FontStyle.Normal,
            sourceState.Bold ? FontWeight.Bold : FontWeight.Normal);
        FontSize = sourceState.FontSizePoints;

        var effects = Effects.None;
        if (sourceState.Outline)
            effects |= Effects.Outline;
        if (sourceState.Shadow)
            effects |= Effects.Shadow;
        if (sourceState.Underline)
            effects |= Effects.Underline;

        Effects = effects;
        VertType = sourceState.Superscript ? VertType.Super :
            sourceState.Subscript ? VertType.Subs :
            VertType.Normal;
    }

    /// <summary>
    /// Converts a packed color integer (typically 0xRRGGBB) into an Avalonia color.
    /// </summary>
    /// <param name="color">The packed color value (typically 0xRRGGBB).</param>
    /// <returns>The converted Avalonia color.</returns>
    private static Color ToAvaloniaColor(int color)
    {
        var value = unchecked((uint)color);

        byte a = (value & 0xff000000) == 0
            ? (byte)0xff
            : (byte)((value >> 24) & 0xff);

        var r = (byte)((value >> 16) & 0xff);
        var g = (byte)((value >> 8) & 0xff);
        var b = (byte)(value & 0xff);

        return Color.FromArgb(a, r, g, b);
    }

    /// <summary>
    /// Maps FancyText font metadata to a cross-platform Avalonia font family stack.
    /// </summary>
    /// <param name="family">The FancyText font metadata.</param>
    /// <returns>The resolved Avalonia font family list.</returns>
    private static FontFamily ResolveFontFamily(FancyTextDocument.FontFamily family)
    {
        if (family.Name.Equals("Symbol", StringComparison.OrdinalIgnoreCase))
        {
            return new FontFamily("Symbol");
        }

        if (family.IsMono)
        {
            return family.IsSerif
                ? new FontFamily("Courier New, Courier, Liberation Mono, DejaVu Sans Mono, monospace")
                : new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, Menlo, monospace");
        }

        return family.IsSerif
            ? new FontFamily("Georgia, Times New Roman, Liberation Serif, Times, serif")
            : new FontFamily("Arial, Helvetica, Liberation Sans, sans-serif");
    }
}

/// <summary>
/// Tracks paragraph-level formatting while the logical document is being built.
/// </summary>
public class ParagraphState
{
    private readonly float _pageWidth;
    private float _leftMargin = ViewerLayoutConstants.DefaultLeftMargin;
    private float _rightMargin = ViewerLayoutConstants.DefaultRightMargin;

    public ParagraphState(float pageWidth)
    {
        _pageWidth = ViewerLayoutConstants.NormalizePageWidth(pageWidth);
    }

    /// <summary>
    /// Gets or sets the paragraph justification mode.
    /// </summary>
    public FancyTextDocument.Justification Justification { get; set; } = FancyTextDocument.Justification.Left;

    /// <summary>
    /// Gets or sets the normalized left margin in inches.
    /// </summary>
    public float LeftMargin
    {
        get => _leftMargin;
        set => _leftMargin = ViewerLayoutConstants.NormalizeLeftMargin(value, _rightMargin, _pageWidth);
    }

    /// <summary>
    /// Gets or sets the normalized right margin offset from the page edge, in inches.
    /// </summary>
    public float RightMargin
    {
        get => _rightMargin;
        set => _rightMargin =
            ViewerLayoutConstants.NormalizeRightMarginOffset(_leftMargin, value, _pageWidth);
    }
}
