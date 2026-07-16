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
using System.Linq;
using System.Text;
using FancyTextDocument = FileConv.FancyText;

namespace cp2_avalonia.Controls.FancyTextViewer;

/// <summary>
/// Converts FancyText into semantic runs with stable formatting state.
/// </summary>
public class FancyTextParser
{
    /// <summary>
    /// Parses a FancyText document into the logical structure consumed by the viewer layout engine.
    /// </summary>
    /// <param name="fancyText">The source document to parse.</param>
    /// <param name="pageWidthInches">The page width in inches.</param>
    /// <param name="coarseLineFragments">When <c>true</c> and the document carries no
    /// annotations, each line becomes a single fragment instead of being split into
    /// word/whitespace runs. Only valid when word wrapping is disabled, since coarse
    /// fragments contain no line-break opportunities.</param>
    /// <returns>The parsed logical document.</returns>
    public FancyTextLogicalDocument Parse(
            FancyTextDocument fancyText,
            float pageWidthInches = ViewerLayoutConstants.DefaultPageWidth,
            bool coarseLineFragments = false)
    {
        if (coarseLineFragments && !fancyText.Any())
        {
            return ParsePlainLines(fancyText, pageWidthInches);
        }

        var factory = new FancyTextLogicalDocumentBuilder(pageWidthInches);
        var currAnnotationIndex = 0;
        var length = fancyText.Text.Length;

        var items = fancyText.OrderBy(a => a.Offset).ToList();

        var curOffset = 0;
        while (curOffset < length)
        {
            while (currAnnotationIndex < items.Count && items[currAnnotationIndex].Offset == curOffset)
            {
                curOffset += factory.ProcessAnnotation(items[currAnnotationIndex]);
                currAnnotationIndex++;
            }

            if (curOffset < length)
            {
                char currentChar = fancyText.Text[curOffset];
                if (TryHandleUnannotatedParagraphBreak(fancyText.Text, curOffset, out int breakLen))
                {
                    factory.ProcessAnnotation(new FancyTextDocument.Annotation(
                        FancyTextDocument.AnnoType.NewParagraph, null, 0, curOffset));
                    curOffset += breakLen;
                }
                else
                {
                    factory.AddCharacter(currentChar, curOffset);
                    curOffset++;
                }
            }
        }

        while (currAnnotationIndex < items.Count)
        {
            factory.ProcessAnnotation(items[currAnnotationIndex]);
            currAnnotationIndex++;
        }

        factory.Flush();
        var parsedDocument = factory.GetDocument();
        return parsedDocument;
    }

    private static readonly char[] LineBreakChars = ['\r', '\n'];

    /// <summary>
    /// Fast path for plain (annotation-free) documents: splits the text on line breaks
    /// and emits one fragment per line, avoiding per-character classification and
    /// word/whitespace fragment splitting. A viewport line then realizes as a single
    /// shaped run instead of one per word and space.
    /// </summary>
    /// <param name="fancyText">The annotation-free source document.</param>
    /// <param name="pageWidthInches">The page width in inches.</param>
    /// <returns>The parsed logical document.</returns>
    private static FancyTextLogicalDocument ParsePlainLines(
            FancyTextDocument fancyText, float pageWidthInches)
    {
        var factory = new FancyTextLogicalDocumentBuilder(pageWidthInches);
        string text = fancyText.Text.ToString();
        int length = text.Length;
        int offset = 0;

        while (offset < length)
        {
            int lineEnd = text.IndexOfAny(LineBreakChars, offset);
            if (lineEnd < 0)
            {
                factory.AddTextRun(text[offset..], offset);
                break;
            }

            if (lineEnd > offset)
            {
                factory.AddTextRun(text[offset..lineEnd], offset);
            }
            factory.ProcessAnnotation(new FancyTextDocument.Annotation(
                FancyTextDocument.AnnoType.NewParagraph, null, 0, lineEnd));

            // Consume CR, LF, CRLF, or LFCR (same as TryHandleUnannotatedParagraphBreak).
            int breakLength = 1;
            if (lineEnd + 1 < length)
            {
                char ch = text[lineEnd];
                char next = text[lineEnd + 1];
                if ((ch == '\r' && next == '\n') || (ch == '\n' && next == '\r'))
                {
                    breakLength = 2;
                }
            }
            offset = lineEnd + breakLength;
        }

        factory.Flush();
        return factory.GetDocument();
    }

    /// <summary>
    /// Detects raw CR/LF line breaks that appear without explicit FancyText paragraph annotations.
    /// </summary>
    /// <param name="text">Source text buffer.</param>
    /// <param name="offset">Current parse offset.</param>
    /// <param name="breakLength">Number of source characters consumed by the line break.</param>
    /// <returns><c>true</c> if an unannotated line break was found.</returns>
    private static bool TryHandleUnannotatedParagraphBreak(
            StringBuilder text, int offset, out int breakLength)
    {
        breakLength = 0;
        if (offset < 0 || offset >= text.Length)
        {
            return false;
        }

        char ch = text[offset];
        if (ch != '\r' && ch != '\n')
        {
            return false;
        }

        breakLength = 1;
        if (offset + 1 < text.Length)
        {
            char next = text[offset + 1];
            if ((ch == '\r' && next == '\n') || (ch == '\n' && next == '\r'))
            {
                breakLength = 2;
            }
        }
        return true;
    }
}
