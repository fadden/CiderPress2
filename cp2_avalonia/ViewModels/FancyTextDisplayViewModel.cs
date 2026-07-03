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
    private FancyText? mDocument;
    private float mPageWidth = DEFAULT_PAGE_WIDTH;
    private bool mShowPageBoundaries = DEFAULT_SHOW_PAGE_BOUNDARIES;
    private bool mShowMarginBoundaries = DEFAULT_SHOW_MARGIN_BOUNDARIES;
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
    /// Sets display content from conversion output, upconverting plain text to FancyText.
    /// </summary>
    public void SetFromConvOutput(IConvOutput? output)
    {
        switch (output)
        {
            case FancyText fancyText when !fancyText.PreferSimple:
                Document = fancyText;
                ShowPageBoundaries = true;
                ShowMarginBoundaries = false;
                break;
            case FancyText fancyText:
                SetFromPlainText(fancyText.Text.ToString());
                break;
            case SimpleText simpleText:
                SetFromPlainText(simpleText.Text.ToString());
                break;
            default:
                Document = null;
                ShowPageBoundaries = DEFAULT_SHOW_PAGE_BOUNDARIES;
                ShowMarginBoundaries = DEFAULT_SHOW_MARGIN_BOUNDARIES;
                break;
        }
    }

    /// <summary>
    /// Sets display content from plain text by upconverting to FancyText defaults.
    /// </summary>
    public void SetFromPlainText(string text)
    {
        FancyText fancyText = text.Length > 0 ? new FancyText(text.Length) : new FancyText();
        fancyText.Text.Append(text);
        Document = fancyText;
        ShowPageBoundaries = false;
        ShowMarginBoundaries = false;
    }
}
