/*
 * Copyright 2023 faddenSoft
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
using System.Collections;
using System.Diagnostics;

namespace FileConv {
    /// <summary>
    /// <para>This represents a text document with formatting annotations.</para>
    /// <para>The text and annotations are held in separate data structures, similar to the way
    /// some Apple documents stored the formatting in the resource fork.  This allows the
    /// text to be exported as either fancy or plain.</para>
    /// </summary>
    /// <remarks>
    /// <para>The format is intended to be easy to convert to RTF, which is still supported as
    /// an import format by various document editors.</para>
    /// <para>Initial values:</para>
    /// <list type="bullet">
    ///   <item>Font is monospace sans-serif (Consolas).</item>
    ///   <item>Font size is 10 points.</item>
    ///   <item>Paragraphs are left justified.</item>
    ///   <item>Left/right margins set to zero.</item>
    ///   <item>Foreground color is black, background color is white.</item>
    /// </list>
    /// <para>Tab stops are not well defined at present.</para>
    /// </remarks>
    public class FancyText : SimpleText, IEnumerable<FancyText.Annotation> {
        public const char NO_BREAK_SPACE = '\u00a0';    // NO-BREAK SPACE (NBSP) or "sticky space"

        public FancyText() : base() { }
        public FancyText(int lengthGuess) : base(lengthGuess) { }

        /// <summary>
        /// Annotation type.
        /// </summary>
        public enum AnnoType {
            Unknown = 0,
            NewParagraph,
            NewPage,
            Tab,

            Justification,
            LeftMargin,
            RightMargin,

            FontFamily,
            FontSize,
            Bold,
            Italic,
            Underline,
            Outline,
            Shadow,
            Superscript,
            Subscript,

            ForeColor,
            BackColor,
        }

        /// <summary>
        /// Annotation added at a specific position in the output text file.  There may be more
        /// than one annotation at a given offset.
        /// </summary>
        public class Annotation {
            public AnnoType Type { get; private set; }
            public object? Data { get; private set; }
            public int ExtraLen { get; private set; }
            public int Offset { get; private set; }

            //public Annotation(AnnoType type, object? data, int offset) :
            //    this (type, data, 0, offset) { }

            public Annotation(AnnoType type, object? data, int extraLen, int offset) {
                Type = type;
                Data = data;
                ExtraLen = extraLen;
                Offset = offset;
            }
            public override string ToString() {
                return "[Anno: type=" + Type + " off=" + Offset + "]";
            }
        }
        private List<Annotation> mAnnotations = new List<Annotation>();

        public IEnumerator<Annotation> GetEnumerator() {
            return mAnnotations.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() {
            return mAnnotations.GetEnumerator();
        }

        /// <summary>
        /// Paragraph justification.
        /// </summary>
        public enum Justification { Unknown = 0, Left, Right, Center, Full }

        /// <summary>
        /// Font family information.
        /// </summary>
        /// <remarks>
        /// Immutable.
        /// </remarks>
        public class FontFamily {
            public string Name { get; private set; }
            public bool IsMono { get; private set; }
            public bool IsSerif { get; private set; }

            public FontFamily(string name, bool isMono, bool isSerif) {
                Name = name;
                IsMono = isMono;
                IsSerif = isSerif;
            }
            public static bool operator ==(FontFamily a, FontFamily b) {
                return a.Name == b.Name && a.IsMono == b.IsMono && a.IsSerif == b.IsSerif;
            }
            public static bool operator !=(FontFamily a, FontFamily b) {
                return !(a == b);
            }
            public override bool Equals(object? obj) {
                return obj is FontFamily && this == (FontFamily)obj;
            }
            public override int GetHashCode() {
                return Name.GetHashCode() + (IsMono ? 1 : 0) + (IsSerif ? 2 : 0);
            }
            public override string ToString() {
                return "[FontFamily: name=" + Name +
                    " isMono=" + IsMono + " isSerif=" + IsSerif + "]";
            }
        }
        public static readonly FontFamily DEFAULT_FONT = new FontFamily("Consolas", true, false);

        //
        // Current values.  We track these so we only output an annotation if something changes.
        //

        // Paragraph formatting.
        private float mLeftMargin = 0.0f;       // inches
        private float mRightMargin = 0.0f;      // inches
        private Justification mJustification = Justification.Left;

        // Character styling.
        private FontFamily mFontFamily = DEFAULT_FONT;
        private int mFontBasePointSize = 10;
        private float mFontSizeMult = 1.0f;
        private int mFontPointSize = 10;
        private bool mBoldEnabled = false;
        private bool mItalicEnabled = false;
        private bool mUnderlineEnabled = false;
        private bool mOutlineEnabled = false;
        private bool mShadowEnabled = false;
        private bool mSuperscriptEnabled = false;
        private bool mSubscriptEnabled = false;

        // Color.
        private int mForeColor = ConvUtil.MakeRGB(0x00, 0x00, 0x00);    // default = black
        private int mBackColor = ConvUtil.MakeRGB(0xff, 0xff, 0xff);    // default = white


        /// <summary>
        /// Adds an annotation to the list.
        /// </summary>
        /// <param name="type">Annotation type.</param>
        /// <param name="data">Optional data argument.</param>
        /// <param name="extraLen">Number of bytes to exclude from fancy output.</param>
        private void AddAnnotation(AnnoType type, object? data, int extraLen = 0) {
            mAnnotations.Add(new Annotation(type, data, extraLen, Text.Length));
        }

        /// <summary>
        /// Marks the start of a new paragraph.  Do this at the end of each line.
        /// </summary>
        public void NewParagraph() {
            // The annotation generator should eat the EOL marker in the text stream, which is
            // present so that we can extract the plain text trivially.
            AddAnnotation(AnnoType.NewParagraph, null, Environment.NewLine.Length);
            Text.Append(Environment.NewLine);
        }

        /// <summary>
        /// Inserts a page break.  No effect on plain text output.
        /// </summary>
        public void NewPage() {
            AddAnnotation(AnnoType.NewPage, null);
        }

        /// <summary>
        /// Emit a tab.
        /// </summary>
        public void Tab() {
            // Add a tab annotation, and a tab character that will only appear in plain text.
            AddAnnotation(AnnoType.Tab, null, 1);
            Text.Append("\t");
        }

        /// <summary>
        /// Sets the text justification.
        /// </summary>
        /// <param name="just">Justification mode.</param>
        public void SetJustification(Justification just) {
            if (just != mJustification) {
                AddAnnotation(AnnoType.Justification, just);
                mJustification = just;
            }
        }

        /// <summary>
        /// Sets the left margin.
        /// </summary>
        /// <param name="margin">Margin, in inches.</param>
        public void SetLeftMargin(float margin) {
            if (!FloatEquals(margin, mLeftMargin)) {
                AddAnnotation(AnnoType.LeftMargin, margin);
                mLeftMargin = margin;
            }
        }

        /// <summary>
        /// Sets the right margin.
        /// </summary>
        /// <param name="margin">Margin, in inches.</param>
        public void SetRightMargin(float margin) {
            if (!FloatEquals(margin, mRightMargin)) {
                AddAnnotation(AnnoType.RightMargin, margin);
                mRightMargin = margin;
            }
        }

        /// <summary>
        /// Sets the font family.
        /// </summary>
        /// <param name="family">Font family.</param>
        /// <param name="mult">Font size multiplier.</param>
        public void SetFontFamily(FontFamily family, float mult = 1.0f) {
            if (family != mFontFamily) {
                AddAnnotation(AnnoType.FontFamily, family);
                mFontFamily = family;
            }

            // Check for a multiplier change.
            if (mult != mFontSizeMult) {
                mFontSizeMult = mult;
                SetFontSize(mFontBasePointSize);
            }
        }

        /// <summary>
        /// Sets the font size.
        /// </summary>
        /// <param name="points">Font size, in points (1/72nd of an inch).</param>
        public void SetFontSize(int points) {
            int newSize = (int)Math.Round(points * mFontSizeMult);
            if (newSize != mFontPointSize) {
                AddAnnotation(AnnoType.FontSize, newSize);
                mFontPointSize = newSize;
            }
        }

        /// <summary>
        /// Enables or disables the "bold" style.
        /// </summary>
        /// <param name="enabled">True if enabled.</param>
        public void SetBold(bool enabled) {
            if (enabled != mBoldEnabled) {
                AddAnnotation(AnnoType.Bold, enabled);
                mBoldEnabled = enabled;
            }
        }

        /// <summary>
        /// Enables or disables the "italic" style.
        /// </summary>
        /// <param name="enabled">True if enabled.</param>
        public void SetItalic(bool enabled) {
            if (enabled != mItalicEnabled) {
                AddAnnotation(AnnoType.Italic, enabled);
                mItalicEnabled = enabled;
            }
        }

        /// <summary>
        /// Enables or disables the "underline" style.
        /// </summary>
        /// <param name="enabled">True if enabled.</param>
        public void SetUnderline(bool enabled) {
            if (enabled != mUnderlineEnabled) {
                AddAnnotation(AnnoType.Underline, enabled);
                mUnderlineEnabled = enabled;
            }
        }

        /// <summary>
        /// Enables or disables the "outline" style.
        /// </summary>
        /// <param name="enabled">True if enabled.</param>
        public void SetOutline(bool enabled) {
            if (enabled != mOutlineEnabled) {
                AddAnnotation(AnnoType.Outline, enabled);
                mOutlineEnabled = enabled;
            }
        }

        /// <summary>
        /// Enables or disables the "shadow" style.
        /// </summary>
        /// <param name="enabled">True if enabled.</param>
        public void SetShadow(bool enabled) {
            if (enabled != mShadowEnabled) {
                AddAnnotation(AnnoType.Shadow, enabled);
                mShadowEnabled = enabled;
            }
        }

        /// <summary>
        /// Enables or disables the "superscript" style.
        /// </summary>
        /// <param name="enabled">True if enabled.</param>
        public void SetSuperscript(bool enabled) {
            if (enabled != mSuperscriptEnabled) {
                AddAnnotation(AnnoType.Superscript, enabled);
                mSuperscriptEnabled = enabled;
            }
        }

        /// <summary>
        /// Enables or disables the "subscript" style.
        /// </summary>
        /// <param name="enabled">True if enabled.</param>
        public void SetSubscript(bool enabled) {
            if (enabled != mSubscriptEnabled) {
                AddAnnotation(AnnoType.Subscript, enabled);
                mSubscriptEnabled = enabled;
            }
        }

        /// <summary>
        /// Helper function that accepts an Apple IIgs font style byte.
        /// </summary>
        /// <remarks>
        /// Allows the QuickDraw II styles, plus the AWGS additions.
        /// </remarks>
        /// <param name="style">Style bit flags.</param>
        public void SetGSFontStyle(byte style) {
            SetBold((style & (int)Doc.GSDocCommon.FontStyleBits.Bold) != 0);
            SetItalic((style & (int)Doc.GSDocCommon.FontStyleBits.Italic) != 0);
            SetUnderline((style & (int)Doc.GSDocCommon.FontStyleBits.Underline) != 0);
            SetOutline((style & (int)Doc.GSDocCommon.FontStyleBits.Outline) != 0);
            SetShadow((style & (int)Doc.GSDocCommon.FontStyleBits.Shadow) != 0);
            SetSuperscript((style & (int)Doc.GSDocCommon.FontStyleBits.Superscript) != 0);
            SetSubscript((style & (int)Doc.GSDocCommon.FontStyleBits.Subscript) != 0);
        }

        /// <summary>
        /// Changes the text foreground color.  Doesn't generate anything if the new color
        /// matches the old color.
        /// </summary>
        /// <param name="color">New color, as an ARGB value (0xFFrrggbb).</param>
        public void SetForeColor(int color) {
            if (color != mForeColor) {
                AddAnnotation(AnnoType.ForeColor, color);
                mForeColor = color;
            }
        }

        /// <summary>
        /// Changes the text background color.  Doesn't generate anything if the new color
        /// matches the old color.
        /// </summary>
        /// <param name="color">New color, as an ARGB value (0xFFrrggbb).</param>
        public void SetBackColor(int color) {
            if (color != mBackColor) {
                AddAnnotation(AnnoType.BackColor, color);
                mBackColor = color;
            }
        }

        /// <summary>
        /// Determines whether two floating-point values are equal.  This is difficult to do
        /// perfectly because of the way floats are represented, but for our purposes we don't
        /// need perfection.
        /// </summary>
        /// <remarks>
        /// See also <see href="https://stackoverflow.com/q/3874627/294248"/>.
        /// </remarks>
        /// <param name="a">First number.</param>
        /// <param name="b">Second number.</param>
        /// <returns>True if the numbers are equal.</returns>
        private static bool FloatEquals(float a, float b) {
            const float DIFF = 0.0001f;
            if (a == b) {
                // Handle infinities and trivial case.
                return true;
            } else {
                return Math.Abs(a - b) <= DIFF;
            }
        }
    }
}
