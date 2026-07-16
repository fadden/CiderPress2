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
using System.Linq;
using FancyTextDocument = FileConv.FancyText;

namespace cp2_avalonia.Controls.FancyTextViewer;

/// <summary>
/// Builds the viewer's logical document model from FancyText content and annotations.
/// </summary>
public class FancyTextLogicalDocumentBuilder
{
    private FancyTextLogicalDocument? document;
    private FancyTextLogicalPage? currentPage;
    private FancyTextLogicalParagraph? currentParagraph;
    private readonly SourceTextState state = new();
    private readonly ParagraphState paragraphState;
    private ILogicalItem? currentFragment;

    public FancyTextLogicalDocumentBuilder(float pageWidthInches = ViewerLayoutConstants.DefaultPageWidth)
    {
        paragraphState = new ParagraphState(pageWidthInches);
    }

    /// <summary>
    /// Returns the logical document being assembled, creating it on first use.
    /// </summary>
    /// <returns>The current logical document instance.</returns>
    public FancyTextLogicalDocument GetDocument()
    {
        document ??= GenerateNewFancyTextLogicalDocument();
        return document;
    }

    /// <summary>
    /// Appends a source character to the current logical fragment.
    /// </summary>
    /// <param name="c">The character to append.</param>
    /// <param name="sourceOffset">The corresponding offset in the source document.</param>
    public void AddCharacter(char c, int sourceOffset)
    {
        GuaranteeParagraph();
        var expectedType = GetCharacterType(c);
        if (expectedType == ILogicalItem.Type.Text)
        {
            currentParagraph!.OnlyWhiteSpace = false;
        }

        if (currentFragment != null && currentFragment.GetLogicalType() != expectedType)
        {
            FinalizeCurrentFragment();
        }

        if (currentFragment == null)
        {
            currentFragment = GenerateNewCharFragment(expectedType);
            var textFragment = (TextFragmentBase)currentFragment;
            textFragment.StartOffset = sourceOffset;
            textFragment.Style.ApplySourceTextState(state);
        }

        ((TextFragmentBase)currentFragment).AppendCharacter(c);
    }

    /// <summary>
    /// Appends a run of source text as a single text fragment. Used by the plain-text
    /// fast path (no annotations, word wrap off), where per-word/whitespace splitting is
    /// unnecessary because nothing needs line-break opportunities or style boundaries.
    /// </summary>
    /// <param name="text">The run text (must not contain line breaks).</param>
    /// <param name="sourceOffset">The run's starting offset in the source document.</param>
    public void AddTextRun(string text, int sourceOffset)
    {
        if (text.Length == 0)
        {
            return;
        }

        FinalizeCurrentFragment();
        GuaranteeParagraph();
        currentParagraph!.OnlyWhiteSpace = false;

        var fragment = new FancyTextFragment { StartOffset = sourceOffset };
        fragment.AppendText(text);
        currentParagraph!.AppendItem(fragment, state);
    }

    /// <summary>
    /// Determines whether a paragraph already ends with an explicit new-paragraph annotation.
    /// </summary>
    /// <param name="paragraph">The paragraph to inspect.</param>
    /// <returns><c>true</c> when the paragraph already terminates with a new-paragraph marker.</returns>
    private static bool EndsWithNewParagraph(FancyTextLogicalParagraph paragraph)
    {
        return paragraph.Fragments.LastOrDefault() is AnnotationItem
        {
            Format: AnnotationItem.AnnotationType.NewParagraph
        };
    }

    /// <summary>
    /// Checks whether the current output position is already terminated by a paragraph break.
    /// </summary>
    /// <returns><c>true</c> when the current paragraph stream already ends in a paragraph break.</returns>
    private bool CurrentLineAlreadyEndsWithNewParagraph()
    {
        if (currentParagraph != null)
        {
            return EndsWithNewParagraph(currentParagraph);
        }

        return currentPage?.Paragraphs.LastOrDefault() is { } lastParagraph &&
               EndsWithNewParagraph(lastParagraph);
    }

    /// <summary>
    /// Applies an annotation to the current builder state and returns any source offset advance it consumes.
    /// </summary>
    /// <param name="anno">The annotation to apply.</param>
    /// <returns>The number of source characters consumed by the annotation.</returns>
    public int ProcessAnnotation(FancyTextDocument.Annotation anno)
    {
        var ptrAdvanceValue = 0;

        switch (anno.Type)
        {
            case FancyTextDocument.AnnoType.NewParagraph:
                FinalizeCurrentFragment();
                GuaranteeParagraph();
                currentFragment = GenerateNewParagraphAnnotation();
                ptrAdvanceValue = anno.ExtraLen;
                currentParagraph!.AppendItem(currentFragment, state);
                currentFragment = null;
                FinalizeCurrentParagraph();
                break;
            case FancyTextDocument.AnnoType.NewPage:
                FinalizeCurrentFragment();

                if (!CurrentLineAlreadyEndsWithNewParagraph())
                {
                    GuaranteeParagraph();
                    var paragraphAnno = GenerateNewParagraphAnnotation();
                    currentParagraph!.AppendItem(paragraphAnno);
                    FinalizeCurrentParagraph();
                }

                GuaranteeParagraph();
                var pageAnno = GenerateNewPageAnnotation();
                currentParagraph!.AppendItem(pageAnno);
                var followingParagraphAnno = GenerateNewParagraphAnnotation();
                currentParagraph!.AppendItem(followingParagraphAnno);
                FinalizeCurrentPage();
                break;
            case FancyTextDocument.AnnoType.Tab:
                break;
            case FancyTextDocument.AnnoType.Justification:
                HandleJustification(anno.Data);
                break;
            case FancyTextDocument.AnnoType.LeftMargin:
                HandleLeftMargin(anno.Data);
                break;
            case FancyTextDocument.AnnoType.RightMargin:
                HandleRightMargin(anno.Data);
                break;
            case FancyTextDocument.AnnoType.FontFamily:
                if (anno.Data is FancyTextDocument.FontFamily ffamily)
                {
                    HandleFontFamily(ffamily);
                }
                break;
            case FancyTextDocument.AnnoType.FontSize:
                if (anno.Data is int fontSize)
                {
                    HandleFontSize(fontSize);
                }
                break;
            case FancyTextDocument.AnnoType.Bold:
                if (anno.Data is bool doBold)
                {
                    HandleBold(doBold);
                }
                break;
            case FancyTextDocument.AnnoType.Italic:
                if (anno.Data is bool doItalic)
                {
                    HandleItalic(doItalic);
                }
                break;
            case FancyTextDocument.AnnoType.Underline:
                if (anno.Data is bool doUnderline)
                {
                    HandleUnderline(doUnderline);
                }
                break;
            case FancyTextDocument.AnnoType.Outline:
                if (anno.Data is bool doOutline)
                {
                    HandleOutline(doOutline);
                }
                break;
            case FancyTextDocument.AnnoType.Shadow:
                if (anno.Data is bool doShadow)
                {
                    HandleShadow(doShadow);
                }
                break;
            case FancyTextDocument.AnnoType.Superscript:
                if (anno.Data is bool doSuper)
                {
                    HandleSuperscript(doSuper);
                }
                break;
            case FancyTextDocument.AnnoType.Subscript:
                if (anno.Data is bool doSub)
                {
                    HandleSubscript(doSub);
                }
                break;
            case FancyTextDocument.AnnoType.ForeColor:
                if (anno.Data is int fcolor)
                {
                    HandleForeColor(fcolor);
                }
                break;
            case FancyTextDocument.AnnoType.BackColor:
                if (anno.Data is int bcolor)
                {
                    HandleBackColor(bcolor);
                }
                break;
        }

        return ptrAdvanceValue;
    }

    /// <summary>
    /// Updates the active font family for subsequently emitted fragments.
    /// </summary>
    /// <param name="family">The new font family.</param>
    private void HandleFontFamily(FancyTextDocument.FontFamily family)
    {
        if (state.FontFamily == family) return;
        FinalizeCurrentFragment();
        state.FontFamily = family;
    }

    /// <summary>
    /// Updates the active font size for subsequently emitted fragments.
    /// </summary>
    /// <param name="size">The new font size in points.</param>
    private void HandleFontSize(int size)
    {
        if (state.FontSizePoints == size) return;
        FinalizeCurrentFragment();
        state.FontSizePoints = size;
    }

    /// <summary>
    /// Updates the active bold state for subsequently emitted fragments.
    /// </summary>
    /// <param name="enabled"><c>true</c> to enable bold text.</param>
    private void HandleBold(bool enabled)
    {
        if (state.Bold == enabled) return;
        FinalizeCurrentFragment();
        state.Bold = enabled;
    }

    /// <summary>
    /// Updates the active italic state for subsequently emitted fragments.
    /// </summary>
    /// <param name="enabled"><c>true</c> to enable italic text.</param>
    private void HandleItalic(bool enabled)
    {
        if (state.Italic == enabled) return;
        FinalizeCurrentFragment();
        state.Italic = enabled;
    }

    /// <summary>
    /// Updates the active underline state for subsequently emitted fragments.
    /// </summary>
    /// <param name="enabled"><c>true</c> to enable underline.</param>
    private void HandleUnderline(bool enabled)
    {
        if (state.Underline == enabled) return;
        FinalizeCurrentFragment();
        state.Underline = enabled;
    }

    /// <summary>
    /// Updates the active outline state for subsequently emitted fragments.
    /// </summary>
    /// <param name="enabled"><c>true</c> to enable outlining.</param>
    private void HandleOutline(bool enabled)
    {
        if (state.Outline == enabled) return;
        FinalizeCurrentFragment();
        state.Outline = enabled;
    }

    /// <summary>
    /// Updates the active shadow state for subsequently emitted fragments.
    /// </summary>
    /// <param name="enabled"><c>true</c> to enable shadow rendering.</param>
    private void HandleShadow(bool enabled)
    {
        if (state.Shadow == enabled) return;
        FinalizeCurrentFragment();
        state.Shadow = enabled;
    }

    /// <summary>
    /// Updates the active superscript state for subsequently emitted fragments.
    /// </summary>
    /// <param name="enabled"><c>true</c> to enable superscript positioning.</param>
    private void HandleSuperscript(bool enabled)
    {
        if (state.Superscript == enabled) return;
        FinalizeCurrentFragment();
        state.Superscript = enabled;
    }

    /// <summary>
    /// Updates the active subscript state for subsequently emitted fragments.
    /// </summary>
    /// <param name="enabled"><c>true</c> to enable subscript positioning.</param>
    private void HandleSubscript(bool enabled)
    {
        if (state.Subscript == enabled) return;
        FinalizeCurrentFragment();
        state.Subscript = enabled;
    }

    /// <summary>
    /// Updates the active foreground color for subsequently emitted fragments.
    /// </summary>
    /// <param name="color">The packed foreground color value (0xRRGGBB).</param>
    private void HandleForeColor(int color)
    {
        if (state.ForeColor == color) return;
        FinalizeCurrentFragment();
        state.ForeColor = color;
    }

    /// <summary>
    /// Updates the active background color for subsequently emitted fragments.
    /// </summary>
    /// <param name="color">The packed background color value (0xRRGGBB).</param>
    private void HandleBackColor(int color)
    {
        if (state.BackColor == color) return;
        FinalizeCurrentFragment();
        state.BackColor = color;
    }

    /// <summary>
    /// Applies paragraph justification for the current paragraph state.
    /// </summary>
    /// <param name="data">The annotation payload that carries the justification value.</param>
    private void HandleJustification(object? data)
    {
        if (data == null) return;
        paragraphState.Justification = (FancyTextDocument.Justification)data;
    }

    /// <summary>
    /// Applies the left margin for the current paragraph state.
    /// </summary>
    /// <param name="data">The annotation payload that carries the left margin.</param>
    private void HandleLeftMargin(object? data)
    {
        if (data == null) return;
        paragraphState.LeftMargin = (float)data;
    }

    /// <summary>
    /// Applies the right margin for the current paragraph state.
    /// </summary>
    /// <param name="data">The annotation payload that carries the right margin.</param>
    private void HandleRightMargin(object? data)
    {
        if (data == null) return;
        paragraphState.RightMargin = (float)data;
    }

    /// <summary>
    /// Classifies an input character into the logical fragment type used by the builder.
    /// </summary>
    /// <param name="c">The input character to classify.</param>
    /// <returns>The logical fragment type for the supplied character.</returns>
    private ILogicalItem.Type GetCharacterType(char c)
    {
        GuaranteeParagraph();
        if (!char.IsWhiteSpace(c)) return ILogicalItem.Type.Text;

        var isNbsp = c == '\u00A0';
        return isNbsp || currentParagraph!.OnlyWhiteSpace
            ? ILogicalItem.Type.NonbreakingSpace
            : ILogicalItem.Type.BreakableSpace;
    }

    /// <summary>
    /// Ensures that a logical document exists.
    /// </summary>
    private void GuaranteeDocument()
    {
        document ??= GenerateNewFancyTextLogicalDocument();
    }

    /// <summary>
    /// Ensures that a logical page exists.
    /// </summary>
    private void GuaranteePage()
    {
        GuaranteeDocument();
        currentPage ??= GenerateNewFancyTextLogicalPage();
    }

    /// <summary>
    /// Ensures that a logical paragraph exists.
    /// </summary>
    private void GuaranteeParagraph()
    {
        GuaranteePage();
        currentParagraph ??= GenerateNewFancyTextLogicalParagraph();
    }

    /// <summary>
    /// Creates a new logical document container.
    /// </summary>
    /// <returns>A new logical document.</returns>
    private static FancyTextLogicalDocument GenerateNewFancyTextLogicalDocument() => new();
    /// <summary>
    /// Creates a new logical page container.
    /// </summary>
    /// <returns>A new logical page.</returns>
    private static FancyTextLogicalPage GenerateNewFancyTextLogicalPage() => new();
    /// <summary>
    /// Creates a new logical paragraph container.
    /// </summary>
    /// <returns>A new logical paragraph.</returns>
    private static FancyTextLogicalParagraph GenerateNewFancyTextLogicalParagraph() => new();

    /// <summary>
    /// Creates a logical text fragment for the requested character classification.
    /// </summary>
    /// <param name="type">The logical item type to create.</param>
    /// <returns>A new fragment matching <paramref name="type"/>.</returns>
    private static ILogicalItem GenerateNewCharFragment(ILogicalItem.Type type)
    {
        return type switch
        {
            ILogicalItem.Type.NonbreakingSpace => new NonbreakingSpaceFragment(),
            ILogicalItem.Type.BreakableSpace => new BreakableSpaceFragment(),
            ILogicalItem.Type.Text => new FancyTextFragment(),
            _ => throw new ArgumentException("Invalid character type for fragment generation", nameof(type))
        };
    }

    /// <summary>
    /// Creates an annotation item that marks a paragraph break.
    /// </summary>
    /// <returns>A paragraph-break annotation item.</returns>
    private static AnnotationItem GenerateNewParagraphAnnotation()
    {
        return new AnnotationItem
        {
            Format = AnnotationItem.AnnotationType.NewParagraph,
            SkippedText = "\n"
        };
    }

    /// <summary>
    /// Creates an annotation item that marks a page break.
    /// </summary>
    /// <returns>A page-break annotation item.</returns>
    private static AnnotationItem GenerateNewPageAnnotation()
    {
        return new AnnotationItem
        {
            Format = AnnotationItem.AnnotationType.NewPage,
            SkippedText = "\f"
        };
    }

    /// <summary>
    /// Finalizes any partially built page content.
    /// </summary>
    public void Flush()
    {
        FinalizeCurrentPage();
    }

    /// <summary>
    /// Commits the active fragment into the current paragraph.
    /// </summary>
    private void FinalizeCurrentFragment()
    {
        if (currentFragment == null) return;
        GuaranteeParagraph();
        currentParagraph!.AppendItem(currentFragment, state);
        currentFragment = null;
    }

    /// <summary>
    /// Commits the active paragraph into the current page.
    /// </summary>
    private void FinalizeCurrentParagraph()
    {
        FinalizeCurrentFragment();
        if (currentParagraph == null) return;

        GuaranteePage();
        currentPage!.AppendParagraph(currentParagraph, paragraphState);
        currentParagraph = null;
    }

    /// <summary>
    /// Commits the active page into the logical document.
    /// </summary>
    private void FinalizeCurrentPage()
    {
        FinalizeCurrentParagraph();
        if (currentPage == null) return;

        GuaranteeDocument();
        document!.AppendPage(currentPage);
        currentPage = null;
    }
}
