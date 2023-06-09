﻿/*
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
    /// </remarks>
    public class FancyText : SimpleText, IEnumerable<FancyText.Annotation> {
        public FancyText() : base() { }
        public FancyText(int lengthGuess) : base(lengthGuess) { }

        /// <summary>
        /// Annotation type.
        /// </summary>
        public enum AnnoType {
            Unknown = 0,
            NewParagraph,
            Tab,

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
        private static FontFamily DEFAULT_FONT = new FontFamily("Courier", true, true);

        //
        // Current values.  We track these so we only output an annotation if something changes.
        //

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
        /// Emit a tab.
        /// </summary>
        public void Tab() {
            AddAnnotation(AnnoType.Tab, null);
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
    }
}
