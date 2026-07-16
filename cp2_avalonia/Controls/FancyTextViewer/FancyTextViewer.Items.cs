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
using System.Text;

namespace cp2_avalonia.Controls.FancyTextViewer;

public interface ILogicalItem
{
    /// <summary>
    /// Identifies the category of logical item represented in the parsed stream.
    /// </summary>
    public enum Type
    {
        /// <summary>
        /// A normal text fragment.
        /// </summary>
        Text,
        /// <summary>
        /// A whitespace fragment that must not be used for line breaking.
        /// </summary>
        NonbreakingSpace,
        /// <summary>
        /// A whitespace fragment that may be used for line breaking.
        /// </summary>
        BreakableSpace,
        /// <summary>
        /// A non-text formatting or structural marker.
        /// </summary>
        Formatting
    }

    /// <summary>
    /// Returns the logical item category represented by this fragment.
    /// </summary>
    /// <returns>The logical item category.</returns>
    Type GetLogicalType();
}

/// <summary>
/// Base class for text-bearing logical items.
/// </summary>
public abstract class TextFragmentBase : ILogicalItem
{
    /// <summary>
    /// Gets or sets the starting source-text offset for this fragment.
    /// </summary>
    public int StartOffset { get; set; }
    /// <summary>
    /// Returns the logical item type for this fragment.
    /// </summary>
    public abstract ILogicalItem.Type GetLogicalType();
    /// <summary>
    /// Gets or sets the resolved style for this fragment.
    /// </summary>
    public TextFragmentStyle Style { get; set; } = new();
    private readonly StringBuilder _text = new();
    private string? _textCache;

    /// <summary>
    /// Gets the text contained in this fragment.
    /// </summary>
    public string Text => _textCache ??= _text.ToString();
    /// <summary>
    /// Gets the exclusive end offset for this fragment in the source text.
    /// </summary>
    public int EndOffset => StartOffset + _text.Length;

    /// <summary>
    /// Appends a character to this logical text fragment.
    /// </summary>
    /// <param name="c">The character to append.</param>
    public void AppendCharacter(char c)
    {
        _text.Append(c);
        _textCache = null;
    }

    /// <summary>
    /// Appends a string to this logical text fragment in one operation. Used by the
    /// plain-text fast path, which emits whole lines as single fragments.
    /// </summary>
    /// <param name="text">The text to append.</param>
    public void AppendText(string text)
    {
        _text.Append(text);
        _textCache = null;
    }
}

/// <summary>
/// Represents a standard text fragment.
/// </summary>
public class FancyTextFragment : TextFragmentBase
{
    /// <summary>
    /// Identifies this fragment as regular text.
    /// </summary>
    /// <returns><see cref="ILogicalItem.Type.Text"/>.</returns>
    public override ILogicalItem.Type GetLogicalType() => ILogicalItem.Type.Text;
    /// <summary>
    /// Returns a debug-oriented representation of this fragment.
    /// </summary>
    /// <returns>A short debug string for the fragment.</returns>
    public override string ToString() => "[Text:" + Text + "]";
}

/// <summary>
/// Represents a non-breaking whitespace fragment.
/// </summary>
public class NonbreakingSpaceFragment : TextFragmentBase
{
    /// <summary>
    /// Identifies this fragment as non-breaking whitespace.
    /// </summary>
    /// <returns><see cref="ILogicalItem.Type.NonbreakingSpace"/>.</returns>
    public override ILogicalItem.Type GetLogicalType() => ILogicalItem.Type.NonbreakingSpace;
    /// <summary>
    /// Returns a debug-oriented representation of this fragment.
    /// </summary>
    /// <returns>A short debug string for the fragment.</returns>
    public override string ToString() => "[NBSP:" + Text + "]";
}

/// <summary>
/// Represents whitespace that can be used as a line-break opportunity.
/// </summary>
public class BreakableSpaceFragment : TextFragmentBase
{
    /// <summary>
    /// Identifies this fragment as breakable whitespace.
    /// </summary>
    /// <returns><see cref="ILogicalItem.Type.BreakableSpace"/>.</returns>
    public override ILogicalItem.Type GetLogicalType() => ILogicalItem.Type.BreakableSpace;
    /// <summary>
    /// Returns a debug-oriented representation of this fragment.
    /// </summary>
    /// <returns>A short debug string for the fragment.</returns>
    public override string ToString() => "[SP:" + Text + "]";
}

/// <summary>
/// Represents non-text formatting markers embedded in the logical stream.
/// </summary>
public class AnnotationItem : ILogicalItem
{
    /// <summary>
    /// Identifies the kind of formatting annotation carried by an <see cref="AnnotationItem"/>.
    /// </summary>
    public enum AnnotationType
    {
        /// <summary>
        /// An unknown or unspecified annotation type.
        /// </summary>
        Unknown,
        /// <summary>
        /// Marks a paragraph break.
        /// </summary>
        NewParagraph,
        /// <summary>
        /// Marks a page break.
        /// </summary>
        NewPage,
        /// <summary>
        /// Marks a tab stop.
        /// </summary>
        Tab
    }

    /// <summary>
    /// Gets or sets the annotation category.
    /// </summary>
    public AnnotationType Format { get; set; }
    /// <summary>
    /// Gets or sets the annotation payload, if any.
    /// </summary>
    public object? data { get; set; }
    /// <summary>
    /// Gets or sets the source text that the annotation logically represents.
    /// </summary>
    public string? SkippedText { get; set; }

    /// <summary>
    /// Identifies this item as formatting metadata.
    /// </summary>
    /// <returns><see cref="ILogicalItem.Type.Formatting"/>.</returns>
    public ILogicalItem.Type GetLogicalType() => ILogicalItem.Type.Formatting;
    /// <summary>
    /// Returns a debug-oriented representation of this annotation.
    /// </summary>
    /// <returns>A short debug string for the annotation.</returns>
    public override string ToString() => "[Anno:" + Format + ":" + data + "]";
}
