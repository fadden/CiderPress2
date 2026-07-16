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
using FileConv;
using CommunityToolkit.Mvvm.ComponentModel;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel wrapper for FancyTextViewer content.
/// </summary>
public class FancyTextDisplayViewModel : ObservableObject
{
    private const float DEFAULT_PAGE_WIDTH = 8.5f;
    private const float MIN_PAGE_WIDTH = 0.5f;
    private const bool DEFAULT_SHOW_PAGE_BOUNDARIES = true;
    private const bool DEFAULT_SHOW_MARGIN_BOUNDARIES = false;
    private const bool DEFAULT_WORD_WRAP = true;
    private FancyText? mDocument;
    private float mPageWidth = DEFAULT_PAGE_WIDTH;
    private bool mShowPageBoundaries = DEFAULT_SHOW_PAGE_BOUNDARIES;
    private bool mShowMarginBoundaries = DEFAULT_SHOW_MARGIN_BOUNDARIES;
    private bool mWordWrap = DEFAULT_WORD_WRAP;
    public FancyText? Document
    {
        get => mDocument;
        private set => SetProperty(ref mDocument, value);
    }

    public float PageWidth
    {
        get => mPageWidth;
        set => SetProperty(ref mPageWidth, Math.Max(MIN_PAGE_WIDTH, value));
    }

    public bool ShowPageBoundaries
    {
        get => mShowPageBoundaries;
        set => SetProperty(ref mShowPageBoundaries, value);
    }

    public bool ShowMarginBoundaries
    {
        get => mShowMarginBoundaries;
        set => SetProperty(ref mShowMarginBoundaries, value);
    }

    /// <summary>
    /// Whether long lines wrap. Defaults on for fancy (formatted) documents and off for
    /// plain/simple text such as code listings and disassembly, where preserving the
    /// column layout matters more; the user can still toggle it per view.
    /// </summary>
    public bool WordWrap
    {
        get => mWordWrap;
        set => SetProperty(ref mWordWrap, value);
    }

    /// <summary>
    /// Sets display content from conversion output, upconverting plain text to FancyText.
    /// </summary>
    public void SetFromConvOutput(IConvOutput? output)
    {
        switch (output)
        {
            case FancyText fancyText when !fancyText.PreferSimple:
                // Set display options before the document so the viewer parses once
                // with the correct word-wrap mode.
                WordWrap = true;
                ShowPageBoundaries = true;
                ShowMarginBoundaries = false;
                Document = fancyText;
                break;
            case FancyText fancyText:
                SetFromPlainText(fancyText.Text.ToString());
                break;
            case SimpleText simpleText:
                SetFromPlainText(simpleText.Text.ToString());
                break;
            default:
                WordWrap = DEFAULT_WORD_WRAP;
                ShowPageBoundaries = DEFAULT_SHOW_PAGE_BOUNDARIES;
                ShowMarginBoundaries = DEFAULT_SHOW_MARGIN_BOUNDARIES;
                Document = null;
                break;
        }
    }

    /// <summary>
    /// Sets display content from plain text by upconverting to FancyText defaults.
    /// Plain text (code listings, disassembly, etc.) defaults to word wrap off so the
    /// column layout is preserved.
    /// </summary>
    public void SetFromPlainText(string text)
    {
        FancyText fancyText = text.Length > 0 ? new FancyText(text.Length) : new FancyText();
        fancyText.Text.Append(text);
        // Set display options before the document so the viewer parses once with the
        // correct word-wrap mode (plain + wrap off selects the line-fragment fast path).
        WordWrap = false;
        ShowPageBoundaries = false;
        ShowMarginBoundaries = false;
        Document = fancyText;
    }
}
