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
using FancyTextDocument = FileConv.FancyText;

namespace cp2_avalonia.Controls.FancyTextViewer;

/// <summary>
/// Represents a logical paragraph composed of text and annotation fragments.
/// </summary>
public class FancyTextLogicalParagraph
{
    /// <summary>
    /// Gets the logical fragments that make up the paragraph.
    /// </summary>
    public readonly List<ILogicalItem> Fragments = [];

    /// <summary>
    /// Gets or sets the paragraph justification.
    /// </summary>
    public FancyTextDocument.Justification Justification { get; set; } = FancyTextDocument.Justification.Left;
    /// <summary>
    /// Gets or sets the paragraph left margin in inches.
    /// </summary>
    public float LeftMargin { get; set; } = ViewerLayoutConstants.DefaultLeftMargin;
    /// <summary>
    /// Gets or sets the paragraph right margin offset from the page edge, in inches.
    /// </summary>
    public float RightMargin { get; set; } = ViewerLayoutConstants.DefaultRightMargin;

    /// <summary>
    /// Appends a logical item to the paragraph, applying the current text state to text fragments.
    /// </summary>
    /// <param name="fragment">The fragment to append.</param>
    /// <param name="state">The current source text state for text fragments.</param>
    public void AppendItem(ILogicalItem fragment, SourceTextState? state = null)
    {
        if (fragment is TextFragmentBase f)
        {
            ArgumentNullException.ThrowIfNull(state);
            f.Style.ApplySourceTextState(state);
        }

        Fragments.Add(fragment);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the paragraph currently contains only whitespace.
    /// </summary>
    public bool OnlyWhiteSpace = true;
}

/// <summary>
/// Represents a logical page composed of paragraphs.
/// </summary>
public class FancyTextLogicalPage
{
    /// <summary>
    /// Gets the paragraphs contained on this logical page.
    /// </summary>
    public readonly List<FancyTextLogicalParagraph> Paragraphs = [];

    /// <summary>
    /// Appends a paragraph and stamps it with the current paragraph formatting state.
    /// </summary>
    /// <param name="paragraph">The paragraph to append.</param>
    /// <param name="paragraphState">The paragraph formatting state to copy onto it.</param>
    public void AppendParagraph(FancyTextLogicalParagraph paragraph, ParagraphState paragraphState)
    {
        paragraph.Justification = paragraphState.Justification;
        paragraph.LeftMargin = paragraphState.LeftMargin;
        paragraph.RightMargin = paragraphState.RightMargin;
        Paragraphs.Add(paragraph);
    }
}

/// <summary>
/// Represents the full logical document consumed by the viewer layout pass.
/// </summary>
public class FancyTextLogicalDocument
{
    /// <summary>
    /// Gets the logical pages contained in the document.
    /// </summary>
    public readonly List<FancyTextLogicalPage> Pages = [];

    /// <summary>
    /// Appends a page to the logical document.
    /// </summary>
    /// <param name="page">The page to append.</param>
    public void AppendPage(FancyTextLogicalPage page)
    {
        Pages.Add(page);
    }
}
