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
using System.Diagnostics;

using CommonUtil;
using DiskArc;
using FileConv.Generic;

namespace FileConv.Doc {
    /// <summary>
    /// Formats an Apple IIgs "Teach Document" file.
    /// </summary>
    public class Teach : Converter {
        public const string TAG = "teach";
        public const string LABEL = "Teach Document";
        public const string DESCRIPTION =
            "Converts an Apple IIgs Teach Document to formatted text.";
        public const string DISCRIMINATOR = "ProDOS GWP/$5445, with resource fork.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const ushort TEACH_AUX_TYPE = 0x5445;       // 'TE', for TextEdit or TEach
        private const int RSTYLEBLOCK = 0x8012;


        private Teach() { }

        public Teach(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }


        protected override Applicability TestApplicability() {
            if (DataStream == null || ResMgr == null) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_GWP &&
                    FileAttrs.AuxType == TEACH_AUX_TYPE) {
                return Applicability.Yes;
            }
            return Applicability.Not;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(FancyText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);
            Debug.Assert(ResMgr != null);

            FancyText output = new FancyText();
            ResourceMgr.ResourceEntry? styleEntry = ResMgr.FindEntry(RSTYLEBLOCK, 1);
            byte[]? styleBlock = null;
            if (styleEntry != null) {
                styleBlock = ResMgr.ReadEntry(styleEntry);
            }
            if (styleBlock == null) {
                output.Notes.AddE("Unable to find rStyleBlock resource");
                PlainText.ConvertStream(DataStream, ConvUtil.ExportCharSrc.MacOSRoman, true, -1,
                    output.Text);
                return output;
            }
            TEFormat teFormat = new TEFormat();
            int offset = 0;
            if (!teFormat.Load(styleBlock, ref offset)) {
                output.Notes.AddE("Failed to parse rStyleBlock resource");
                PlainText.ConvertStream(DataStream, ConvUtil.ExportCharSrc.MacOSRoman, true, -1,
                    output.Text);
                return output;
            }
            //output.Notes.AddI("Number of TERulers: " + teFormat.TheRulerList.Count);
            //output.Notes.AddI("Number of TEStyles: " + teFormat.TheStyleList.Count);
            //output.Notes.AddI("Number of StyleItems: " + teFormat.TheStyles.Count);
            FormatTeach(DataStream, teFormat, output);
            return output;
        }

        /// <summary>
        /// Formats a Teach document.
        /// </summary>
        /// <param name="dataStream">Data fork stream.</param>
        /// <param name="teFormat">TextEdit formatting instructions.</param>
        /// <param name="output">FancyText object.</param>
        private static void FormatTeach(Stream dataStream, TEFormat teFormat, FancyText output) {
            dataStream.Position = 0;

            // We expect to find exactly one ruler, since it's not clear what to do if there's
            // more than one.  The Teach application doesn't seem to provide a way to configure
            // tabs or justification, but it can import files from AppleWorks and MacWrite.
            //
            // TODO: do something with the ruler

            for (int sindex = 0; sindex < teFormat.NumberOfStyles; sindex++) {
                StyleItem si = teFormat.TheStyles[sindex];
                if (si.Length == -1) {
                    // Unused entry.
                    continue;
                }

                // Configure style changes.
                TEStyle? style = teFormat.GetStyleByOffset(si.Offset);
                if (style == null) {
                    output.Notes.AddW("Unable to find style with offset=" + si.Offset);
                    // continue without style
                } else {
                    ApplyStyle(style, output);
                }

                // Output the characters.
                for (int i = 0; i < si.Length; i++) {
                    int bval = dataStream.ReadByte();
                    if (bval < 0) {
                        output.Notes.AddW("Styles extended past end of text");
                        return;
                    }
                    if (bval == '\r') {
                        output.NewParagraph();
                    } else if (bval == '\t') {
                        output.Tab();
                    } else {
                        char ch = MacChar.MacToUnicode((byte)bval, MacChar.Encoding.Roman);
                        output.Append(ch);
                    }
                }
            }
        }

        /// <summary>
        /// Applies the various style values to the FancyText.
        /// </summary>
        private static void ApplyStyle(TEStyle style, FancyText output) {
            FancyText.FontFamily? family = GSDocCommon.FindFont(style.FontFamily);
            if (family is not null) {
                output.SetFontFamily(family, GSDocCommon.GetFontMult(style.FontFamily));
            }
            output.SetFontSize(style.FontSize);
            byte styleBits = style.FontStyle;
            if (style.FontSize < 10) {
                // Hack: the text shown on screen doesn't show underlines for text smaller
                // than 10 points.  Some docs have large stretches of underlined text, probably
                // because the author was unaware that the style was enabled.
                styleBits = (byte)(styleBits & ~(int)GSDocCommon.FontStyleBits.Underline);
            }
            output.SetGSFontStyle(styleBits);

            // The Spy Hunter GS "Read.Me" has a word in color $4444.  Haven't seen others,
            // probably because the Teach app itself doesn't provide a way to set the color.
            output.SetForeColor(GSDocCommon.GetStdColor(style.ForeColor, true));
            output.SetBackColor(GSDocCommon.GetStdColor(style.BackColor, true));
        }
    }

    #region TextEdit structures

    /// <summary>
    /// Apple IIgs TextEdit TEFormat structure.  May be found in rStyleBlock resources.
    /// </summary>
    internal class TEFormat {
        private const int MAX_STYLES = 32767;

        public ushort Version { get; private set; }
        public int RulerListLength { get; private set; }
        public List<TERuler> TheRulerList { get; private set; } = new List<TERuler>();
        public int StyleListLength { get; private set; }
        public List<TEStyle> TheStyleList { get; private set; } = new List<TEStyle>();
        public int NumberOfStyles { get; private set; }
        public List<StyleItem> TheStyles { get; private set; } = new List<StyleItem>();

        public bool Load(byte[] buf, ref int offset) {
            try {   // catch exception rather than trying to range-check everything
                Version = RawData.ReadU16LE(buf, ref offset);
                if (Version != 0) {
                    Debug.WriteLine("Unexpected version");
                    // Keep going?
                }
                RulerListLength = (int)RawData.ReadU32LE(buf, ref offset);
                int endOffset = offset + RulerListLength;
                while (offset < endOffset) {
                    TERuler newRuler = new TERuler();
                    newRuler.Load(buf, ref offset);
                    TheRulerList.Add(newRuler);

                    if (endOffset - offset < TERuler.MIN_LEN) {
                        // Some documents with absolute tabs (e.g. PatchHFS.doc) seem to have
                        // one too many entries in the tab list (as if the $ffff were included
                        // in the list and also as tabTerminator).  Work around that here by
                        // halting if there can't possibly be another ruler.
                        offset = endOffset;
                    }
                }

                StyleListLength = (int)RawData.ReadU32LE(buf, ref offset);
                int styleListOffset = offset;
                endOffset = offset + StyleListLength;
                while (offset < endOffset) {
                    TEStyle newStyle = new TEStyle();
                    newStyle.Load(buf, ref offset, offset - styleListOffset);
                    TheStyleList.Add(newStyle);
                }

                NumberOfStyles = (int)RawData.ReadU32LE(buf, ref offset);
                if (NumberOfStyles > MAX_STYLES) {
                    Debug.WriteLine("Too many style items: " + NumberOfStyles);
                    return false;
                }
                for (int i = 0; i < NumberOfStyles; i++) {
                    StyleItem newItem = new StyleItem();
                    newItem.Load(buf, ref offset);
                    TheStyles.Add(newItem);
                }
                return true;
            } catch (IndexOutOfRangeException ex) {
                // This will catch overrun from this class or any of the sub-classes.
                Debug.WriteLine("TEFormat load overran buffer: " + ex);
                return false;
            }
        }

        public TEStyle? GetStyleByOffset(int offset) {
            foreach (TEStyle style in TheStyleList) {
                if (style.StyleListOffset == offset) {
                    return style;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Apple IIgs TextEdit TERuler structure.  May be found in rTERuler resources.
    /// </summary>
    internal class TERuler {
        public const int MIN_LEN = 18;

        public enum Justification : short {
            Left = 0, Right = -1, Center = 1, Full = 2
        }
        public enum Tabbing : short {
            None = 0, Regular = 1, Absolute = 2
        }
        public short LeftMargin { get; private set; }
        public short LeftIndent { get; private set; }
        public short RightMargin { get; private set; }
        public Justification Just { get; private set; }
        public short ExtraLS { get; private set; }
        public short Flags { get; private set; }
        public uint UserData { get; private set; }
        public Tabbing TabType { get; private set; }
        public TabItem[] TabItems { get; private set; } = new TabItem[0];
        public short TabTerminator { get; private set; }

        public void Load(byte[] buf, ref int offset) {
            LeftMargin = (short)RawData.ReadU16LE(buf, ref offset);
            LeftIndent = (short)RawData.ReadU16LE(buf, ref offset);
            RightMargin = (short)RawData.ReadU16LE(buf, ref offset);
            Just = (Justification)RawData.ReadU16LE(buf, ref offset);
            ExtraLS = (short)RawData.ReadU16LE(buf, ref offset);
            Flags = (short)RawData.ReadU16LE(buf, ref offset);
            UserData = RawData.ReadU32LE(buf, ref offset);
            TabType = (Tabbing)RawData.ReadU16LE(buf, ref offset);
            switch (TabType) {
                case Tabbing.None:
                    // No tabs, no more data to read.
                    TabTerminator = 0;
                    break;
                case Tabbing.Regular:
                    // Regularly-spaced tabs; spacing determined by next 16-bit value.
                    TabTerminator = (short)RawData.ReadU16LE(buf, ref offset);
                    break;
                case Tabbing.Absolute:
                    // Tabs at arbitrary absolute pixel positions.
                    List<TabItem> tabList = new List<TabItem>();
                    while (true) {
                        TabItem ti = new TabItem();
                        ti.Load(buf, ref offset);
                        if (ti.TabKind == -1) {
                            break;
                        }
                        tabList.Add(ti);
                    }
                    TabItems = tabList.ToArray();
                    TabTerminator = -1;
                    break;
                default:
                    // Bad TabType value from data.  We're probably hosed; ignore it and hope
                    // for the best?
                    Debug.WriteLine("Unknown TabType " + TabType);
                    break;
            }
        }
    }

    /// <summary>
    /// Apple IIgs TextEdit TEStyle structure.
    /// </summary>
    internal class TEStyle {
        public uint FontID { get; private set; }
        public ushort ForeColor { get; private set; }
        public ushort BackColor { get; private set; }
        public uint UserData { get; private set; }

        // Track byte offset within list, since that's how StyleItem references us.
        public int StyleListOffset { get; private set; }

        // Break apart the FontID field.
        public ushort FontFamily { get { return (ushort)FontID; } }
        public byte FontStyle { get { return (byte)(FontID >> 16); } }
        public byte FontSize { get { return (byte)(FontID >> 24); } }

        public void Load(byte[] buf, ref int offset, int styleListOffset) {
            FontID = RawData.ReadU32LE(buf, ref offset);
            ForeColor = RawData.ReadU16LE(buf, ref offset);
            BackColor = RawData.ReadU16LE(buf, ref offset);
            UserData = RawData.ReadU32LE(buf, ref offset);

            StyleListOffset = styleListOffset;
        }
    }

    /// <summary>
    /// Apple IIgs TextEdit StyleItem structure.
    /// </summary>
    internal class StyleItem {
        public int Length { get; private set; }
        public int Offset { get; private set; }

        public void Load(byte[] buf, ref int offset) {
            Length = (int)RawData.ReadU32LE(buf, ref offset);
            Offset = (int)RawData.ReadU32LE(buf, ref offset);
        }
    }

    /// <summary>
    /// Apple IIgs TextEdit TabItem structure.
    /// </summary>
    internal class TabItem {
        public short TabKind { get; private set; }
        public short TabData { get; private set; }

        public void Load(byte[] buf, ref int offset) {
            TabKind = (short)RawData.ReadU16LE(buf, ref offset);
            if (TabKind == -1) {
                return;     // end of list reached
            }
            TabData = (short)RawData.ReadU16LE(buf, ref offset);
        }
    }

    #endregion TextEdit structures
}
