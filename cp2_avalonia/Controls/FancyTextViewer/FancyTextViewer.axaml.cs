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
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Controls;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FancyTextDocument = FileConv.FancyText;

using cp2_avalonia.Services;

namespace cp2_avalonia.Controls.FancyTextViewer;



/// <summary>
/// Renders immutable FancyText documents with selection, copy, zoom, and viewer chrome options.
/// </summary>
public partial class FancyTextViewer : Control
{
    private readonly record struct SelectionRange(int Start, int End)
    {
        public int NormalizedStart => Math.Min(Start, End);
        public int NormalizedEnd => Math.Max(Start, End);
        public bool HasSelection => Start != End;
    }

    /// <summary>
    /// Identifies the <see cref="Document"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, FancyTextDocument?> DocumentProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, FancyTextDocument?>(
            nameof(Document),
            o => o.Document,
            (o, v) => o.Document = v);

    /// <summary>
    /// Identifies the <see cref="Background"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, Color> BackgroundProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, Color>(
            nameof(Background),
            o => o.Background,
            (o, v) => o.Background = v);

    /// <summary>
    /// Identifies the <see cref="Foreground"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, Color> ForegroundProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, Color>(
            nameof(Foreground),
            o => o.Foreground,
            (o, v) => o.Foreground = v);

    /// <summary>
    /// Identifies the <see cref="Scale"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, double> ScaleProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, double>(
            nameof(Scale),
            o => o.Scale,
            (o, v) => o.Scale = v);

    /// <summary>
    /// Identifies the <see cref="PageWidth"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, double> PageWidthProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, double>(
            nameof(PageWidth),
            o => o.PageWidth,
            (o, v) => o.PageWidth = v);

    /// <summary>
    /// Identifies the <see cref="WordWrap"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, bool> WordWrapProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, bool>(
            nameof(WordWrap),
            o => o.WordWrap,
            (o, v) => o.WordWrap = v);

    /// <summary>
    /// Identifies the <see cref="ShowPageBoundaries"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, bool> ShowPageBoundariesProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, bool>(
            nameof(ShowPageBoundaries),
            o => o.ShowPageBoundaries,
            (o, v) => o.ShowPageBoundaries = v);

    /// <summary>
    /// Identifies the <see cref="ShowMarginBoundaries"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, bool> ShowMarginBoundariesProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, bool>(
            nameof(ShowMarginBoundaries),
            o => o.ShowMarginBoundaries,
            (o, v) => o.ShowMarginBoundaries = v);

    /// <summary>
    /// Identifies the <see cref="ShowRuler"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, bool> ShowRulerProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, bool>(
            nameof(ShowRuler),
            o => o.ShowRuler,
            (o, v) => o.ShowRuler = v);

    /// <summary>
    /// Identifies the <see cref="Padding"/> direct property.
    /// </summary>
    public static readonly DirectProperty<FancyTextViewer, Thickness> PaddingProperty =
        AvaloniaProperty.RegisterDirect<FancyTextViewer, Thickness>(
            nameof(Padding),
            o => o.Padding,
            (o, v) => o.Padding = v);

    private readonly TextFragmentStyle state = new();
    private FancyTextDocument? _document;
    private Color _background = Colors.White;
    private Color _foreground = Colors.Black;

    /// <summary>
    /// Gets or sets the viewer background color.
    /// </summary>
    public Color Background
    {
        get => _background;
        set => SetBackground(value);
    }

    /// <summary>
    /// Gets or sets the default foreground color used when rendering content.
    /// </summary>
    public Color Foreground
    {
        get => _foreground;
        set => SetForeground(value);
    }

    private const double MIN_SCALE = 0.25;
    private const double ZOOM_STEP = 0.10;
    private double _scale = 1.00;
    private double _pageWidth = ViewerLayoutConstants.DefaultPageWidth;

    /// <summary>
    /// Gets or sets the zoom factor applied to the rendered document.
    /// </summary>
    public double Scale
    {
        get => _scale;
        set => SetScale(value);
    }

    /// <summary>
    /// Gets or sets the document page width in inches.
    /// </summary>
    public double PageWidth
    {
        get => _pageWidth;
        set => SetPageWidth(value);
    }
    
    private bool _wordWrap = true;
    /// <summary>
    /// Gets or sets whether long lines are wrapped within the current margins.
    /// </summary>
    public bool WordWrap
    {
        get => _wordWrap;
        set => SetWordWrap(value);
    }

    private bool _showPageBoundaries = true;
    /// <summary>
    /// Gets or sets whether page boundary guides are drawn.
    /// </summary>
    public bool ShowPageBoundaries
    {
        get => _showPageBoundaries;
        set => SetShowPageBoundaries(value);
    }

    private bool _showMarginBoundaries = true;
    /// <summary>
    /// Gets or sets whether margin guides are drawn.
    /// </summary>
    public bool ShowMarginBoundaries
    {
        get => _showMarginBoundaries;
        set => SetShowMarginBoundaries(value);
    }

    private bool _showRuler;
    /// <summary>
    /// Gets or sets whether the ruler is shown above the document.
    /// </summary>
    public bool ShowRuler
    {
        get => _showRuler;
        set => SetShowRuler(value);
    }
    
    private Thickness _padding = new Thickness(10);
    /// <summary>
    /// Gets or sets the padding around the rendered document.
    /// </summary>
    public Thickness Padding
    {
        get => _padding;
        set => SetPadding(value);
    }

    /// <summary>
    /// Gets or sets the immutable FancyText document to render.
    /// </summary>
    public FancyTextDocument? Document
    {
        get => _document;
        set => SetDocument(value);
    }
    
    private const double PAGE_BREAK_PIXEL_HEIGHT = 6.0;
    private const double RULER_HEIGHT = 16.0;
    private const double SUB_SUPER_SCALE = 0.67;
    private const int LINE_SPACING = 2;
    // Maximum number of paragraph slots kept realized (shaped) before stale ones are
    // evicted; bounds memory when scrolling through very large documents.
    private const int MAX_REALIZED_SLOTS = 1024;
    // Maximum number of slots realized while computing selection bounds for
    // scroll-into-view, so a huge selection cannot force whole-document shaping.
    private const int MAX_BOUNDS_REALIZE = 64;
    
    private double documentWidth;
    private double documentHeight;

    private FancyTextLogicalDocument? logicalDocument;
    private readonly List<PhysicalElement> _visibleElementsBuffer = [];
    // Paragraph slots in document order (ascending Y). Slots carry metrics-estimated
    // heights so the scroll extent is known without shaping any text; paragraph content
    // is shaped ("realized") on demand as it enters the viewport.
    private readonly List<ParagraphSlot> _slots = [];
    private int _realizedSlotCount;
    private int _frameCounter;
    // Widest realized content extent seen so far; may exceed the estimate-based width.
    private double _realizedMaxRight;
    private bool _measureInvalidatePending;
    private ScrollViewer? _scrollHost;
    private bool _layoutDirty = true;
    private bool _documentHasShadowElements;
    // True when the current document has no annotations (plain/simple text). Plain
    // documents parse with one fragment per line when word wrap is off, so fragment
    // granularity depends on the wrap mode and wrap toggles trigger a (cheap) reparse.
    private bool _documentIsPlain;
    private IBrush _viewerBackgroundBrush = GetCachedBrush(Colors.White);
    private int? _selectionAnchorOffset;
    private int? _selectionCaretOffset;
    private bool _isSelecting;
    private readonly MenuItem _showRulerMenuItem;
    private readonly MenuItem _wordWrapMenuItem;
    private readonly MenuItem _showPageBoundariesMenuItem;
    private readonly MenuItem _showMarginsMenuItem;
    private readonly MenuItem _selectAllMenuItem;
    private readonly MenuItem _copyMenuItem;

    private static readonly IBrush SelectionBrush =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x80, 0x33, 0x99, 0xff));
    private const double SHADOW_PASS_OPACITY = 0.33;

    // Extra screen-space margin (in scaled pixels) rendered beyond the viewport so that
    // small scroll steps have content ready before the next repaint lands.
    private const double VIEWPORT_OVERSCAN = 256.0;

    // Pens shared across all render passes; these never change so build them once.
    private static readonly IPen PageGuidePen = new Pen(Brushes.Blue, 1, DashStyle.Dash);
    private static readonly IPen MarginGuidePen = new Pen(Brushes.Red, 1, DashStyle.Dash);
    private static readonly IPen RulerBorderPen = new Pen(Brushes.Black);
    private static readonly IPen[] RulerTickPens =
    [
        new Pen(Brushes.Black, 2.0),    // whole inch
        new Pen(Brushes.Black, 1.5),    // half inch
        new Pen(Brushes.Black, 1.0),    // quarter inch
        new Pen(Brushes.Black, 0.5)     // eighth inch
    ];

    // The ruler is rasterized once into a bitmap and blitted each frame. It is rebuilt
    // only when the page width, zoom, or screen render scaling changes.
    private RenderTargetBitmap? _rulerBitmap;
    private double _rulerBitmapPageWidth = -1;
    private double _rulerBitmapScale = -1;
    private double _rulerBitmapRenderScaling = -1;

    // Solid color brushes are shared document-wide; most elements use the same few colors.
    private static readonly Dictionary<Color, IBrush> SolidBrushCache = new();

    /// <summary>
    /// Returns a shared immutable brush for the supplied color.
    /// </summary>
    /// <param name="color">The color to get a brush for.</param>
    /// <returns>A cached immutable solid color brush.</returns>
    internal static IBrush GetCachedBrush(Color color)
    {
        if (!SolidBrushCache.TryGetValue(color, out IBrush? brush))
        {
            brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color);
            SolidBrushCache[color] = brush;
        }
        return brush;
    }

    private readonly record struct GlyphMetricsInfo(
        double UnderlineThickness, double UnderlinePosition, double DesignEmHeight);

    // Glyph typeface metrics are expensive to look up (font resolution); cache per typeface.
    private static readonly Dictionary<Typeface, GlyphMetricsInfo> GlyphMetricsCache = new();

    /// <summary>
    /// Returns cached glyph typeface design metrics for the supplied typeface.
    /// </summary>
    /// <param name="tf">The typeface to look up.</param>
    /// <returns>The cached design metrics.</returns>
    private static GlyphMetricsInfo GetGlyphMetricsInfo(Typeface tf)
    {
        if (!GlyphMetricsCache.TryGetValue(tf, out GlyphMetricsInfo info))
        {
            var met = tf.GlyphTypeface.Metrics;
            info = new GlyphMetricsInfo(met.UnderlineThickness, met.UnderlinePosition,
                met.DesignEmHeight);
            GlyphMetricsCache[tf] = info;
        }
        return info;
    }

    /// <summary>
    /// Initializes the viewer and its context menu commands.
    /// </summary>
    public FancyTextViewer()
    {
        _showRulerMenuItem = new MenuItem
        {
            Header = "Show Ruler",
            ToggleType = MenuItemToggleType.CheckBox
        };
        _wordWrapMenuItem = new MenuItem
        {
            Header = "Word Wrap",
            ToggleType = MenuItemToggleType.CheckBox
        };
        _showPageBoundariesMenuItem = new MenuItem
        {
            Header = "Show Page Boundaries",
            ToggleType = MenuItemToggleType.CheckBox
        };
        _showMarginsMenuItem = new MenuItem
        {
            Header = "Show Margins",
            ToggleType = MenuItemToggleType.CheckBox
        };
        _selectAllMenuItem = new MenuItem { Header = "Select All" };
        _copyMenuItem = new MenuItem { Header = "Copy" };

        InitializeComponent();
        Focusable = true;

        _showRulerMenuItem.Click += (_, _) => ShowRuler = !ShowRuler;
        _wordWrapMenuItem.Click += (_, _) => WordWrap = !WordWrap;
        _showPageBoundariesMenuItem.Click += (_, _) => ShowPageBoundaries = !ShowPageBoundaries;
        _showMarginsMenuItem.Click += (_, _) => ShowMarginBoundaries = !ShowMarginBoundaries;
        _selectAllMenuItem.Click += (_, _) => SelectAll();
        _copyMenuItem.Click += (_, _) => CopySelectionToClipboard();

        var contextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                _showRulerMenuItem,
                _wordWrapMenuItem,
                _showPageBoundariesMenuItem,
                _showMarginsMenuItem,
                new Separator(),
                _selectAllMenuItem,
                _copyMenuItem
            }
        };
        contextMenu.Opening += (_, _) => UpdateContextMenuState();
        ContextMenu = contextMenu;
    }

    /// <summary>
    /// Replaces the current document and rebuilds the viewer state when it changes.
    /// </summary>
    /// <param name="value">The new document to render.</param>
    private void SetDocument(FancyTextDocument? value)
    {
        if (ReferenceEquals(_document, value))
        {
            return;
        }

        SetAndRaise(DocumentProperty, ref _document, value);
        RebuildDocument();
    }

    /// <summary>
    /// Updates the viewer background color.
    /// </summary>
    /// <param name="value">The new background color.</param>
    private void SetBackground(Color value)
    {
        if (_background == value)
        {
            return;
        }

        state.Background = value;
        SetAndRaise(BackgroundProperty, ref _background, value);
        _viewerBackgroundBrush = GetCachedBrush(value);
        InvalidateVisual();
    }

    /// <summary>
    /// Updates the default foreground color used by the viewer.
    /// </summary>
    /// <param name="value">The new foreground color.</param>
    private void SetForeground(Color value)
    {
        if (_foreground == value)
        {
            return;
        }

        state.Foreground = value;
        SetAndRaise(ForegroundProperty, ref _foreground, value);
        InvalidateVisual();
    }

    /// <summary>
    /// Applies the requested scale after clamping it to the supported zoom range.
    /// </summary>
    /// <param name="value">The requested scale factor.</param>
    private void SetScale(double value)
    {
        var clamped = double.IsNaN(value) || value < MIN_SCALE ? MIN_SCALE : value;
        if (Math.Abs(_scale - clamped) < double.Epsilon)
        {
            return;
        }

        SetAndRaise(ScaleProperty, ref _scale, clamped);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Updates page width and rebuilds layout.
    /// </summary>
    /// <param name="value">The requested page width in inches.</param>
    private void SetPageWidth(double value)
    {
        var clamped = ViewerLayoutConstants.NormalizePageWidth((float)value);
        if (Math.Abs(_pageWidth - clamped) < double.Epsilon)
        {
            return;
        }

        SetAndRaise(PageWidthProperty, ref _pageWidth, clamped);
        RebuildDocument();
    }

    /// <summary>
    /// Enables or disables word wrapping and refreshes layout.
    /// </summary>
    /// <param name="value"><c>true</c> to wrap long lines.</param>
    private void SetWordWrap(bool value)
    {
        if (_wordWrap == value)
        {
            return;
        }

        SetAndRaise(WordWrapProperty, ref _wordWrap, value);
        UpdateContextMenuState();
        if (_documentIsPlain)
        {
            // Plain documents use line-granularity fragments when wrap is off, so the
            // wrap mode changes fragment granularity; reparse (no shaping, so cheap)
            // while keeping the offset-based selection.
            RebuildDocument(preserveSelection: true);
        }
        else
        {
            InvalidateRealizations();
        }
    }

    /// <summary>
    /// Enables or disables page boundary guides.
    /// </summary>
    /// <param name="value"><c>true</c> to draw page boundary guides.</param>
    private void SetShowPageBoundaries(bool value)
    {
        if (_showPageBoundaries == value)
        {
            return;
        }

        SetAndRaise(ShowPageBoundariesProperty, ref _showPageBoundaries, value);
        UpdateContextMenuState();
        InvalidateVisual();
    }

    /// <summary>
    /// Enables or disables margin guide rendering.
    /// </summary>
    /// <param name="value"><c>true</c> to draw margin guides.</param>
    private void SetShowMarginBoundaries(bool value)
    {
        if (_showMarginBoundaries == value)
        {
            return;
        }

        SetAndRaise(ShowMarginBoundariesProperty, ref _showMarginBoundaries, value);
        UpdateContextMenuState();
        InvalidateVisual();
    }

    /// <summary>
    /// Enables or disables the top ruler and refreshes layout.
    /// </summary>
    /// <param name="value"><c>true</c> to show the ruler.</param>
    private void SetShowRuler(bool value)
    {
        if (_showRuler == value)
        {
            return;
        }

        SetAndRaise(ShowRulerProperty, ref _showRuler, value);
        UpdateContextMenuState();
        MarkLayoutDirty();
    }

    /// <summary>
    /// Updates the viewer padding and refreshes layout.
    /// </summary>
    /// <param name="value">The new padding.</param>
    private void SetPadding(Thickness value)
    {
        if (_padding == value)
        {
            return;
        }

        SetAndRaise(PaddingProperty, ref _padding, value);
        InvalidateRealizations();
    }

    /// <summary>
    /// Re-parses the current document and rebuilds physical layout state.
    /// </summary>
    /// <param name="preserveSelection">Keep the offset-based selection; valid when the
    /// document text is unchanged (e.g. a word-wrap mode toggle).</param>
    private void RebuildDocument(bool preserveSelection = false)
    {
        logicalDocument = null;
        ClearPhysicalDocument();
        if (!preserveSelection)
        {
            ClearSelection();
        }

        if (Document is null)
        {
            _documentIsPlain = false;
            documentWidth = 0;
            documentHeight = 0;
            _layoutDirty = false;
            UpdateContextMenuState();
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        _documentIsPlain = !Document.Any();

        FancyTextParser parser = new();
        logicalDocument = parser.Parse(Document, (float)PageWidth,
            coarseLineFragments: _documentIsPlain && !WordWrap);

        BuildParagraphSlots();
        _layoutDirty = true;

        UpdateContextMenuState();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Marks document layout as dirty and schedules a relayout.
    /// </summary>
    private void MarkLayoutDirty()
    {
        _layoutDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Discards realized slot layouts and measured heights, reverting to metric-based
    /// estimates. Used when a property that changes line breaking or horizontal
    /// placement (word wrap, padding) is modified; content re-realizes on next render.
    /// </summary>
    private void InvalidateRealizations()
    {
        foreach (var slot in _slots)
        {
            slot.Realized = null;
            slot.HeightIsActual = false;
        }
        _realizedSlotCount = 0;
        _realizedMaxRight = 0;
        MarkLayoutDirty();
    }

    /// <summary>
    /// Ensures physical element positions and document extents are up to date.
    /// </summary>
    private void EnsureLayout()
    {
        if (!_layoutDirty)
        {
            return;
        }

        LayoutPhysicalDocument();
        _layoutDirty = false;
    }

    /// <summary>
    /// Selects a text range by source offsets and optionally scrolls it into view.
    /// </summary>
    /// <param name="offset">Selection start offset.</param>
    /// <param name="length">Selection length.</param>
    /// <param name="bringIntoView">True to scroll the selected range into view.</param>
    public void SelectRange(int offset, int length, bool bringIntoView = true)
    {
        if (Document == null || Document.Text.Length == 0)
        {
            ClearSelection();
            UpdateContextMenuState();
            InvalidateVisual();
            return;
        }

        int start = Math.Clamp(offset, 0, Document.Text.Length);
        int end = Math.Clamp(start + Math.Max(0, length), 0, Document.Text.Length);
        _selectionAnchorOffset = start;
        _selectionCaretOffset = end;
        _isSelecting = false;

        UpdateContextMenuState();
        InvalidateVisual();

        if (!bringIntoView || start == end)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!TryGetSelectionBounds(start, end, out Rect unscaledRect))
            {
                return;
            }

            Rect scaledRect = new Rect(
                unscaledRect.X * Scale,
                unscaledRect.Y * Scale,
                Math.Max(1, unscaledRect.Width * Scale),
                Math.Max(1, unscaledRect.Height * Scale));
            ScrollRectIntoView(scaledRect);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Starts or updates a text selection when the user presses the pointer over content.
    /// </summary>
    /// <param name="e">The pointer event data.</param>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var hitOffset = HitTestDocumentOffset(e.GetPosition(this));
        if (hitOffset == null)
        {
            return;
        }

        Focus();

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _selectionAnchorOffset == null)
        {
            _selectionAnchorOffset = hitOffset.Value;
        }

        _selectionCaretOffset = hitOffset.Value;
        _isSelecting = true;
        e.Pointer.Capture(this);
        UpdateContextMenuState();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Extends the current selection while the pointer is being dragged.
    /// </summary>
    /// <param name="e">The pointer event data.</param>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_isSelecting)
        {
            return;
        }

        var hitOffset = HitTestDocumentOffset(e.GetPosition(this));
        if (hitOffset == null || _selectionCaretOffset == hitOffset.Value)
        {
            return;
        }

        // Context menu state is refreshed on menu Opening and pointer release; no need
        // to update it on every drag step.
        _selectionCaretOffset = hitOffset.Value;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Completes an active pointer selection gesture.
    /// </summary>
    /// <param name="e">The pointer event data.</param>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_isSelecting)
        {
            return;
        }

        var hitOffset = HitTestDocumentOffset(e.GetPosition(this));
        if (hitOffset != null)
        {
            _selectionCaretOffset = hitOffset.Value;
        }

        _isSelecting = false;
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
        }

        InvalidateVisual();
        UpdateContextMenuState();
        e.Handled = true;
    }

    /// <summary>
    /// Handles Ctrl/Cmd + wheel zoom gestures.
    /// </summary>
    /// <param name="e">The pointer wheel event data.</param>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (TryAdjustScaleFromPointerWheel(e.KeyModifiers, e.Delta.Y))
        {
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    /// <summary>
    /// Applies a Ctrl/Cmd + wheel zoom gesture from an external container.
    /// </summary>
    /// <param name="modifiers">Current key modifiers.</param>
    /// <param name="wheelDeltaY">Pointer wheel delta.</param>
    /// <returns><c>true</c> if the scale changed.</returns>
    public bool TryAdjustScaleFromPointerWheel(KeyModifiers modifiers, double wheelDeltaY)
    {
        if (!HasCommandModifier(modifiers))
        {
            return false;
        }
        return TryAdjustScale(wheelDeltaY * ZOOM_STEP);
    }

    /// <summary>
    /// Opens the viewer context menu.
    /// </summary>
    /// <returns><c>true</c> if the menu was opened.</returns>
    public bool TryOpenContextMenu()
    {
        if (ContextMenu == null)
        {
            return false;
        }

        Focus();
        ContextMenu.Open(this);
        return true;
    }

    /// <summary>
    /// Handles keyboard shortcuts for zoom, copy, and select-all.
    /// </summary>
    /// <param name="e">The key event data.</param>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!HasCommandModifier(e.KeyModifiers))
        {
            return;
        }

        if (e.Key is Key.OemPlus or Key.Add)
        {
            if (TryAdjustScale(ZOOM_STEP))
            {
                e.Handled = true;
            }
        }
        else if (e.Key is Key.OemMinus or Key.Subtract)
        {
            if (TryAdjustScale(-ZOOM_STEP))
            {
                e.Handled = true;
            }
        }
        else if (e.Key == Key.C)
        {
            CopySelectionToClipboard();
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Determines whether the current key modifiers include the platform command modifier.
    /// </summary>
    /// <param name="modifiers">The key modifiers to inspect.</param>
    /// <returns><c>true</c> when the platform command modifier is present.</returns>
    private static bool HasCommandModifier(KeyModifiers modifiers)
    {
        return modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
    }

    /// <summary>
    /// Applies a relative zoom delta if it changes the effective scale.
    /// </summary>
    /// <param name="delta">The scale delta to apply.</param>
    /// <returns><c>true</c> when the scale changed.</returns>
    private bool TryAdjustScale(double delta)
    {
        if (Math.Abs(delta) < double.Epsilon)
        {
            return false;
        }

        var nextScale = Math.Max(MIN_SCALE, Math.Round(Scale + delta, 2, MidpointRounding.AwayFromZero));
        if (Math.Abs(nextScale - Scale) < double.Epsilon)
        {
            return false;
        }

        Scale = nextScale;
        return true;
    }

    /// <summary>
    /// Measures the scaled document size used by the containing scroll viewer.
    /// </summary>
    /// <param name="availableSize">The available size from the layout system.</param>
    /// <returns>The desired scaled size of the document.</returns>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Perform layout to calculate document extents before measuring
        EnsureLayout();
        
        // Return the scaled document extents to enable proper scrolling
        return new Size(documentWidth * Scale, documentHeight * Scale);
    }
    
    /// <summary>
    /// Reports the arranged size needed for the scaled document.
    /// </summary>
    /// <param name="finalSize">The final size proposed by the layout system.</param>
    /// <returns>The arranged scaled document size.</returns>
    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureLayout();

        // Keep the viewer pinned to the top-left of its viewport by occupying at least
        // the arranged size. When content is smaller than the viewport this leaves extra
        // space on the right/bottom instead of centering the rendered document.
        double scaledWidth = documentWidth * Scale;
        double scaledHeight = documentHeight * Scale;
        return new Size(
            Math.Max(finalSize.Width, scaledWidth),
            Math.Max(finalSize.Height, scaledHeight));
    }

    /// <summary>
    /// Locates the hosting ScrollViewer so rendering can be culled to the visible viewport.
    /// Rendering must be invalidated on scroll because only the viewport region is drawn.
    /// </summary>
    /// <param name="e">The attachment event data.</param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollHost = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollHost != null)
        {
            _scrollHost.ScrollChanged += OnScrollHostScrollChanged;
        }
    }

    /// <summary>
    /// Releases the ScrollViewer subscription when the control leaves the visual tree.
    /// </summary>
    /// <param name="e">The detachment event data.</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scrollHost != null)
        {
            _scrollHost.ScrollChanged -= OnScrollHostScrollChanged;
            _scrollHost = null;
        }

        // Release the cached ruler bitmap; it is rebuilt on demand after re-attach, and the
        // render scaling may differ in a new host.
        _rulerBitmap?.Dispose();
        _rulerBitmap = null;
        _rulerBitmapPageWidth = -1;
        _rulerBitmapScale = -1;
        _rulerBitmapRenderScaling = -1;
    }

    private void OnScrollHostScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        InvalidateVisual();
    }


    /// <summary>
    /// Lightweight per-paragraph virtualization record. Slots are created for the whole
    /// document without shaping any text: heights come from cached font metrics, so the
    /// scroll extent is available immediately. The expensive shaped layout (Realized) is
    /// built on demand for slots that enter the viewport, and may be evicted again.
    /// </summary>
    private sealed class ParagraphSlot
    {
        // Source paragraph; null for page-break marker slots.
        public FancyTextLogicalParagraph? Paragraph;
        public bool IsPageBreak;
        // True when the paragraph contains text fragments (i.e. it can be realized).
        public bool HasText;
        // Estimated height of a single line (max fragment ascent+descent).
        public double LineHeightEst;
        // Estimated natural width of all fragments (average-advance based).
        public double TotalWidthEst;
        // Source text offsets covered by this slot; monotonic across the slot list.
        public int StartOffset;
        public int EndOffset;
        // Vertical advance including line spacing; estimated until realized, then sticky
        // so eviction and re-realization never shift the layout.
        public double Height;
        public bool HeightIsActual;
        public double YPosition;
        // Shaped and positioned content, or null when not (or no longer) realized.
        public PhysicalParagraph? Realized;
        // The YPosition the realized content was laid out at, for cheap re-shifting.
        public double RealizedAtY;
        public int LastUsedFrame;
    }

    private class PhysicalParagraph
    {
        public FancyTextDocument.Justification Justification { get; set; } =
            FancyTextDocument.Justification.Left;

        public float LeftMargin { get; set; } = ViewerLayoutConstants.DefaultLeftMargin;
        public float RightMargin { get; set; } = ViewerLayoutConstants.DefaultRightMargin;

        public double MaxAscender { get; set; }
        public double MaxHeight { get; set; }

        // Position information for rendering
        public double YPosition { get; set; }
        public double MinX { get; set; } = double.MaxValue;
        public double MaxX { get; set; } = double.MinValue;

        public List<PhysicalElement> Elements { get; } = [];
        public List<PhysicalParagraph> Lines { get; } = [];
    }


    /// <summary>
    /// Clears the cached slot list and realization state.
    /// </summary>
    private void ClearPhysicalDocument()
    {
        _slots.Clear();
        _realizedSlotCount = 0;
        _realizedMaxRight = 0;
        _documentHasShadowElements = false;
    }

    /// <summary>
    /// Estimates a fragment's line-height contribution (ascent + descent) from cached font
    /// metrics without shaping the fragment's text. Mirrors the PhysicalElement height
    /// math; exact whenever the fragment's glyphs resolve within the probed font.
    /// </summary>
    /// <param name="style">The fragment's resolved style.</param>
    /// <returns>The estimated ascent + descent for the fragment.</returns>
    private static double EstimateFragmentHeight(TextFragmentStyle style)
    {
        bool isSuperscript = style.IsSuperscript;
        bool isSubscript = style.IsSubscript;
        double fontSize = (isSuperscript || isSubscript)
            ? style.FontSize * SUB_SUPER_SCALE
            : style.FontSize;
        var metrics = GetMyFontMetrics(style.Typeface, fontSize);
        double renderAscent = metrics.MaxAscent;
        double renderDescent = metrics.MaxDescent;

        if (isSuperscript || isSubscript)
        {
            var fullMetrics = GetMyFontMetrics(style.Typeface, style.FontSize);
            double fullAscent = fullMetrics.MaxAscent;
            if (isSuperscript)
            {
                return fullAscent + renderDescent;
            }
            double baselineShift = Math.Max(0, fullAscent - renderAscent);
            return fullAscent + baselineShift + renderDescent;
        }
        return renderAscent + renderDescent;
    }

    /// <summary>
    /// Estimates a fragment's advance width from the cached average character advance,
    /// without shaping. Exact for monospace text; approximate for proportional fonts.
    /// </summary>
    /// <param name="fragment">The fragment to estimate.</param>
    /// <returns>The estimated advance width in DIPs.</returns>
    private static double EstimateFragmentWidth(TextFragmentBase fragment)
    {
        var style = fragment.Style;
        double fontSize = (style.IsSuperscript || style.IsSubscript)
            ? style.FontSize * SUB_SUPER_SCALE
            : style.FontSize;
        var metrics = GetMyFontMetrics(style.Typeface, fontSize);
        return (fragment.EndOffset - fragment.StartOffset) * metrics.AvgCharWidth;
    }

    /// <summary>
    /// Converts the logical document into paragraph slots with estimated extents. No text
    /// is shaped here — only cached per-(typeface, size) metric probes are consulted — so
    /// this is fast even for very large documents. Mirrors the paragraph/page-break
    /// structure the old eager build produced.
    /// </summary>
    private void BuildParagraphSlots()
    {
        if (logicalDocument == null) return;

        ClearPhysicalDocument();

        int pageCount = logicalDocument.Pages.Count;
        int currentPageIndex = 0;
        double lastFragmentHeight = 0;  // height basis for blank paragraphs
        int lastEndOffset = 0;

        foreach (var page in logicalDocument.Pages)
        {
            foreach (var paragraph in page.Paragraphs)
            {
                // Paragraphs holding a NewPage annotation are represented by the page
                // break marker slot added between pages instead.
                bool hasNewPageAnnotation = paragraph.Fragments.Any(f =>
                    f is AnnotationItem { Format: AnnotationItem.AnnotationType.NewPage });
                if (hasNewPageAnnotation)
                {
                    continue;
                }

                bool hasTextFragments = false;
                double maxFragmentHeight = 0;
                double totalWidth = 0;
                int startOffset = int.MaxValue;
                int endOffset = lastEndOffset;

                foreach (var fragment in paragraph.Fragments)
                {
                    if (fragment is not TextFragmentBase textFragment) continue;

                    hasTextFragments = true;
                    double fragmentHeight = EstimateFragmentHeight(textFragment.Style);
                    maxFragmentHeight = Math.Max(maxFragmentHeight, fragmentHeight);
                    totalWidth += EstimateFragmentWidth(textFragment);
                    startOffset = Math.Min(startOffset, textFragment.StartOffset);
                    endOffset = Math.Max(endOffset, textFragment.EndOffset);
                    lastFragmentHeight = fragmentHeight;
                    _documentHasShadowElements |= textFragment.Style.IsShadow;
                }

                if (hasTextFragments)
                {
                    _slots.Add(new ParagraphSlot
                    {
                        Paragraph = paragraph,
                        HasText = true,
                        LineHeightEst = maxFragmentHeight,
                        TotalWidthEst = totalWidth,
                        StartOffset = startOffset,
                        EndOffset = endOffset
                    });
                    lastEndOffset = endOffset;
                }
                else if (lastFragmentHeight > 0)
                {
                    // Blank paragraph: occupies one line at the preceding fragment height.
                    _slots.Add(new ParagraphSlot
                    {
                        Paragraph = paragraph,
                        LineHeightEst = lastFragmentHeight,
                        StartOffset = lastEndOffset,
                        EndOffset = lastEndOffset
                    });
                }
                // else: annotation-only paragraph with no known height - skipped
            }

            // Add a page break marker slot after each page (except the last)
            currentPageIndex++;
            if (currentPageIndex < pageCount)
            {
                _slots.Add(new ParagraphSlot
                {
                    IsPageBreak = true,
                    LineHeightEst = PAGE_BREAK_PIXEL_HEIGHT,
                    StartOffset = lastEndOffset,
                    EndOffset = lastEndOffset
                });
            }
        }
    }


    /// <summary>
    /// Computes slot Y positions and document extents from slot heights. Heights come
    /// from metric-based estimates until a slot is realized, after which the measured
    /// height is sticky. No text shaping occurs here, so this is fast even for very
    /// large documents.
    /// </summary>
    private void LayoutPhysicalDocument()
    {
        const double IN_TO_DIP = 96.0;
        double pageWidthDip = PageWidth * IN_TO_DIP;

        // Start with top padding offset, plus ruler height if shown (with 2px gap)
        double ypos = Padding.Top + (ShowRuler ? RULER_HEIGHT + 2 : 0);
        double estMaxRight = 0;

        foreach (var slot in _slots)
        {
            if (!slot.HeightIsActual)
            {
                slot.Height = EstimateSlotHeight(slot, pageWidthDip);
            }
            slot.YPosition = ypos;
            ypos += slot.Height;

            // Track estimated content width so the horizontal scroll extent is sensible
            // before anything is realized (mainly matters with word wrap off).
            if (slot.HasText && !WordWrap)
            {
                double left = Padding.Left + (slot.Paragraph!.LeftMargin * IN_TO_DIP);
                estMaxRight = Math.Max(estMaxRight, left + slot.TotalWidthEst);
            }
        }

        // Set document extents including padding
        // Use the maximum of content width (estimated or realized) or page width
        double minDocWidth = Padding.Left + pageWidthDip + Padding.Right;
        double contentRight = Math.Max(estMaxRight, _realizedMaxRight);
        documentWidth = Math.Max(contentRight + Padding.Right, minDocWidth);
        documentHeight = ypos + Padding.Bottom;
    }

    /// <summary>
    /// Computes a slot's estimated vertical advance for the current word-wrap mode.
    /// </summary>
    /// <param name="slot">The slot to estimate.</param>
    /// <param name="pageWidthDip">The page width in DIPs.</param>
    /// <returns>The estimated height including line spacing.</returns>
    private double EstimateSlotHeight(ParagraphSlot slot, double pageWidthDip)
    {
        if (!slot.HasText)
        {
            // Blank line or page break marker: one fixed-height line.
            return slot.LineHeightEst + LINE_SPACING;
        }

        int lineCount = 1;
        if (WordWrap)
        {
            const double IN_TO_DIP = 96.0;
            double availableWidth = pageWidthDip -
                ((slot.Paragraph!.LeftMargin + slot.Paragraph.RightMargin) * IN_TO_DIP);
            if (availableWidth > 0 && slot.TotalWidthEst > availableWidth)
            {
                lineCount = (int)Math.Ceiling(slot.TotalWidthEst / availableWidth);
            }
        }
        return lineCount * (slot.LineHeightEst + LINE_SPACING);
    }

    /// <summary>
    /// Posts a deferred measure invalidation so scroll extents update after height or
    /// width corrections discovered during rendering.
    /// </summary>
    private void ScheduleMeasureInvalidation()
    {
        if (_measureInvalidatePending)
        {
            return;
        }
        _measureInvalidatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _measureInvalidatePending = false;
            InvalidateMeasure();
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Ensures a slot's paragraph is shaped and laid out at its current Y position,
    /// returning the realized content. Returns null for slots with nothing to render
    /// (blank lines, page break markers). When the measured height differs from the
    /// estimate, the slot height is corrected (sticky) and layout is marked dirty so
    /// subsequent slot positions shift.
    /// </summary>
    /// <param name="slot">The slot to realize.</param>
    /// <returns>The realized paragraph, or null when the slot has no content.</returns>
    private PhysicalParagraph? RealizeSlot(ParagraphSlot slot)
    {
        slot.LastUsedFrame = _frameCounter;
        if (!slot.HasText)
        {
            return null;
        }

        if (slot.Realized != null)
        {
            // Cheaply re-anchor previously realized content if its Y position shifted.
            if (Math.Abs(slot.RealizedAtY - slot.YPosition) > 0.001)
            {
                ShiftRealizedSlot(slot);
            }
            return slot.Realized;
        }

        var logicalParagraph = slot.Paragraph!;
        var physical = new PhysicalParagraph
        {
            Justification = logicalParagraph.Justification,
            LeftMargin = logicalParagraph.LeftMargin,
            RightMargin = logicalParagraph.RightMargin
        };
        foreach (var fragment in logicalParagraph.Fragments)
        {
            if (fragment is not TextFragmentBase textFragment) continue;
            physical.Elements.Add(new PhysicalElement(this, textFragment));
        }

        LayoutSlotLines(slot, physical);

        slot.Realized = physical;
        slot.RealizedAtY = slot.YPosition;
        _realizedSlotCount++;

        double actualHeight = 0;
        foreach (var line in physical.Lines)
        {
            actualHeight += line.MaxHeight + LINE_SPACING;
        }
        if (actualHeight > 0 && Math.Abs(actualHeight - slot.Height) > 0.01)
        {
            slot.Height = actualHeight;
            _layoutDirty = true;
            ScheduleMeasureInvalidation();
        }
        slot.HeightIsActual = true;

        return slot.Realized;
    }

    /// <summary>
    /// Moves a realized slot's lines and elements to the slot's current Y position.
    /// </summary>
    /// <param name="slot">The realized slot to shift.</param>
    private static void ShiftRealizedSlot(ParagraphSlot slot)
    {
        double delta = slot.YPosition - slot.RealizedAtY;
        foreach (var line in slot.Realized!.Lines)
        {
            line.YPosition += delta;
            foreach (var element in line.Elements)
            {
                element.Position = new Point(element.Position.X, element.Position.Y + delta);
            }
        }
        slot.RealizedAtY = slot.YPosition;
    }

    /// <summary>
    /// Drops realized layouts that were not used in the current frame once the realized
    /// count exceeds the cap, bounding memory for very large documents. Measured slot
    /// heights are kept, so eviction never shifts the layout.
    /// </summary>
    private void EvictStaleRealizations()
    {
        if (_realizedSlotCount <= MAX_REALIZED_SLOTS)
        {
            return;
        }
        foreach (var slot in _slots)
        {
            if (slot.Realized != null && slot.LastUsedFrame != _frameCounter)
            {
                slot.Realized = null;
                _realizedSlotCount--;
            }
        }
    }

    /// <summary>
    /// Breaks a realized paragraph into lines and positions its elements starting at the
    /// slot's current Y position, honoring word wrap, trimming, and justification.
    /// </summary>
    /// <param name="slot">The slot being realized.</param>
    /// <param name="paragraph">The realized paragraph holding the shaped elements.</param>
    private void LayoutSlotLines(ParagraphSlot slot, PhysicalParagraph paragraph)
    {
        const float IN_TO_DIP = 96.0f;
        double pageWidthDip = PageWidth * IN_TO_DIP;
        double ypos = slot.YPosition;
        double maxRightExtent = 0;

        double leftMargin = paragraph.LeftMargin * IN_TO_DIP;
        double rightMargin = pageWidthDip - (paragraph.RightMargin * IN_TO_DIP);
        double availableWidth = rightMargin - leftMargin;

        foreach (var element in paragraph.Elements)
        {
            element.ResetLayoutWidth();
        }

        if (WordWrap && availableWidth > 0)
        {
            // Word wrapping enabled - break into lines
            var currentLine = new PhysicalParagraph
            {
                LeftMargin = paragraph.LeftMargin,
                RightMargin = paragraph.RightMargin,
                Justification = paragraph.Justification
            };
            double currentLineWidth = 0;
            
            for (int i = 0; i < paragraph.Elements.Count; i++)
            {
                var element = paragraph.Elements[i];
                double elementWidth = element.LayoutWidth;
                
                // Check if element fits on current line
                if (currentLine.Elements.Count > 0 && 
                    currentLineWidth + elementWidth > availableWidth)
                {
                    // Only break at a BreakableSpace boundary
                    // Look back to find the last BreakableSpace in the current line
                    int breakPoint = -1;
                    for (int j = currentLine.Elements.Count - 1; j >= 0; j--)
                    {
                        if (currentLine.Elements[j].IsBreakableSpace)
                        {
                            breakPoint = j;
                            break;
                        }
                    }
                    
                    if (breakPoint >= 0)
                    {
                        // Break after the last BreakableSpace
                        // Move elements after breakPoint to new line
                        int moveStart = breakPoint + 1;
                        int moveCount = currentLine.Elements.Count - moveStart;
                        var elementsToMove = currentLine.Elements.GetRange(moveStart, moveCount);
                        currentLine.Elements.RemoveRange(moveStart, moveCount);
                        
                        // Recalculate line metrics after removing elements
                        currentLine.MaxAscender = 0;
                        currentLine.MaxHeight = 0;
                        foreach (var elem in currentLine.Elements)
                        {
                            currentLine.MaxAscender = Math.Max(currentLine.MaxAscender, elem.Ascent);
                            currentLine.MaxHeight = Math.Max(currentLine.MaxHeight, elem.Ascent + elem.Descent);
                        }
                        
                        // Finalize current line
                        paragraph.Lines.Add(currentLine);
                        
                        // Start new line with moved elements
                        currentLine = new PhysicalParagraph
                        {
                            LeftMargin = paragraph.LeftMargin,
                            RightMargin = paragraph.RightMargin,
                            Justification = paragraph.Justification
                        };
                        currentLineWidth = 0;
                        
                        foreach (var elem in elementsToMove)
                        {
                            currentLine.Elements.Add(elem);
                            currentLine.MaxAscender = Math.Max(currentLine.MaxAscender, elem.Ascent);
                            currentLine.MaxHeight = Math.Max(currentLine.MaxHeight, elem.Ascent + elem.Descent);
                            currentLineWidth += elem.LayoutWidth;
                        }
                    }
                    else
                    {
                        // No BreakableSpace found - allow line to overflow
                        // (This handles the case where a single word is too long)
                    }
                }
                
                // Add element to current line (elements are shared with the
                // paragraph; per-layout state is reset at the top of the pass)
                currentLine.Elements.Add(element);
                currentLine.MaxAscender = Math.Max(currentLine.MaxAscender, element.Ascent);
                currentLine.MaxHeight = Math.Max(currentLine.MaxHeight, element.Ascent + element.Descent);
                currentLineWidth += elementWidth;
            }
            
            // Add final line if it has elements
            if (currentLine.Elements.Count > 0)
            {
                paragraph.Lines.Add(currentLine);
            }
        }
        else
        {
            // No word wrapping - all elements on one line
            var line = new PhysicalParagraph
            {
                LeftMargin = paragraph.LeftMargin,
                RightMargin = paragraph.RightMargin,
                Justification = paragraph.Justification
            };
            
            foreach (var element in paragraph.Elements)
            {
                line.Elements.Add(element);
                line.MaxAscender = Math.Max(line.MaxAscender, element.Ascent);
                line.MaxHeight = Math.Max(line.MaxHeight, element.Ascent + element.Descent);
            }
            
            if (line.Elements.Count > 0)
            {
                paragraph.Lines.Add(line);
            }
        }
        
        // Trim whitespace from lines based on justification mode
        foreach (var line in paragraph.Lines)
        {
            bool shouldTrimLeading = false;
            bool shouldTrimTrailing = false;
            bool trimAllWhitespace = false;
            
            switch (line.Justification)
            {
                case FancyTextDocument.Justification.Right:
                case FancyTextDocument.Justification.Full:
                    // Trim trailing breakable spaces only
                    shouldTrimTrailing = true;
                    trimAllWhitespace = false;
                    break;
                    
                case FancyTextDocument.Justification.Center:
                    // Trim all leading and trailing whitespace
                    shouldTrimLeading = true;
                    shouldTrimTrailing = true;
                    trimAllWhitespace = true;
                    break;
            }
            
            if (shouldTrimLeading)
            {
                // Remove leading whitespace elements
                while (line.Elements.Count > 0)
                {
                    var firstElement = line.Elements[0];
                    bool isWhitespaceToTrim = trimAllWhitespace 
                        ? firstElement.IsWhiteSpace 
                        : firstElement.IsBreakableSpace;
                        
                    if (!isWhitespaceToTrim)
                        break;
                        
                    line.Elements.RemoveAt(0);
                }
            }
            
            if (shouldTrimTrailing)
            {
                // Remove trailing whitespace elements
                while (line.Elements.Count > 0)
                {
                    var lastElement = line.Elements[^1];
                    bool isWhitespaceToTrim = trimAllWhitespace 
                        ? lastElement.IsWhiteSpace 
                        : lastElement.IsBreakableSpace;
                        
                    if (!isWhitespaceToTrim)
                        break;
                        
                    line.Elements.RemoveAt(line.Elements.Count - 1);
                }
            }
        }
        
        // Now set positions for all elements in all lines based on justification
        for (int lineIndex = 0; lineIndex < paragraph.Lines.Count; lineIndex++)
        {
            var line = paragraph.Lines[lineIndex];
            bool isLastLine = (lineIndex == paragraph.Lines.Count - 1);
            double xpos;
            
            // All elements in the line should be positioned (leading/trailing already trimmed)
            int startIndex = 0;
            int elementsToPosition = line.Elements.Count;
            
            // Calculate width of elements we're positioning
            double lineWidth = 0;
            for (int i = startIndex; i < startIndex + elementsToPosition; i++)
            {
                lineWidth += line.Elements[i].LayoutWidth;
            }
            
            // For full justification, expand the width of breakable space elements
            if (line.Justification == FancyTextDocument.Justification.Full && !isLastLine)
            {
                // Count breakable spaces in the range we're positioning
                int breakableSpaceCount = 0;
                for (int i = startIndex; i < startIndex + elementsToPosition; i++)
                {
                    if (line.Elements[i].IsBreakableSpace)
                    {
                        breakableSpaceCount++;
                    }
                }
                
                // Expand each breakable space to fill the line
                if (breakableSpaceCount > 0)
                {
                    double extraSpace = availableWidth - lineWidth;
                    double extraSpacePerGap = extraSpace / breakableSpaceCount;
                    
                    // Modify the width of each breakable space element
                    for (int i = startIndex; i < startIndex + elementsToPosition; i++)
                    {
                        var element = line.Elements[i];
                        if (element.IsBreakableSpace)
                        {
                            double newWidth = element.LayoutWidth + extraSpacePerGap;
                            element.SetWidth(newWidth);
                        }
                    }
                }
            }
            
            // Determine starting X position based on justification (add left padding)
            switch (line.Justification)
            {
                case FancyTextDocument.Justification.Left:
                default:
                    xpos = Padding.Left + leftMargin;
                    break;
                    
                case FancyTextDocument.Justification.Right:
                    xpos = Padding.Left + rightMargin - lineWidth;
                    break;
                    
                case FancyTextDocument.Justification.Center:
                    xpos = Padding.Left + leftMargin + (availableWidth - lineWidth) / 2.0;
                    break;
                    
                case FancyTextDocument.Justification.Full:
                    // Full justification starts at left margin
                    // Last line is treated as left-justified
                    xpos = Padding.Left + leftMargin;
                    break;
            }
            
            // Store line's Y position and initialize min/max X tracking
            line.YPosition = ypos;
            line.MinX = double.MaxValue;
            line.MaxX = double.MinValue;
            
            // Position all elements (leading whitespace already handled for center in startIndex)
            for (int i = startIndex; i < startIndex + elementsToPosition; i++)
            {
                var element = line.Elements[i];
                // Baseline alignment: adjust Y position based on max ascender
                element.Position = new Point(xpos, ypos + (line.MaxAscender - element.Ascent));
                
                // Track min/max X for this line
                line.MinX = Math.Min(line.MinX, xpos);
                line.MaxX = Math.Max(line.MaxX, xpos + element.LayoutWidth);
                
                // Track maximum right extent for document
                maxRightExtent = Math.Max(maxRightExtent, xpos + element.LayoutWidth);
                
                // Use the element's actual width (which may have been expanded for full justification)
                xpos += element.LayoutWidth;
            }

            ypos += line.MaxHeight + LINE_SPACING;
        }

        // Track the widest realized content for horizontal extent corrections.
        if (maxRightExtent > _realizedMaxRight)
        {
            _realizedMaxRight = maxRightExtent;
            if (maxRightExtent + Padding.Right > documentWidth)
            {
                _layoutDirty = true;
                ScheduleMeasureInvalidation();
            }
        }
    }

    public enum RenderStages
    {
        /// <summary>
        /// Draws background fills.
        /// </summary>
        RenderBg = 0,
        /// <summary>
        /// Draws selection highlights.
        /// </summary>
        RenderSelection,
        /// <summary>
        /// Draws shadow effects.
        /// </summary>
        RenderShadow,
        /// <summary>
        /// Draws the foreground glyphs.
        /// </summary>
        RenderFg
    }

    /// <summary>
    /// Draws the document background, guides, selection, and glyph content.
    /// </summary>
    /// <param name="context">The drawing context to render into.</param>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureLayout();

        using (context.PushTransform(Matrix.CreateScale(new Vector(Scale, Scale))))
        {
            // Calculate the visible viewport in unscaled document coordinates. When hosted
            // in a ScrollViewer only the on-screen region (plus overscan) is rendered;
            // scrolling invalidates the visual via the ScrollChanged subscription.
            var inverseScale = 1.0 / Scale;
            Rect viewportRect;
            if (_scrollHost != null)
            {
                viewportRect = new Rect(_scrollHost.Offset.X, _scrollHost.Offset.Y,
                    _scrollHost.Viewport.Width, _scrollHost.Viewport.Height);
            }
            else
            {
                viewportRect = new Rect(Bounds.Size);
            }
            viewportRect = viewportRect.Inflate(VIEWPORT_OVERSCAN);
            var visibleRect = new Rect(
                viewportRect.X * inverseScale,
                viewportRect.Y * inverseScale,
                viewportRect.Width * inverseScale,
                viewportRect.Height * inverseScale);

            // Draw background rectangle for the visible portion of the document area
            var bgRect = new Rect(0, 0, documentWidth, documentHeight).Intersect(visibleRect);
            if (bgRect.Width > 0 && bgRect.Height > 0)
            {
                context.DrawRectangle(_viewerBackgroundBrush, null, bgRect);
            }

            // Draw ruler at the top if enabled and in view
            if (ShowRuler && Padding.Top + RULER_HEIGHT >= visibleRect.Top)
            {
                DrawRuler(context);
            }

            if (logicalDocument == null) return;

            _frameCounter++;

            // Realize (shape + lay out) just the slots intersecting the viewport. Height
            // corrections shift later slots, so re-resolve until stable; this converges
            // immediately when the estimates are exact (e.g. word wrap off).
            var (firstSlot, endSlot) = GetVisibleSlotRange(visibleRect);
            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = firstSlot; i < endSlot; i++)
                {
                    RealizeSlot(_slots[i]);
                }
                if (!_layoutDirty)
                {
                    break;
                }
                LayoutPhysicalDocument();
                _layoutDirty = false;
                (firstSlot, endSlot) = GetVisibleSlotRange(visibleRect);
            }

            var visibleElements =
                GetVisibleElements(firstSlot, endSlot, visibleRect, out bool hasVisibleShadows);
            var selection = GetSelectionRange();

            foreach (var physElem in visibleElements)
            {
                physElem.RenderBackground(context);
            }

            if (selection.HasSelection)
            {
                foreach (var physElem in visibleElements)
                {
                    physElem.RenderSelection(context, selection);
                }
            }

            if (_documentHasShadowElements && hasVisibleShadows)
            {
                using var _ = context.PushOpacity(SHADOW_PASS_OPACITY);
                foreach (var physElem in visibleElements)
                {
                    if (physElem.IsShadow)
                    {
                        physElem.RenderShadows(context);
                    }
                }
            }

            foreach (var physElem in visibleElements)
            {
                physElem.RenderForeground(context);
            }

            // Draw line margins (dotted lines for each line) and page breaks (blue horizontal lines)
            const float IN_TO_DIP = 96.0f;
            double pageWidthDip = PageWidth * IN_TO_DIP;

            if (ShowPageBoundaries)
            {
                // Page break markers - draw horizontal blue dotted lines
                for (int i = firstSlot; i < endSlot; i++)
                {
                    var slot = _slots[i];
                    if (!slot.IsPageBreak)
                    {
                        continue;
                    }
                    double centerY = slot.YPosition + (PAGE_BREAK_PIXEL_HEIGHT / 2.0);
                    var lineStart = new Point(Padding.Left, centerY);
                    var lineEnd = new Point(Padding.Left + pageWidthDip, centerY);
                    context.DrawLine(PageGuidePen, lineStart, lineEnd);
                }
            }

            if (ShowMarginBoundaries)
            {
                for (int i = firstSlot; i < endSlot; i++)
                {
                    var realized = _slots[i].Realized;
                    if (realized == null)
                    {
                        continue;
                    }
                    foreach (var line in realized.Lines)
                    {
                        if (line.Elements.Count == 0)
                        {
                            continue;
                        }
                        double leftMargin = Padding.Left + (line.LeftMargin * IN_TO_DIP);
                        double rightMargin =
                            Padding.Left + pageWidthDip - (line.RightMargin * IN_TO_DIP);

                        // Draw left margin line
                        var leftTop = new Point(leftMargin, line.YPosition);
                        var leftBottom = new Point(leftMargin, line.YPosition + line.MaxHeight);
                        context.DrawLine(MarginGuidePen, leftTop, leftBottom);

                        // Draw right margin line
                        var rightTop = new Point(rightMargin, line.YPosition);
                        var rightBottom = new Point(rightMargin, line.YPosition + line.MaxHeight);
                        context.DrawLine(MarginGuidePen, rightTop, rightBottom);
                    }
                }
            }

            // Draw Right Margin (max right margin for document)
            if (ShowPageBoundaries)
            {
                var maxRightMargin = Padding.Left + pageWidthDip;
                var topPoint = new Point(maxRightMargin, Padding.Top);
                var botPoint = new Point(maxRightMargin, documentHeight);
                context.DrawLine(PageGuidePen, topPoint, botPoint);
            }

            // Bound memory: drop shaped content that was not used this frame.
            EvictStaleRealizations();
        }
    }

    /// <summary>
    /// Draws the inch ruler shown above the document content.
    /// </summary>
    /// <param name="context">The drawing context to render into.</param>
    private void DrawRuler(DrawingContext context)
    {
        const double IN_TO_DIP = 96.0;
        double pageWidthDip = PageWidth * IN_TO_DIP;

        EnsureRulerBitmap(pageWidthDip);
        if (_rulerBitmap == null)
        {
            return;
        }

        // Blit the cached ruler bitmap into place. The destination rect is in unscaled
        // document coordinates; the outer scale transform (and screen DPI) map it back to
        // the pixel resolution the bitmap was rendered at, keeping it crisp.
        var destRect = new Rect(Padding.Left, Padding.Top, pageWidthDip, RULER_HEIGHT);
        context.DrawImage(_rulerBitmap, destRect);
    }

    /// <summary>
    /// Rebuilds the cached ruler bitmap when the page width, zoom, or screen render scaling
    /// changes; otherwise leaves the existing bitmap in place.
    /// </summary>
    /// <param name="pageWidthDip">The page width in device-independent pixels.</param>
    private void EnsureRulerBitmap(double pageWidthDip)
    {
        double renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        if (_rulerBitmap != null &&
            Math.Abs(_rulerBitmapPageWidth - PageWidth) < double.Epsilon &&
            Math.Abs(_rulerBitmapScale - Scale) < double.Epsilon &&
            Math.Abs(_rulerBitmapRenderScaling - renderScaling) < double.Epsilon)
        {
            return;
        }

        // Render at zoom * screen-scaling so the blitted result stays sharp at any zoom.
        double effectiveScale = Scale * renderScaling;
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(pageWidthDip * effectiveScale));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(RULER_HEIGHT * effectiveScale));

        var bitmap = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight),
            new Vector(96, 96));

        using (var ctx = bitmap.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateScale(effectiveScale, effectiveScale)))
        {
            RenderRulerContent(ctx, pageWidthDip);
        }

        _rulerBitmap?.Dispose();
        _rulerBitmap = bitmap;
        _rulerBitmapPageWidth = PageWidth;
        _rulerBitmapScale = Scale;
        _rulerBitmapRenderScaling = renderScaling;
    }

    /// <summary>
    /// Draws the ruler background, tick marks, and inch labels at the bitmap origin (0,0),
    /// in unscaled coordinates.
    /// </summary>
    /// <param name="context">The bitmap drawing context to render into.</param>
    /// <param name="pageWidthDip">The page width in device-independent pixels.</param>
    private void RenderRulerContent(DrawingContext context, double pageWidthDip)
    {
        const double IN_TO_DIP = 96.0;

        // Draw ruler background
        context.DrawRectangle(Brushes.LightYellow, RulerBorderPen,
            new Rect(0, 0, pageWidthDip, RULER_HEIGHT));

        // Draw tick marks
        for (double inch = 0.125; inch < PageWidth; inch += 0.125) // Every eighth of an inch
        {
            double xPos = inch * IN_TO_DIP;
            double tickHeight;
            IPen tickPen;

            // Determine tick height based on fraction
            if (NearlyEqual(inch, Math.Floor(inch))) // Whole inch
            {
                tickHeight = RULER_HEIGHT * 0.3;
                tickPen = RulerTickPens[0];
            }
            else if (NearlyEqual((inch * 2), Math.Floor(inch * 2)))  // Half inch
            {
                tickHeight = RULER_HEIGHT * 0.4;
                tickPen = RulerTickPens[1];
            }
            else if (NearlyEqual((inch * 4), Math.Floor(inch * 4))) // Quarter inch
            {
                tickHeight = RULER_HEIGHT * 0.3;
                tickPen = RulerTickPens[2];
            }
            else // Eighth inch
            {
                tickHeight = RULER_HEIGHT * 0.2;
                tickPen = RulerTickPens[3];
            }

            var tickTop = new Point(xPos, RULER_HEIGHT - tickHeight);
            var tickBottom = new Point(xPos, RULER_HEIGHT);
            context.DrawLine(tickPen, tickTop, tickBottom);

            // Draw inch numbers on whole inches
            if (NearlyEqual(inch, Math.Floor(inch)) && inch > 0)
            {
                var label = new FormattedText(
                    inch.ToString("0", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Arial"),
                    8,
                    Brushes.Black);
                context.DrawText(label, new Point(xPos - (label.Width / 2), 1));
            }
        }

        context.DrawRectangle(null, RulerBorderPen,
            new Rect(0, 0, pageWidthDip, RULER_HEIGHT));

        return;

        bool NearlyEqual(double a, double b, double epsilon = 1e-9)
        {
            return Math.Abs(a - b) <= epsilon;
        }
    }

    private class PhysicalElement
    {
        private readonly FancyTextViewer _viewer;
        private TextFragmentBase LogicalItem { get; }
        private readonly IBrush _foregroundBrush;
        private readonly IBrush _backgroundBrush;
        // Natural (unexpanded) extents; LayoutWidth/Bounds return to these each layout pass.
        private readonly double _naturalWidth;
        private readonly Rect _naturalBounds;
        // Lazily created render resources, valid for the element's lifetime.
        private IPen? _outlinePen;
        private IPen? _underlinePen;
        private Geometry? _outlineGeometry;
        private bool _outlineGeometryBuilt;
        public FormattedText FormattedText { get; }
        public double LayoutWidth { get; private set; }
        public double Ascent { get; }
        public double Descent { get; }
        public double Baseline { get; }
        public Vector GlyphOffset { get; }
        public Rect Bounds { get; private set; }
        public bool IsWhiteSpace => LogicalItem.GetLogicalType() != ILogicalItem.Type.Text;
        public bool IsBreakableSpace => LogicalItem.GetLogicalType() == ILogicalItem.Type.BreakableSpace;
        public Typeface Typeface => LogicalItem.Style.Typeface;
        public double FontSize { get; }
        public Color Foreground => LogicalItem.Style.Foreground;
        public Color Background => LogicalItem.Style.Background;
        public string Text => LogicalItem.Text;
        public int StartOffset => LogicalItem.StartOffset;
        public int EndOffset => LogicalItem.EndOffset;
        public bool IsUnderlined => LogicalItem.Style.IsUnderline;
        public bool IsShadow => LogicalItem.Style.IsShadow;
        public bool IsOutline => LogicalItem.Style.IsOutline;

        // Position where this element should be rendered
        public Point Position { get; set; }


        /// <summary>
        /// Creates a physical renderable element from a logical text fragment.
        /// </summary>
        /// <param name="viewer">The owning viewer instance.</param>
        /// <param name="logicalItem">The logical fragment to render.</param>
        public PhysicalElement(FancyTextViewer viewer, TextFragmentBase logicalItem)
        {
            _viewer = viewer;
            LogicalItem = logicalItem;
            Position = new Point(0, 0);
            _foregroundBrush = GetCachedBrush(logicalItem.Style.Foreground);
            _backgroundBrush = GetCachedBrush(logicalItem.Style.Background);

            var text = logicalItem.Text;
            bool isSuperscript = logicalItem.Style.IsSuperscript;
            bool isSubscript = logicalItem.Style.IsSubscript;

            FontSize = (isSuperscript || isSubscript)
                ? logicalItem.Style.FontSize * SUB_SUPER_SCALE
                : logicalItem.Style.FontSize;
            FormattedText = new FormattedText(text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                logicalItem.Style.Typeface,
                FontSize,
                _foregroundBrush);
            if (IsUnderlined && !IsWhiteSpace)
            {
                FormattedText.SetTextDecorations([
                    new TextDecoration
                    {
                        Location = TextDecorationLocation.Underline,
                    }
                ]);
            }

            // Calculate bounds and logical advance width at origin (0,0) - will be positioned
            // later. The advance width (including trailing whitespace) matches what the
            // highlight geometry box used to provide, without building a Geometry.
            var glyphRect = GetGlyphStringRect(FormattedText);
            double advanceWidth = FormattedText.WidthIncludingTrailingWhitespace;
            var rect = glyphRect.Union(new Rect(0, 0, advanceWidth, FormattedText.Height));
            LayoutWidth = Math.Max(advanceWidth, FormattedText.Width);
            var metrics = GetMyFontMetrics(logicalItem.Style.Typeface, FontSize);
            var ftAscent = FormattedText.Baseline;
            var ftDescent = FormattedText.Height - FormattedText.Baseline;
            var renderAscent = Math.Max(ftAscent, metrics.MaxAscent);
            var renderDescent = Math.Max(ftDescent, metrics.MaxDescent);

            if (isSuperscript || isSubscript)
            {
                var fullSizeMetrics = GetMyFontMetrics(
                    logicalItem.Style.Typeface, logicalItem.Style.FontSize);
                var fullAscent = fullSizeMetrics.MaxAscent;
                var baselineShift = Math.Max(0, fullAscent - renderAscent);

                Ascent = fullAscent;

                if (isSuperscript)
                {
                    Baseline = renderAscent;
                    Descent = renderDescent;
                }
                else
                {
                    Baseline = fullAscent + baselineShift;
                    Descent = baselineShift + renderDescent;
                    GlyphOffset = new Vector(0, Baseline - renderAscent);
                }
            }
            else
            {
                Baseline = renderAscent;
                Ascent = renderAscent;
                Descent = renderDescent;
            }

            // Normalize the rect to use consistent height (Ascent + Descent)
            // while preserving the natural left/width from the glyph rect
            rect = new Rect(rect.Left, 0, rect.Width, Ascent + Descent);

            Bounds = rect;
            _naturalBounds = rect;
            _naturalWidth = LayoutWidth;
        }

        /// <summary>
        /// Sets a custom width for this element (used for expanding whitespace in full justification)
        /// </summary>
        public void SetWidth(double newWidth)
        {
            LayoutWidth = newWidth;
            Bounds = new Rect(Bounds.Left, Bounds.Top, newWidth, Bounds.Height);
        }

        /// <summary>
        /// Restores the element's natural width and bounds before a fresh layout pass.
        /// </summary>
        public void ResetLayoutWidth()
        {
            LayoutWidth = _naturalWidth;
            Bounds = _naturalBounds;
        }


        /// <summary>
        /// Draws the element background and optional diagnostics.
        /// </summary>
        /// <param name="context">The drawing context to render into.</param>
        /// <param name="drawBaseline"><c>true</c> to draw the baseline guide.</param>
        /// <param name="drawBounds"><c>true</c> to draw the element bounds.</param>
        public void RenderBackground(DrawingContext context, bool drawBaseline = false, bool drawBounds = false)
        {
            // Skip fills that would be invisible against the viewer background
            var background = LogicalItem.Style.Background;
            if (background.A != 0 && background != _viewer._background)
            {
                context.FillRectangle(_backgroundBrush, new Rect(Position, Bounds.Size));
            }
            // Draw bounding box
            if (drawBounds)
            {
                context.DrawRectangle(new Pen(Brushes.Blue, .25), new Rect(Position, Bounds.Size));
            }

            //Draw baseline
            if (drawBaseline)
            {
                context.DrawLine(new Pen(Brushes.Red, .25),
                    new Point(Position.X, Baseline + Position.Y),
                    new Point(Position.X + LayoutWidth, Baseline + Position.Y));
            }
        }

        /// <summary>
        /// Draws the highlighted portion of this element that overlaps the current selection.
        /// </summary>
        /// <param name="context">The drawing context to render into.</param>
        /// <param name="selection">The current selection range.</param>
        public void RenderSelection(DrawingContext context, SelectionRange selection)
        {
            if (!selection.HasSelection)
            {
                return;
            }

            int start = Math.Max(selection.NormalizedStart, StartOffset);
            int end = Math.Min(selection.NormalizedEnd, EndOffset);
            if (end <= start)
            {
                return;
            }

            int localStart = start - StartOffset;
            int localLength = end - start;
            var renderOrigin = Position + GlyphOffset;
            var selectionGeometry = FormattedText.BuildHighlightGeometry(renderOrigin, localStart, localLength);
            if (selectionGeometry != null)
            {
                context.DrawGeometry(SelectionBrush, null, selectionGeometry);
            }
        }


        /// <summary>
        /// Draws the element shadow effect when enabled.
        /// </summary>
        /// <param name="context">The drawing context to render into.</param>
        public void RenderShadows(DrawingContext context)
        {
            if (!IsShadow) return;
            var offset = Math.Max(FontSize / 12.0, 2);
            var renderOrigin = Position + GlyphOffset;
            context.DrawText(FormattedText, renderOrigin + new Vector(offset, offset));
            if (IsUnderlined && IsWhiteSpace)
            {
                DrawWhitespaceUnderline(context, offset, offset);
            }
        }

        /// <summary>
        /// Draws the glyph foreground, including optional outline rendering.
        /// </summary>
        /// <param name="context">The drawing context to render into.</param>
        public void RenderForeground(DrawingContext context)
        {
            var renderOrigin = Position + GlyphOffset;

            if (IsOutline /*&& !IsWhiteSpace*/)
            {
                // The glyph geometry and pen never change; build them once and translate
                // to the render origin at draw time.
                if (!_outlineGeometryBuilt)
                {
                    _outlineGeometry = FormattedText.BuildGeometry(new Point(0, 0));
                    _outlineGeometryBuilt = true;
                }
                if (_outlineGeometry != null)
                {
                    if (_outlinePen == null)
                    {
                        var met = GetGlyphMetricsInfo(Typeface);
                        var scale = FontSize / met.DesignEmHeight;
                        _outlinePen = new Pen(_foregroundBrush, met.UnderlineThickness * scale)
                        {
                            LineCap = PenLineCap.Square
                        };
                    }
                    using (context.PushTransform(
                               Matrix.CreateTranslation(renderOrigin.X, renderOrigin.Y)))
                    {
                        // Outline pass: stroke (and fill) with the foreground color, then
                        // fill the glyph interior with the background color.
                        context.DrawGeometry(_foregroundBrush, _outlinePen, _outlineGeometry);
                        context.DrawGeometry(GetCachedBrush(Background), null, _outlineGeometry);
                    }
                }
            }
            else
            {
                context.DrawText(FormattedText, renderOrigin);
            }

            if (IsUnderlined && IsWhiteSpace)
            {
                DrawWhitespaceUnderline(context);
            }
        }

        /// <summary>
        /// Draws an explicit underline segment for whitespace runs.
        /// </summary>
        /// <param name="context">The drawing context to render into.</param>
        /// <param name="xOffset">Horizontal offset (used by the shadow pass).</param>
        /// <param name="yOffset">Vertical offset (used by the shadow pass).</param>
        private void DrawWhitespaceUnderline(
                DrawingContext context,
                double xOffset = 0,
                double yOffset = 0)
        {
            if (LayoutWidth <= 0)
            {
                return;
            }

            var met = GetGlyphMetricsInfo(Typeface);
            var scale = FontSize / met.DesignEmHeight;
            _underlinePen ??= new Pen(_foregroundBrush,
                Math.Max(1.0, met.UnderlineThickness * scale))
            {
                LineCap = PenLineCap.Flat
            };

            double underlineY = Position.Y + yOffset + Baseline +
                Math.Abs(met.UnderlinePosition * scale);
            var start = new Point(Position.X + xOffset, underlineY);
            var end = new Point(Position.X + xOffset + LayoutWidth, underlineY);
            context.DrawLine(_underlinePen, start, end);
        }
    }


    private readonly record struct FontKey(Typeface Typeface, double Size);

    private static readonly Dictionary<FontKey, MyFontMetrics> FontMetricsCache = new();

    /// <summary>
    /// Represents cached ascent and descent metrics for a typeface at a specific size.
    /// </summary>
    /// <param name="MaxAscent">The maximum measured ascent.</param>
    /// <param name="MaxDescent">The maximum measured descent.</param>
    /// <param name="AvgCharWidth">The average character advance width, used for
    /// estimating unshaped fragment widths (exact for monospace fonts).</param>
    public record MyFontMetrics(double MaxAscent, double MaxDescent, double AvgCharWidth);

    private static readonly string typicalString =
        " ,./<>?;':[]{}|=-+_)(*&^%$#@!`~abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>
    /// Returns cached ascent and descent metrics for the supplied font.
    /// </summary>
    /// <param name="tf">The typeface to measure.</param>
    /// <param name="size">The font size to measure.</param>
    /// <returns>The measured ascent and descent values.</returns>
    internal static MyFontMetrics GetMyFontMetrics(Typeface tf, double size)
    {
        var key = new FontKey(tf, size);
        if (FontMetricsCache.TryGetValue(key, out var metrics)) return metrics;

        var ft = new FormattedText(typicalString, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            tf, size, Brushes.Black);

        var ascent = ft.Baseline;
        var descent = ft.Height - ft.Baseline;
        var avgCharWidth = ft.WidthIncludingTrailingWhitespace / typicalString.Length;
        metrics = new MyFontMetrics(ascent, descent, avgCharWidth);
        FontMetricsCache[key] = metrics;

        return metrics;
    }


    /// <summary>
    /// Computes the unpositioned glyph bounds for formatted text, including overhangs.
    /// </summary>
    /// <param name="ft">The formatted text to measure.</param>
    /// <returns>The glyph bounds at the origin.</returns>
    private static Rect GetGlyphStringRect(FormattedText ft)
    {
        // Alignment rectangle is [0,0, Width, Height]
        // Overhangs extend beyond that box.
        double x = -ft.OverhangLeading;
        double y = 0; // top of the first line

        double width = ft.Width + ft.OverhangLeading + ft.OverhangTrailing;
        double height = ft.Height + ft.OverhangAfter;

        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Clears any active text selection state.
    /// </summary>
    private void ClearSelection()
    {
        _selectionAnchorOffset = null;
        _selectionCaretOffset = null;
        _isSelecting = false;
    }

    /// <summary>
    /// Returns the normalized selection range for the current anchor and caret state.
    /// </summary>
    /// <returns>The current selection range.</returns>
    private SelectionRange GetSelectionRange()
    {
        if (_selectionAnchorOffset == null || _selectionCaretOffset == null)
        {
            return new SelectionRange(0, 0);
        }

        return new SelectionRange(_selectionAnchorOffset.Value, _selectionCaretOffset.Value);
    }

    /// <summary>
    /// Selects the entire underlying document text.
    /// </summary>
    private void SelectAll()
    {
        if (Document == null || Document.Text.Length == 0)
        {
            return;
        }

        _selectionAnchorOffset = 0;
        _selectionCaretOffset = Document.Text.Length;
        UpdateContextMenuState();
        InvalidateVisual();
    }

    /// <summary>
    /// Starts an asynchronous clipboard copy for the current selection.
    /// </summary>
    private void CopySelectionToClipboard()
    {
        _ = CopySelectionToClipboardAsync();
    }

    /// <summary>
    /// Copies the current plain-text selection to the system clipboard.
    /// </summary>
    /// <returns>A task that completes when the clipboard operation finishes.</returns>
    private async Task CopySelectionToClipboardAsync()
    {
        try
        {
            var selection = GetSelectionRange();
            if (!selection.HasSelection || Document == null)
            {
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
            {
                return;
            }

            var text = Document.Text.ToString(
                selection.NormalizedStart,
                selection.NormalizedEnd - selection.NormalizedStart);
            await topLevel.Clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            AppLog.W("Failed to copy text selection to clipboard", ex);
        }
    }

    /// <summary>
    /// Maps a control-space pointer position to the nearest document text offset.
    /// </summary>
    /// <param name="controlPoint">The pointer position in control coordinates.</param>
    /// <returns>The nearest document offset, or <c>null</c> when no document is loaded.</returns>
    private int? HitTestDocumentOffset(Point controlPoint)
    {
        if (Document == null)
        {
            return null;
        }

        EnsureLayout();
        var point = controlPoint / Scale;

        var slot = FindNearestTextSlot(point.Y);
        if (slot == null)
        {
            return Document.Text.Length;
        }

        var realized = RealizeSlot(slot);
        if (_layoutDirty)
        {
            // A height correction moved later slots; this slot's own position and
            // realized content are unaffected, so just settle positions for next time.
            LayoutPhysicalDocument();
            _layoutDirty = false;
            ScheduleMeasureInvalidation();
        }
        if (realized == null || realized.Lines.Count == 0)
        {
            return Document.Text.Length;
        }

        // Find the line containing (or vertically nearest to) the point.
        PhysicalParagraph? nearestLine = null;
        double nearestLineDistance = double.MaxValue;
        foreach (var candidate in realized.Lines)
        {
            if (candidate.Elements.Count == 0)
            {
                continue;
            }
            double dy = point.Y < candidate.YPosition
                ? candidate.YPosition - point.Y
                : point.Y > candidate.YPosition + candidate.MaxHeight
                    ? point.Y - (candidate.YPosition + candidate.MaxHeight)
                    : 0;
            if (dy < nearestLineDistance)
            {
                nearestLineDistance = dy;
                nearestLine = candidate;
            }
        }
        if (nearestLine == null)
        {
            return Document.Text.Length;
        }

        // Within the nearest line, pick the element containing the point, or the
        // horizontally closest one.
        PhysicalElement? closestElement = null;
        double closestDistance = double.MaxValue;

        foreach (var element in nearestLine.Elements)
        {
            var bounds = new Rect(element.Position, element.Bounds.Size);
            if (point.X >= bounds.Left && point.X <= bounds.Right)
            {
                return GetOffsetForElementPoint(element, point);
            }

            double dx = point.X < bounds.Left ? bounds.Left - point.X : point.X - bounds.Right;
            if (dx < closestDistance)
            {
                closestDistance = dx;
                closestElement = element;
            }
        }

        return closestElement == null ?
            Document.Text.Length : GetOffsetForElementPoint(closestElement, point);
    }

    /// <summary>
    /// Finds the text-bearing slot containing, or vertically nearest to, the supplied Y
    /// position using a binary search over the Y-ordered slot list.
    /// </summary>
    /// <param name="y">The unscaled document Y coordinate.</param>
    /// <returns>The nearest text slot, or <c>null</c> when the document has none.</returns>
    private ParagraphSlot? FindNearestTextSlot(double y)
    {
        int count = _slots.Count;
        if (count == 0)
        {
            return null;
        }

        // Find the first slot whose bottom edge is at or below y (slot bottoms are
        // strictly increasing).
        int lo = 0;
        int hi = count - 1;
        int idx = count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_slots[mid].YPosition + _slots[mid].Height < y)
            {
                lo = mid + 1;
            }
            else
            {
                idx = mid;
                hi = mid - 1;
            }
        }
        if (idx >= count)
        {
            idx = count - 1;    // below the last slot
        }

        // The nearest slot may be a blank line or page break; expand outward to the
        // nearest slots that actually carry text and pick the vertically closer one.
        int up = idx;
        while (up >= 0 && !_slots[up].HasText)
        {
            up--;
        }
        int down = idx;
        while (down < count && !_slots[down].HasText)
        {
            down++;
        }

        if (up < 0 && down >= count)
        {
            return null;
        }
        if (up < 0)
        {
            return _slots[down];
        }
        if (down >= count)
        {
            return _slots[up];
        }
        return DistanceToSlot(_slots[up], y) <= DistanceToSlot(_slots[down], y)
            ? _slots[up] : _slots[down];
    }

    /// <summary>
    /// Returns the vertical distance from a Y coordinate to a slot's span (0 if inside).
    /// </summary>
    private static double DistanceToSlot(ParagraphSlot slot, double y)
    {
        if (y < slot.YPosition)
        {
            return slot.YPosition - y;
        }
        double bottom = slot.YPosition + slot.Height;
        return y > bottom ? y - bottom : 0;
    }

    /// <summary>
    /// Computes the index range [First, End) of slots that vertically intersect the
    /// supplied rect, using a binary search over the Y-ordered slot list.
    /// </summary>
    /// <param name="visibleRect">The visible rect in unscaled document coordinates.</param>
    /// <returns>The first index and exclusive end index of intersecting slots.</returns>
    private (int First, int End) GetVisibleSlotRange(Rect visibleRect)
    {
        int count = _slots.Count;
        if (count == 0)
        {
            return (0, 0);
        }

        int lo = 0;
        int hi = count - 1;
        int first = count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_slots[mid].YPosition + _slots[mid].Height < visibleRect.Top)
            {
                lo = mid + 1;
            }
            else
            {
                first = mid;
                hi = mid - 1;
            }
        }

        int end = first;
        while (end < count && _slots[end].YPosition <= visibleRect.Bottom)
        {
            end++;
        }

        return (first, end);
    }

    /// <summary>
    /// Computes bounds that cover the selected source-offset range.
    /// </summary>
    private bool TryGetSelectionBounds(int startOffset, int endOffset, out Rect bounds)
    {
        bounds = default;
        if (startOffset > endOffset)
        {
            (startOffset, endOffset) = (endOffset, startOffset);
        }

        EnsureLayout();

        // Binary search for the first slot that can overlap the range (slot offsets are
        // monotonic non-decreasing in document order).
        int count = _slots.Count;
        int lo = 0;
        int hi = count - 1;
        int firstIndex = count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_slots[mid].EndOffset <= startOffset)
            {
                lo = mid + 1;
            }
            else
            {
                firstIndex = mid;
                hi = mid - 1;
            }
        }

        bool found = false;
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        int realizeBudget = MAX_BOUNDS_REALIZE;

        for (int i = firstIndex; i < count && realizeBudget > 0; i++)
        {
            var slot = _slots[i];
            if (slot.StartOffset >= endOffset)
            {
                break;
            }
            if (!slot.HasText || slot.EndOffset <= startOffset)
            {
                continue;
            }

            var realized = RealizeSlot(slot);
            realizeBudget--;
            if (realized == null)
            {
                continue;
            }

            foreach (var line in realized.Lines)
            {
                foreach (var element in line.Elements)
                {
                    if (element.EndOffset <= startOffset || element.StartOffset >= endOffset)
                    {
                        continue;
                    }
                    Rect elementRect = new Rect(element.Position, element.Bounds.Size);
                    minX = Math.Min(minX, elementRect.X);
                    minY = Math.Min(minY, elementRect.Y);
                    maxX = Math.Max(maxX, elementRect.Right);
                    maxY = Math.Max(maxY, elementRect.Bottom);
                    found = true;
                }
            }
        }

        // Settle any height corrections discovered while realizing off-screen slots.
        if (_layoutDirty)
        {
            LayoutPhysicalDocument();
            _layoutDirty = false;
            ScheduleMeasureInvalidation();
        }

        if (!found)
        {
            return false;
        }

        bounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        return true;
    }

    /// <summary>
    /// Scrolls the nearest parent ScrollViewer so that the supplied rect is visible.
    /// </summary>
    private void ScrollRectIntoView(Rect rect)
    {
        ScrollViewer? scrollViewer =
            _scrollHost ?? this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer == null)
        {
            return;
        }

        Vector offset = scrollViewer.Offset;
        Size viewport = scrollViewer.Viewport;
        double x = offset.X;
        double y = offset.Y;

        if (rect.Left < x)
        {
            x = rect.Left;
        }
        else if (rect.Right > x + viewport.Width)
        {
            x = rect.Right - viewport.Width;
        }

        if (rect.Top < y)
        {
            y = rect.Top;
        }
        else if (rect.Bottom > y + viewport.Height)
        {
            y = rect.Bottom - viewport.Height;
        }

        scrollViewer.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
    }

    /// <summary>
    /// Approximates the text offset inside an element for a given point.
    /// </summary>
    /// <param name="element">The element containing the point.</param>
    /// <param name="point">The point to map.</param>
    /// <returns>The approximate text offset for the supplied point.</returns>
    private static int GetOffsetForElementPoint(PhysicalElement element, Point point)
    {
        if (element.Text.Length == 0)
        {
            return element.StartOffset;
        }

        var bounds = new Rect(element.Position, element.Bounds.Size);
        if (point.X <= bounds.Left)
        {
            return element.StartOffset;
        }

        if (point.X >= bounds.Right)
        {
            return element.EndOffset;
        }

        double width = Math.Max(bounds.Width, 1);
        double relativeX = Math.Clamp((point.X - bounds.Left) / width, 0, 1);
        int localOffset = (int)Math.Round(relativeX * element.Text.Length, MidpointRounding.AwayFromZero);
        localOffset = Math.Clamp(localOffset, 0, element.Text.Length);
        return element.StartOffset + localOffset;
    }

    /// <summary>
    /// Collects elements that intersect the visible viewport.
    /// </summary>
    private List<PhysicalElement> GetVisibleElements(
            int firstSlot, int endSlot, Rect visibleRect, out bool hasVisibleShadows)
    {
        _visibleElementsBuffer.Clear();
        hasVisibleShadows = false;

        for (int s = firstSlot; s < endSlot; s++)
        {
            var realized = _slots[s].Realized;
            if (realized == null)
            {
                continue;
            }

            foreach (var line in realized.Lines)
            {
                if (line.Elements.Count == 0 ||
                    line.YPosition + line.MaxHeight < visibleRect.Top ||
                    line.YPosition > visibleRect.Bottom ||
                    line.MaxX < visibleRect.Left ||
                    line.MinX > visibleRect.Right)
                {
                    continue;
                }

                foreach (var element in line.Elements)
                {
                    double left = element.Position.X;
                    double right = left + element.LayoutWidth;
                    if (right < visibleRect.Left || left > visibleRect.Right)
                    {
                        continue;
                    }

                    _visibleElementsBuffer.Add(element);
                    hasVisibleShadows |= element.IsShadow;
                }
            }
        }

        return _visibleElementsBuffer;
    }

    /// <summary>
    /// Synchronizes context menu check states and enabled commands with the viewer state.
    /// </summary>
    private void UpdateContextMenuState()
    {
        _showRulerMenuItem.IsChecked = ShowRuler;
        _wordWrapMenuItem.IsChecked = WordWrap;
        _showPageBoundariesMenuItem.IsChecked = ShowPageBoundaries;
        _showMarginsMenuItem.IsChecked = ShowMarginBoundaries;
        _selectAllMenuItem.IsEnabled = Document?.Text.Length > 0;
        _copyMenuItem.IsEnabled = GetSelectionRange().HasSelection;
    }
}

/// <summary>
/// Provides drawing helpers used by the FancyText viewer.
/// </summary>
public static class DrawingContextExtensions
{
    /// <summary>
    /// Draws formatted text by building glyph geometry and optionally stroking an outline.
    /// </summary>
    /// <param name="ctx">The drawing context to render into.</param>
    /// <param name="formattedText">The formatted text to draw.</param>
    /// <param name="origin">The origin at which to draw the text.</param>
    /// <param name="foreground">The fill brush for the text geometry.</param>
    /// <param name="outlinePen">An optional outline pen.</param>
    public static void DrawTextWithOutline(
        this DrawingContext ctx,
        FormattedText formattedText,
        Point origin,
        IBrush? foreground,
        Pen? outlinePen = null
    )
    {
        // Build vector geometry for the glyphs
        var geometry = formattedText.BuildGeometry(origin);
        if (geometry == null) return;

        // 1. Outline pass (optional)
        if (outlinePen != null)
        {
            ctx.DrawGeometry(outlinePen.Brush, outlinePen, geometry);
        }

        // 2. Foreground fill
        if (foreground != null)
        {
            ctx.DrawGeometry(foreground, null, geometry);
        }
    }
}
