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
    
    private double documentWidth;
    private double documentHeight;

    private FancyTextLogicalDocument? logicalDocument;
    private readonly PhysicalDocument physDoc = new();
    private readonly List<PhysicalElement> _visibleElementsBuffer = [];
    private bool _layoutDirty = true;
    private bool _documentHasShadowElements;
    private IBrush _viewerBackgroundBrush = new SolidColorBrush(Colors.White);
    private double lastElementHeight; // Track last element height for blank paragraphs
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
        new SolidColorBrush(Color.FromArgb(0x80, 0x33, 0x99, 0xff));
    private const double SHADOW_PASS_OPACITY = 0.33;

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
        _viewerBackgroundBrush = new SolidColorBrush(value);
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
        MarkLayoutDirty();
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
        MarkLayoutDirty();
    }

    /// <summary>
    /// Re-parses the current document and rebuilds physical layout state.
    /// </summary>
    private void RebuildDocument()
    {
        lastElementHeight = 0;
        logicalDocument = null;
        ClearPhysicalDocument();
        ClearSelection();

        if (Document is null)
        {
            documentWidth = 0;
            documentHeight = 0;
            _layoutDirty = false;
            UpdateContextMenuState();
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        FancyTextParser parser = new();
        logicalDocument = parser.Parse(Document, (float)PageWidth);

        BuildPhysicalDocument();
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

        _selectionCaretOffset = hitOffset.Value;
        UpdateContextMenuState();
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


    private class PhysicalDocument
    {
        public List<PhysicalPage> Pages { get; } = new();
    }

    private class PhysicalPage
    {
        public List<PhysicalParagraph> Paragraphs { get; } = new();
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
        
        // Flag to indicate this is a page break marker
        public bool IsPageBreak { get; init; }

        public List<PhysicalElement> Elements { get; } = [];
        public List<PhysicalParagraph> Lines { get; } = [];
    }


    /// <summary>
    /// Clears the cached physical document structure.
    /// </summary>
    private void ClearPhysicalDocument()
    {
        physDoc.Pages.Clear();
        _documentHasShadowElements = false;
    }

    /// <summary>
    /// Converts the logical document into physical paragraphs and elements for layout.
    /// </summary>
    private void BuildPhysicalDocument()
    {
        if (logicalDocument == null) return;

        ClearPhysicalDocument();

        var currPage = new PhysicalPage();
        var currParagraph = new PhysicalParagraph();
        
        int pageCount = logicalDocument.Pages.Count;
        int currentPageIndex = 0;

        foreach (var page in logicalDocument.Pages)
        {
            foreach (var paragraph in page.Paragraphs)
            {
                // Check if this paragraph contains a NewPage annotation
                bool hasNewPageAnnotation = paragraph.Fragments.Any(f => 
                    f is AnnotationItem { Format: AnnotationItem.AnnotationType.NewPage });
                
                // If paragraph only contains NewPage/NewParagraph annotations, skip it
                // The page break marker will be added separately
                if (hasNewPageAnnotation)
                {
                    continue;
                }
                
                // Copy paragraph properties from logical to physical
                currParagraph.Justification = paragraph.Justification;
                currParagraph.LeftMargin = paragraph.LeftMargin;
                currParagraph.RightMargin = paragraph.RightMargin;
                
                // Check if paragraph contains any actual text fragments (not just formatting annotations)
                bool hasTextFragments = false;
                
                foreach (var fragment in paragraph.Fragments)
                {
                    if (fragment is not TextFragmentBase textFragment) continue;
                    
                    var physElem = new PhysicalElement(this, textFragment);
                    currParagraph.Elements.Add(physElem);
                    hasTextFragments = true;
                    _documentHasShadowElements |= physElem.IsShadow;
                    
                    // Track the height of the last element for blank paragraphs
                    double elementHeight = physElem.Ascent + physElem.Descent;
                    lastElementHeight = elementHeight;
                }
                
                // If paragraph is blank but we have a last element height, set MaxHeight
                if (currParagraph.Elements.Count == 0 && lastElementHeight > 0)
                {
                    currParagraph.MaxHeight = lastElementHeight;
                }
                
                // Only add paragraph if it has text fragments or is a blank line with height
                // Skip annotation-only paragraphs (e.g., NewParagraph/NewPage annotations)
                if (hasTextFragments || currParagraph.MaxHeight > 0)
                {
                    currPage.Paragraphs.Add(currParagraph);
                }
                
                currParagraph = new PhysicalParagraph();
            }
            
            // Add a page break marker paragraph after each page (except the last)
            currentPageIndex++;
            if (currentPageIndex < pageCount)
            {
                var pageBreakParagraph = new PhysicalParagraph
                {
                    IsPageBreak = true,
                    MaxHeight = PAGE_BREAK_PIXEL_HEIGHT
                };
                currPage.Paragraphs.Add(pageBreakParagraph);
            }

            physDoc.Pages.Add(currPage);
            currPage = new PhysicalPage();
        }
    }


    /// <summary>
    /// Computes positions and extents for all physical elements in the document.
    /// </summary>
    private void LayoutPhysicalDocument()
    {
        const float IN_TO_DIP = 96.0f;
        const int LINE_SPACING = 2;
        double pageWidthDip = PageWidth * IN_TO_DIP;
        
        // Start with top padding offset, plus ruler height if shown (with 2px gap)
        double ypos = Padding.Top + (ShowRuler ? RULER_HEIGHT + 2 : 0);
        double maxRightExtent = 0;
        
        foreach (var page in physDoc.Pages)
        {
            foreach (var paragraph in page.Paragraphs)
            {
                paragraph.Lines.Clear();
                
                // Handle page break paragraphs
                if (paragraph.IsPageBreak)
                {
                    paragraph.YPosition = ypos;
                    if (paragraph.MaxHeight > 0)
                    {
                        ypos += paragraph.MaxHeight + LINE_SPACING;
                    }
                    continue;
                }
                
                // Handle blank paragraphs - they should still create vertical space
                if (paragraph.Elements.Count == 0)
                {
                    if (paragraph.MaxHeight > 0)
                    {
                        // Blank paragraph with height set from BuildPhysicalDocument
                        ypos += paragraph.MaxHeight + LINE_SPACING;
                    }
                    continue; // Skip rest of processing for blank paragraph
                }
                
                double leftMargin = paragraph.LeftMargin * IN_TO_DIP;
                double rightMargin = pageWidthDip - (paragraph.RightMargin * IN_TO_DIP);
                double availableWidth = rightMargin - leftMargin;
                
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
                                var elementsToMove = currentLine.Elements.Skip(breakPoint + 1).ToList();
                                currentLine.Elements.RemoveRange(breakPoint + 1, currentLine.Elements.Count - breakPoint - 1);
                                
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
                        
                        // Add element to current line (clone it so we can modify independently)
                        var clonedElement = element.Clone();
                        currentLine.Elements.Add(clonedElement);
                        currentLine.MaxAscender = Math.Max(currentLine.MaxAscender, clonedElement.Ascent);
                        currentLine.MaxHeight = Math.Max(currentLine.MaxHeight, clonedElement.Ascent + clonedElement.Descent);
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
                        var clonedElement = element.Clone();
                        line.Elements.Add(clonedElement);
                        line.MaxAscender = Math.Max(line.MaxAscender, clonedElement.Ascent);
                        line.MaxHeight = Math.Max(line.MaxHeight, clonedElement.Ascent + clonedElement.Descent);
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
            }
        }
        
        // Set document extents including padding
        // Use the maximum of actual content width or page width
        double minDocWidth = Padding.Left + pageWidthDip + Padding.Right;
        documentWidth = Math.Max(maxRightExtent + Padding.Right, minDocWidth);
        documentHeight = ypos + Padding.Bottom;
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
            // Calculate visible viewport in unscaled coordinates for culling
            var inverseScale = 1.0 / Scale;
            var visibleRect = new Rect(
                0,
                0,
                Bounds.Width * inverseScale,
                Bounds.Height * inverseScale
            );
            
            // Draw background rectangle for the entire document area (within scaled context)
            context.DrawRectangle(_viewerBackgroundBrush, null, new Rect(0, 0, documentWidth, documentHeight));

            // Draw ruler at the top if enabled
            if (ShowRuler)
            {
                DrawRuler(context);
            }
            
            if (logicalDocument == null) return;

            var visibleElements = GetVisibleElements(visibleRect, out bool hasVisibleShadows);
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
            
            if (ShowPageBoundaries || ShowMarginBoundaries)
            {
                foreach (var page in physDoc.Pages)
                {
                    foreach (var paragraph in page.Paragraphs)
                    {
                        // Handle page break paragraphs - draw horizontal blue dotted line
                        if (paragraph.IsPageBreak && ShowPageBoundaries)
                        {
                            double centerY = paragraph.YPosition + (paragraph.MaxHeight / 2.0);
                            var lineStart = new Point(Padding.Left, centerY);
                            var lineEnd = new Point(Padding.Left + pageWidthDip, centerY);
                            context.DrawLine(new Pen { Brush = Brushes.Blue, Thickness = 1, DashStyle = DashStyle.Dash },
                                lineStart, lineEnd);
                            continue;
                        }
                        
                        if (ShowMarginBoundaries)
                        {
                            foreach (var line in paragraph.Lines)
                            {
                                if (line.Elements.Count > 0)
                                {
                                    double leftMargin = Padding.Left + (line.LeftMargin * IN_TO_DIP);
                                    double rightMargin =
                                        Padding.Left + pageWidthDip - (line.RightMargin * IN_TO_DIP);
                                    
                                    // Draw left margin line
                                    var leftTop = new Point(leftMargin, line.YPosition);
                                    var leftBottom = new Point(leftMargin, line.YPosition + line.MaxHeight);
                                    context.DrawLine(new Pen { Brush = Brushes.Red, Thickness = 1, DashStyle = DashStyle.Dash },
                                        leftTop, leftBottom);
                                    
                                    // Draw right margin line
                                    var rightTop = new Point(rightMargin, line.YPosition);
                                    var rightBottom = new Point(rightMargin, line.YPosition + line.MaxHeight);
                                    context.DrawLine(new Pen { Brush = Brushes.Red, Thickness = 1, DashStyle = DashStyle.Dash },
                                        rightTop, rightBottom);
                                }
                            }
                        }
                    }
                }
            }

            // Draw Right Margin (max right margin for document)
            if (ShowPageBoundaries)
            {
                var maxRightMargin = Padding.Left + pageWidthDip;
                var topPoint = new Point(maxRightMargin, Padding.Top);
                var botPoint = new Point(maxRightMargin, documentHeight);
                context.DrawLine(new Pen { Brush = Brushes.Blue, Thickness = 1, DashStyle = DashStyle.Dash },
                    topPoint, botPoint);
            }
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
        
        double rulerTop = Padding.Top;
        double rulerBottom = Padding.Top + RULER_HEIGHT;
        double rulerLeft = Padding.Left;
        
        // Draw ruler background
        context.DrawRectangle(new SolidColorBrush(Colors.LightYellow), 
            new Pen(Brushes.Black), 
            new Rect(rulerLeft, rulerTop, pageWidthDip, RULER_HEIGHT));
        
        // Draw tick marks
        for (double inch = 0.125; inch < PageWidth; inch += 0.125) // Every eighth of an inch
        {
            double xPos = rulerLeft + (inch * IN_TO_DIP);
            double tickHeight;
            double tickThickness;
            
            // Determine tick height based on fraction
            if (NearlyEqual(inch,Math.Floor(inch))) // Whole inch
            {
                tickHeight = RULER_HEIGHT * 0.3;
                tickThickness = 2.0;
            }
            else if (NearlyEqual((inch * 2), Math.Floor(inch * 2)))  // Half inch
            {
                tickHeight = RULER_HEIGHT * 0.4;
                tickThickness = 1.5;
            }
            else if (NearlyEqual((inch * 4), Math.Floor(inch * 4))) // Quarter inch
            {
                tickHeight = RULER_HEIGHT * 0.3;
                tickThickness = 1.0;
            }
            else // Eighth inch
            {
                tickHeight = RULER_HEIGHT * 0.2;
                tickThickness = 0.5;
            }
            
            var tickTop = new Point(xPos, rulerBottom - tickHeight);
            var tickBottom = new Point(xPos, rulerBottom);
            context.DrawLine(new Pen(Brushes.Black, tickThickness), tickTop, tickBottom);
            
            // Draw inch numbers
            
            if (NearlyEqual(inch, Math.Floor(inch)) && inch > 0)
            {
                var formattedText = new FormattedText(
                    inch.ToString("0"),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Arial"),
                    8,
                    Brushes.Black);
                
                context.DrawText(formattedText, new Point(xPos - (formattedText.Width/2), rulerTop + 1));
            }
        }

        context.DrawRectangle(new SolidColorBrush(Colors.Transparent), 
            new Pen(Brushes.Black), 
            new Rect(rulerLeft, rulerTop, pageWidthDip, RULER_HEIGHT));

        return;
        
        bool NearlyEqual(double a, double b, double epsilon = 1e-9)
        {
            return Math.Abs(a - b) <= epsilon;
        }

    }

    private class PhysicalElement
    {
        private readonly FancyTextViewer _viewer;
        private TextFragmentBase LogicalItem { get; set; }
        private readonly IBrush _foregroundBrush;
        private readonly IBrush _backgroundBrush;
        public FormattedText FormattedText { get; private set; }
        public Geometry Geometry { get; private set; }
        public double LayoutWidth { get; private set; }
        public double Ascent { get; private set; }
        public double Descent { get; private set; }
        public double Baseline { get; private set; }
        public Vector GlyphOffset { get; private set; }
        public Rect Bounds => Geometry.Bounds;
        public bool IsWhiteSpace => LogicalItem.GetLogicalType() != ILogicalItem.Type.Text;
        public bool IsBreakableSpace => LogicalItem.GetLogicalType() == ILogicalItem.Type.BreakableSpace;
        public Typeface Typeface => LogicalItem.Style.Typeface;
        public double FontSize { get; private set; }
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
            _foregroundBrush = new SolidColorBrush(logicalItem.Style.Foreground);
            _backgroundBrush = new SolidColorBrush(logicalItem.Style.Background);

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

            // Calculate bounds and logical advance width at origin (0,0) - will be positioned later
            var glyphRect = GetGlyphStringRect(FormattedText, new Point(0, 0));
            var highlightGeometry = FormattedText.BuildHighlightGeometry(new Point(0, 0));
            var highlightRect = highlightGeometry?.Bounds;
            var rect = highlightRect != null ? glyphRect.Union(highlightRect.Value) : glyphRect;
            LayoutWidth = Math.Max(highlightRect?.Width ?? 0, FormattedText.Width);
            var metrics = GetMyFontMetrics(logicalItem.Style.Typeface, FontSize);
            var ftAscent = FormattedText.Baseline;
            var ftDescent = FormattedText.Height - FormattedText.Baseline;
            var renderAscent = Math.Max(ftAscent, metrics.MaxAscent);
            var renderDescent = Math.Max(ftDescent, metrics.MaxDescent);

            if (isSuperscript || isSubscript)
            {
                var fullSizeText = new FormattedText(text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    logicalItem.Style.Typeface,
                    logicalItem.Style.FontSize,
                    new SolidColorBrush(logicalItem.Style.Foreground));
                var fullSizeMetrics = GetMyFontMetrics(logicalItem.Style.Typeface, logicalItem.Style.FontSize);
                var fullAscent = Math.Max(fullSizeText.Baseline, fullSizeMetrics.MaxAscent);
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

            Geometry = new RectangleGeometry(rect);
        }

        private PhysicalElement(PhysicalElement source)
        {
            _viewer = source._viewer;
            LogicalItem = source.LogicalItem;
            _foregroundBrush = source._foregroundBrush;
            _backgroundBrush = source._backgroundBrush;
            FormattedText = source.FormattedText;
            Geometry = new RectangleGeometry(source.Geometry.Bounds);
            LayoutWidth = source.LayoutWidth;
            Ascent = source.Ascent;
            Descent = source.Descent;
            Baseline = source.Baseline;
            GlyphOffset = source.GlyphOffset;
            FontSize = source.FontSize;
            Position = new Point(0, 0);
        }
        
        /// <summary>
        /// Creates a clone of this PhysicalElement (shallow copy with new Geometry that can be modified)
        /// </summary>
        public PhysicalElement Clone()
        {
            return new PhysicalElement(this)
            {
                Position = this.Position
            };
        }
        
        /// <summary>
        /// Sets a custom width for this element (used for expanding whitespace in full justification)
        /// </summary>
        public void SetWidth(double newWidth)
        {
            LayoutWidth = newWidth;
            var oldRect = Geometry.Bounds;
            var newRect = new Rect(oldRect.Left, oldRect.Top, newWidth, oldRect.Height);
            Geometry = new RectangleGeometry(newRect);
        }


        /// <summary>
        /// Draws the element background and optional diagnostics.
        /// </summary>
        /// <param name="context">The drawing context to render into.</param>
        /// <param name="drawBaseline"><c>true</c> to draw the baseline guide.</param>
        /// <param name="drawBounds"><c>true</c> to draw the element bounds.</param>
        public void RenderBackground(DrawingContext context, bool drawBaseline = false, bool drawBounds = false)
        {
            var geom = Geometry;

            context.FillRectangle(_backgroundBrush, new Rect(Position, geom.Bounds.Size));
            // Draw bounding box
            if (drawBounds)
            {
                context.DrawRectangle(new Pen(Brushes.Blue, .25), new Rect(Position, Geometry.Bounds.Size));
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
            var met = Typeface.GlyphTypeface.Metrics;
            var scale = FontSize / met.DesignEmHeight;
            context.DrawText(FormattedText, renderOrigin + new Vector(offset, offset));
            if (IsUnderlined && IsWhiteSpace)
            {
                DrawWhitespaceUnderline(
                    context,
                    Math.Max(1.0, met.UnderlineThickness * scale),
                    Math.Abs(met.UnderlinePosition * scale),
                    _foregroundBrush,
                    offset,
                    offset);
            }
        }

        /// <summary>
        /// Draws the glyph foreground, including optional outline rendering.
        /// </summary>
        /// <param name="context">The drawing context to render into.</param>
        public void RenderForeground(DrawingContext context)
        {
            var met = Typeface.GlyphTypeface.Metrics;
            var scale = FontSize / met.DesignEmHeight;
            var renderOrigin = Position + GlyphOffset;

            if (IsOutline /*&& !IsWhiteSpace*/)
            {
                var outlineThickness = met.UnderlineThickness * scale;
                context.DrawTextWithOutline(FormattedText, renderOrigin, new SolidColorBrush(Background),
                    new Pen
                    {
                        LineCap = PenLineCap.Square,
                        Thickness = outlineThickness,
                        Brush = new SolidColorBrush(Foreground)
                    });
            }
            else
            {
                context.DrawText(FormattedText, renderOrigin);
            }

            if (IsUnderlined && IsWhiteSpace)
            {
                DrawWhitespaceUnderline(
                    context,
                    Math.Max(1.0, met.UnderlineThickness * scale),
                    Math.Abs(met.UnderlinePosition * scale),
                    _foregroundBrush);
            }
        }

        /// <summary>
        /// Draws an explicit underline segment for whitespace runs.
        /// </summary>
        /// <param name="context">The drawing context to render into.</param>
        /// <param name="underlineThickness">The computed underline thickness.</param>
        /// <param name="underlineOffset">The absolute underline offset from baseline.</param>
        /// <param name="brush"></param>
        /// <param name="xOffset"></param>
        /// <param name="yOffset"></param>
        private void DrawWhitespaceUnderline(
                DrawingContext context,
                double underlineThickness,
                double underlineOffset,
                IBrush brush,
                double xOffset = 0,
                double yOffset = 0)
        {
            if (LayoutWidth <= 0)
            {
                return;
            }

            double underlineY = Position.Y + yOffset + Baseline + underlineOffset;
            var underlinePen = new Pen(brush, underlineThickness)
            {
                LineCap = PenLineCap.Flat
            };
            var start = new Point(Position.X + xOffset, underlineY);
            var end = new Point(Position.X + xOffset + LayoutWidth, underlineY);
            context.DrawLine(underlinePen, start, end);
        }
    }


    private readonly record struct FontKey(Typeface Typeface, double Size);

    private static readonly Dictionary<FontKey, MyFontMetrics> FontMetricsCache = new();

    /// <summary>
    /// Represents cached ascent and descent metrics for a typeface at a specific size.
    /// </summary>
    /// <param name="MaxAscent">The maximum measured ascent.</param>
    /// <param name="MaxDescent">The maximum measured descent.</param>
    public record MyFontMetrics(double MaxAscent, double MaxDescent);

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
        FontMetricsCache[key] = new MyFontMetrics(ascent, descent);

        return new MyFontMetrics(ascent, descent);
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
    /// Computes glyph bounds for formatted text translated to a specific origin.
    /// </summary>
    /// <param name="ft">The formatted text to measure.</param>
    /// <param name="origin">The origin to translate the bounds to.</param>
    /// <returns>The translated glyph bounds.</returns>
    private static Rect GetGlyphStringRect(FormattedText ft, Point origin)
    {
        var local = GetGlyphStringRect(ft);
        return local.WithX(local.X + origin.X)
            .WithY(local.Y + origin.Y);
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
        PhysicalElement? closestElement = null;
        double closestDistance = double.MaxValue;

        foreach (var element in EnumeratePhysicalElements())
        {
            var bounds = new Rect(element.Position, element.Geometry.Bounds.Size);
            if (bounds.Contains(point))
            {
                return GetOffsetForElementPoint(element, point);
            }

            double dx = 0;
            if (point.X < bounds.Left)
            {
                dx = bounds.Left - point.X;
            }
            else if (point.X > bounds.Right)
            {
                dx = point.X - bounds.Right;
            }

            double dy = 0;
            if (point.Y < bounds.Top)
            {
                dy = bounds.Top - point.Y;
            }
            else if (point.Y > bounds.Bottom)
            {
                dy = point.Y - bounds.Bottom;
            }

            double distance = (dx * dx) + (dy * dy);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestElement = element;
            }
        }

        return closestElement == null ? Document.Text.Length : GetOffsetForElementPoint(closestElement, point);
    }

    /// <summary>
    /// Enumerates all laid-out physical elements in reading order.
    /// </summary>
    /// <returns>A sequence of all physical elements in layout order.</returns>
    private IEnumerable<PhysicalElement> EnumeratePhysicalElements()
    {
        foreach (var page in physDoc.Pages)
        {
            foreach (var paragraph in page.Paragraphs)
            {
                foreach (var line in paragraph.Lines)
                {
                    foreach (var element in line.Elements)
                    {
                        yield return element;
                    }
                }
            }
        }
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

        bool found = false;
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        foreach (var element in EnumeratePhysicalElements())
        {
            if (element.EndOffset <= startOffset || element.StartOffset >= endOffset)
            {
                continue;
            }

            Rect elementRect = new Rect(element.Position, element.Geometry.Bounds.Size);
            minX = Math.Min(minX, elementRect.X);
            minY = Math.Min(minY, elementRect.Y);
            maxX = Math.Max(maxX, elementRect.Right);
            maxY = Math.Max(maxY, elementRect.Bottom);
            found = true;
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
        ScrollViewer? scrollViewer = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
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

        var bounds = new Rect(element.Position, element.Geometry.Bounds.Size);
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
    private List<PhysicalElement> GetVisibleElements(Rect visibleRect, out bool hasVisibleShadows)
    {
        _visibleElementsBuffer.Clear();
        hasVisibleShadows = false;

        foreach (var page in physDoc.Pages)
        {
            foreach (var paragraph in page.Paragraphs)
            {
                foreach (var line in paragraph.Lines)
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