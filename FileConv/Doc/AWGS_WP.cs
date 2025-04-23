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
using System.Text;

using CommonUtil;
using DiskArc;

namespace FileConv.Doc {
    /// <summary>
    /// AppleWorks GS word processor document.
    /// </summary>
    public class AWGS_WP : Converter {
        public const string TAG = "awgswp";
        public const string LABEL = "AppleWorks GS WP";
        public const string DESCRIPTION =
            "Converts an AppleWorks GS Word Processor document to formatted text.";
        public const string DISCRIMINATOR = "ProDOS GWP/$8010.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        public const string OPT_SHOW_EMBEDS = "embed";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_SHOW_EMBEDS, "Show embedded formatting",
                    OptionDefinition.OptType.Boolean, "true"),
            };

        private const int DOC_HEADER_LEN = 282;
        private const int GLOBALS_LEN = 386;
        private const int DATE_FIELD_WIDTH = 26;
        private const int TIME_FIELD_WIDTH = 10;
        private const ushort PAGE_BREAK_PARA = 0x0001;

        private const int MIN_LEN = DOC_HEADER_LEN + GLOBALS_LEN;
        private const int MAX_LEN = 8 * 1024 * 1024;        // arbitrary 8MB cap

        private static readonly int EMBED_FG_COLOR = ConvUtil.MakeRGB(0x00, 0x00, 0xff);
        private static readonly FancyText.FontFamily DEFAULT_FONT = FancyText.DEFAULT_FONT;
        private const int DEFAULT_FONT_SIZE = 10;

        private static readonly SaveArrayEntry[] EMPTY_SAE_ARRAY = new SaveArrayEntry[0];
        private static readonly Ruler[] EMPTY_RULER_ARRAY = new Ruler[0];
        private static readonly TabRecord[] EMPTY_TAB_ARRAY = new TabRecord[0];
        private static readonly TextBlockRecord[] EMPTY_TBR_ARRAY = new TextBlockRecord[0];

        /// <summary>
        /// Document chunks.  Three in total: document, header, footer.
        /// </summary>
        private class Chunk {
            public ushort mSaveArrayCount;
            public int mSaveArrayOffset;        // file offset of start of save array entries
            public SaveArrayEntry[] mSaveArrayEntries = EMPTY_SAE_ARRAY;

            public ushort mNumRulers;
            public int mRulersOffset;           // file offset of start of rulers
            public Ruler[] mRulers = EMPTY_RULER_ARRAY;

            public ushort mNumTextBlocks;
            public int mTextBlocksOffset;       // file offset of start of text blocks
            public TextBlockRecord[] mTextBlockRecords = EMPTY_TBR_ARRAY;
        }

        /// <summary>
        /// SaveArray entry.  One of these per paragraph.
        /// </summary>
        private class SaveArrayEntry {
            public const int LENGTH = 12;

            public ushort mTextBlock;       // Text Block number, starting from zero
            public ushort mOffset;          // offset of paragraph within text block
            public ushort mAttributes;      // 0=normal text, 1=page break paragraph
            public ushort mRulerNum;        // number of ruler associated with this paragraph
            public ushort mPixelHeight;     // height of paragraph, in pixels
            public ushort mNumLines;        // number of lines in paragraph

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                mTextBlock = RawData.ReadU16LE(buf, ref offset);
                mOffset = RawData.ReadU16LE(buf, ref offset);
                mAttributes = RawData.ReadU16LE(buf, ref offset);
                mRulerNum = RawData.ReadU16LE(buf, ref offset);
                mPixelHeight = RawData.ReadU16LE(buf, ref offset);
                mNumLines = RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        /// <summary>
        /// Ruler.
        /// </summary>
        /// <remarks>
        /// Every paragraph references one, and every chunk has at least one paragraph,
        /// so there must be at least one ruler per chunk.
        /// </remarks>
        private class Ruler {
            public const int LENGTH = 52;
            public const int NUM_TAB_RECORDS = 10;

            [Flags]
            public enum StatusFlags : ushort {
                FullJustification = 1 << 7,
                RightJustification = 1 << 6,
                CenterJustification = 1 << 5,
                LeftJustification = 1 << 4,
                NoBreakPage = 1 << 3,           // "paragraph cannot break pages"
                TripleSpaced = 1 << 2,          // "really double"
                DoubleSpaced = 1 << 1,          // "really one and one half"
                SingleSpaced = 1,
            }

            public ushort mNumParagraphs;   // number of paragraphs using this ruler
            public ushort mStatusBits;      // justification, multiple line spacing
            public ushort mLeftMargin;      // in pixels from left edge
            public ushort mIndentMargin;    // in pixels from left edge
            public ushort mRightMargin;     // in pixels from left edge
            public ushort mNumTabs;         // number of tabs defined (1-10)
            public TabRecord[] mTabRecords = EMPTY_TAB_ARRAY;

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                mNumParagraphs = RawData.ReadU16LE(buf, ref offset);
                mStatusBits = RawData.ReadU16LE(buf, ref offset);
                mLeftMargin = RawData.ReadU16LE(buf, ref offset);
                mIndentMargin = RawData.ReadU16LE(buf, ref offset);
                mRightMargin = RawData.ReadU16LE(buf, ref offset);
                mNumTabs = RawData.ReadU16LE(buf, ref offset);
                mTabRecords = new TabRecord[NUM_TAB_RECORDS];
                for (int i = 0; i < NUM_TAB_RECORDS; i++) {
                    mTabRecords[i] = new TabRecord();
                    mTabRecords[i].Load(buf, ref offset);
                }
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        /// <summary>
        /// Tab Record.
        /// </summary>
        private class TabRecord {
            public const int LENGTH = 4;

            public ushort mTabLocation;     // position of tab, in pixels from left edge
            public ushort mTabType;         // 0=left, 1=right, -1=decimal

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                mTabLocation = RawData.ReadU16LE(buf, ref offset);
                mTabType = RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        /// <summary>
        /// Text Block Record.
        /// </summary>
        private class TextBlockRecord {
            public uint mLongLen;           // length of text block, in Text Block Record
            public ushort mBlockSize;       // length of text block, in Text Block
            public ushort mBlockUsed;       // number of bytes actually used; always == block size

            public int mTextOffset;         // file offset of paragraph data
        }

        /// <summary>
        /// Paragraph header, found at the start of every paragraph.
        /// </summary>
        private class ParagraphHeader {
            public const int LENGTH = 7;

            public ushort mFirstFont;       // font family number of initial font
            public byte mFirstStyle;        // initial font style
            public byte mFirstSize;         // initial font size, in points
            public byte mFirstColor;        // initial color, as index into QuickDrawII color table
            public ushort mReserved;

            public void Load(byte[] buf, ref int offset) {
                int startOffset = offset;
                mFirstFont = RawData.ReadU16LE(buf, ref offset);
                mFirstStyle = buf[offset++];
                mFirstSize = buf[offset++];
                mFirstColor = buf[offset++];
                mReserved = RawData.ReadU16LE(buf, ref offset);
                Debug.Assert(offset - startOffset == LENGTH);
            }
        }

        private const int PALETTE_LENGTH = 16;
        private int[] mColorTable = RawData.EMPTY_INT_ARRAY;
        private int mCurColor;      // ARGB value

        // Internal state.
        private bool mShowEmbeds;
        private bool mSingleSpacing;


        private AWGS_WP() { }

        public AWGS_WP(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_GWP && FileAttrs.AuxType == 0x8010) {
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
            // Load the entire file into memory.
            Debug.Assert(DataStream != null);
            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);

            mShowEmbeds = GetBoolOption(options, OPT_SHOW_EMBEDS, true);
            mSingleSpacing = true;

            FancyText output = new FancyText();
            int strikes = 0;

            // Version number of the file format.  $1011 for v1.0v2 and v1.1.  $0006 has also
            // been seen; could be an early beta?
            ushort version = RawData.GetU16LE(fileBuf, 0);
            if (version != 0x1011 && version != 0x0006) {
                output.Notes.AddW("Found unexpected file version number: $" +
                    version.ToString("x2"));
                strikes++;
            }
            ushort headerSize = RawData.GetU16LE(fileBuf, 2);
            if (headerSize != DOC_HEADER_LEN) {
                output.Notes.AddW("Found unexpected header size: " + headerSize);
                // We might want to use this value instead of the constant, but in practice there
                // are no AWGS-WP files with a different header size.  This file is probably
                // corrupted or mis-typed.
                strikes++;
            }

            // Pull the colors out of the color table.  The table holds a single 16-color palette,
            // two bytes per color in 640-mode QuickDraw II format.  In 640 mode, colors for four
            // adjacent pixels come from different parts of the palette, so we need to average
            // them together to simulate dithering.
            mColorTable = Gfx.QuickDrawII.Make640Palette(fileBuf, 56);

            // Look at some stuff in the globals.
            int offset = DOC_HEADER_LEN;
            ushort intVersion = RawData.GetU16LE(fileBuf, offset + 0);
            if (intVersion != 0x0002) {
                output.Notes.AddW("Found unexpected internal version: $" +
                    intVersion.ToString("x2"));
                strikes++;
            }

            byte dateLen = fileBuf[offset + 6];
            byte timeLen = fileBuf[offset + 32];
            if (dateLen >= DATE_FIELD_WIDTH || timeLen >= TIME_FIELD_WIDTH) {
                output.Notes.AddW("Bad date/time lengths: " + dateLen + ", " + timeLen);
                strikes++;
            } else {
                output.Notes.AddI("File was last saved " +
                    Encoding.ASCII.GetString(fileBuf, offset + 7, dateLen) + " at " +
                    Encoding.ASCII.GetString(fileBuf, offset + 33, timeLen));
            }
            if (strikes >= 3) {
                output.Notes.AddE("Header looks bad, giving up");
                return output;
            }

            // Scan the three chunks: document, header, footer.
            offset += GLOBALS_LEN;
            Chunk? docChunk, headerChunk, footerChunk;
            if ((docChunk = ScanChunk(fileBuf, ref offset, output)) == null) {
                return output;
            }
            if ((headerChunk = ScanChunk(fileBuf, ref offset, output)) == null) {
                return output;
            }
            if ((footerChunk = ScanChunk(fileBuf, ref offset, output)) == null) {
                return output;
            }

            if (offset != fileBuf.Length) {
                output.Notes.AddI("Found " + (fileBuf.Length - offset) +
                    " extra bytes of data at end of file");
            }

            // Show header and footer at the start of the document, but only if embedded
            // formatting is being displayed.
            // TODO: don't show if the chunk only contains a single empty paragraph
            if (mShowEmbeds) {
                PrintEmbedLine("<header>", output);
                PrintChunk(fileBuf, headerChunk, output);
                PrintEmbedLine("</header>", output);

                PrintEmbedLine("<footer>", output);
                PrintChunk(fileBuf, footerChunk, output);
                PrintEmbedLine("</footer>", output);
            }

            PrintChunk(fileBuf, docChunk, output);

            return output;
        }

        /// <summary>
        /// Scans the contents of a chunk, confirming its structure.
        /// </summary>
        /// <param name="buf">Data buffer.</param>
        /// <param name="offset">Offset of start of chunk.  On exit, this will be set to the
        ///   offset of the first byte past the end of the chunk.</param>
        /// <param name="output">FancyText object, for notes.</param>
        /// <returns>New Chunk object, or null on failure.</returns>
        private Chunk? ScanChunk(byte[] buf, ref int offset, FancyText output) {
            try {
                Debug.WriteLine("Scanning chunk at offset 0x" + offset.ToString("x4"));
                Chunk chunk = new Chunk();

                // Get the saveArray count, which is also the number of paragraphs.
                chunk.mSaveArrayCount = RawData.ReadU16LE(buf, ref offset);
                if (chunk.mSaveArrayCount == 0) {
                    // Should always be at least one paragraph.
                    output.Notes.AddE("Found empty saveArray");
                    return null;
                }
                chunk.mSaveArrayOffset = offset;
                Debug.WriteLine("Scanning " + chunk.mSaveArrayCount + " SAEs");

                // Read the saveArray contents.  Remember the largest ruler number and text
                // block number seen.
                ushort maxRuler = 0;
                ushort maxTextBlock = 0;
                chunk.mSaveArrayEntries = new SaveArrayEntry[chunk.mSaveArrayCount];
                for (int i = 0; i < chunk.mSaveArrayCount; i++) {
                    chunk.mSaveArrayEntries[i] = new SaveArrayEntry();
                    chunk.mSaveArrayEntries[i].Load(buf, ref offset);

                    // If this is a regular (non-page-break) paragraph, check the ruler number.
                    if (chunk.mSaveArrayEntries[i].mAttributes != PAGE_BREAK_PARA) {
                        ushort rulerNum = chunk.mSaveArrayEntries[i].mRulerNum;
                        if (rulerNum > maxRuler) {
                            maxRuler = rulerNum;
                        }
                    }
                    // Update the text block number.
                    ushort textBlockNum = chunk.mSaveArrayEntries[i].mTextBlock;
                    if (textBlockNum > maxTextBlock) {
                        maxTextBlock = textBlockNum;
                    }
                }
                Debug.Assert(offset ==
                    chunk.mSaveArrayOffset + chunk.mSaveArrayCount * SaveArrayEntry.LENGTH);

                // Read the rulers.
                chunk.mNumRulers = (ushort)(maxRuler + 1);
                if (chunk.mNumRulers == 0) {
                    output.Notes.AddE("Found invalid ruler count");
                    return null;
                }
                chunk.mRulersOffset = offset;
                chunk.mRulers = new Ruler[chunk.mNumRulers];
                for (int i = 0; i < chunk.mNumRulers; i++) {
                    chunk.mRulers[i] = new Ruler();
                    chunk.mRulers[i].Load(buf, ref offset);
                }

                // We've reached the Text Blocks.  Assuming text blocks are used in order, the
                // text block number of the last paragraph should tells us how many there are.
                // I don't know if this is guaranteed, so we captured the max value earlier.
                chunk.mNumTextBlocks = (ushort)(maxTextBlock + 1);
                if (chunk.mNumTextBlocks == 0) {
                    output.Notes.AddE("Found invalid text block count");
                    return null;
                }
                chunk.mTextBlocksOffset = offset;

                // Advance the offset past the text blocks.  These have variable size, so we
                // need to scan them.
                int tbLen = ScanTextBlocks(buf, chunk, output);
                if (tbLen < 0) {
                    return null;
                }
                offset += tbLen;

                // Success!
                return chunk;
            } catch (IndexOutOfRangeException ex) {
                Debug.WriteLine("Ran off end while scanning chunk: " + ex);
                output.Notes.AddE("Ran off end of file while scanning chunk");
                return null;
            }
        }

        /// <summary>
        /// Scans the text blocks in the chunk.
        /// </summary>
        /// <remarks>
        /// This does limited bounds checking.  The caller is expected to catch
        /// IndexOutOfRangeException.  On success, this guarantees that all text blocks fall
        /// within the bounds of the file.
        /// </remarks>
        /// <param name="buf">File data.</param>
        /// <param name="chunk">Chunk information.</param>
        /// <param name="output">FancyText object.</param>
        /// <returns>Total length of the text block section, in bytes, or -1 if the data could
        ///   not be parsed successfully.</returns>
        private int ScanTextBlocks(byte[] buf, Chunk chunk, FancyText output) {
            Debug.WriteLine("Scanning " + chunk.mNumTextBlocks + " text blocks");
            int offset = chunk.mTextBlocksOffset;
            int startOffset = offset;

            chunk.mTextBlockRecords = new TextBlockRecord[chunk.mNumTextBlocks];

            for (int i = 0; i < chunk.mNumTextBlocks; i++) {
                TextBlockRecord rec = new TextBlockRecord();
                rec.mLongLen = RawData.ReadU32LE(buf, ref offset);
                // Must have room for two 16-bit counts, and must fit in a 16-bit word.
                if (rec.mLongLen < 4 || rec.mLongLen > ushort.MaxValue) {
                    output.Notes.AddE("Found invalid text block size: " + rec.mLongLen);
                    return -1;
                }

                rec.mTextOffset = offset;
                rec.mBlockSize = RawData.ReadU16LE(buf, ref offset);
                rec.mBlockUsed = RawData.ReadU16LE(buf, ref offset);
                if (rec.mBlockSize != rec.mLongLen || rec.mBlockUsed != rec.mLongLen) {
                    output.Notes.AddW("Text block sizes don't match: total=" + rec.mLongLen +
                        ", size=" + rec.mBlockSize + ", used=" + rec.mBlockUsed);
                    // keep going
                }

                chunk.mTextBlockRecords[i] = rec;
                offset += (int)rec.mLongLen - 4;
            }

            if (offset > buf.Length) {
                output.Notes.AddE("Text block ran off end of file");
                return -1;
            }

            return offset - startOffset;
        }

        /// <summary>
        /// Formats the contents of the chunk, and prints them in the output object.
        /// </summary>
        /// <param name="buf">File data buffer.</param>
        /// <param name="chunk">Chunk information.</param>
        /// <param name="output">FancyText object.</param>
        private void PrintChunk(byte[] buf, Chunk chunk, FancyText output) {
            // We successfully scanned everything, so we shouldn't run off the end of the file
            // buffer or any of the arrays until we get to the paragraphs.

            // Walk through the SaveArray entries.
            for (int i = 0; i < chunk.mSaveArrayCount; i++) {
                SaveArrayEntry sae = chunk.mSaveArrayEntries[i];
                if (sae.mAttributes == PAGE_BREAK_PARA) {
                    // This is a page-break paragraph.  Just output the format command.
                    PrintEmbedLine("<page-break>", output);
                    continue;
                }

                ushort statusBits = chunk.mRulers[sae.mRulerNum].mStatusBits;
                if ((statusBits & (ushort)Ruler.StatusFlags.FullJustification) != 0) {
                    output.SetJustification(FancyText.Justification.Full);
                } else if ((statusBits & (ushort)Ruler.StatusFlags.RightJustification) != 0) {
                    output.SetJustification(FancyText.Justification.Right);
                } else if ((statusBits & (ushort)Ruler.StatusFlags.CenterJustification) != 0) {
                    output.SetJustification(FancyText.Justification.Center);
                } else if ((statusBits & (ushort)Ruler.StatusFlags.LeftJustification) != 0) {
                    output.SetJustification(FancyText.Justification.Left);
                }

                if ((statusBits & (ushort)Ruler.StatusFlags.SingleSpaced) != 0) {
                    if (!mSingleSpacing) {
                        PrintEmbedLine("<single-spacing>", output);
                        mSingleSpacing = true;
                    }
                } else if ((statusBits & (ushort)Ruler.StatusFlags.DoubleSpaced) != 0) {
                    PrintEmbedLine("<double-spacing>", output);
                    mSingleSpacing = false;
                } else if ((statusBits & (ushort)Ruler.StatusFlags.TripleSpaced) != 0) {
                    PrintEmbedLine("<triple-spacing>", output);
                    mSingleSpacing = false;
                }

                // Get the text block record, and the offset within it.
                Debug.Assert(sae.mTextBlock < chunk.mTextBlockRecords.Length);
                TextBlockRecord tbr = chunk.mTextBlockRecords[sae.mTextBlock];
                PrintParagraph(buf, tbr, sae.mOffset, output);
            }
        }

        private enum SpecialChar {
            FontChange = 0x01,
            StyleChange = 0x02,
            SizeChange = 0x03,
            ColorChange = 0x04,
            PageNumber = 0x05,          // header/footer only?
            Date = 0x06,                // header/footer only?
            Time = 0x07,                // header/footer only?
            Tab = 0x09,
            End = 0x0d,
        }

        private void PrintParagraph(byte[] buf, TextBlockRecord tbr, ushort paraOffset,
                FancyText output) {
            try {
                // Find the start of the paragraph.
                int offset = tbr.mTextOffset + paraOffset;
                ParagraphHeader hdr = new ParagraphHeader();
                hdr.Load(buf, ref offset);
                //Debug.WriteLine("Printing paragraph at offset 0x" + offset.ToString("x4"));

                FancyText.FontFamily? family = GSDocCommon.FindFont(hdr.mFirstFont);
                if (family is not null) {
                    output.SetFontFamily(family, GSDocCommon.GetFontMult(hdr.mFirstFont));
                } else {
                    output.Notes.AddW("Unable to set initial font family $" +
                        hdr.mFirstFont.ToString("x4"));
                }
                output.SetFontSize(hdr.mFirstSize);
                output.SetGSFontStyle(hdr.mFirstStyle);
                if (hdr.mFirstColor < PALETTE_LENGTH) {
                    mCurColor = mColorTable[hdr.mFirstColor];
                    output.SetForeColor(mCurColor);
                    Debug.WriteLine("Set initial color " + hdr.mFirstColor + " #" +
                        mCurColor.ToString("x8"));
                } else {
                    output.Notes.AddW("Bad initial color index " + hdr.mFirstColor);
                    output.SetForeColor(0);
                }

                while (true) {
                    byte bch = buf[offset++];
                    SpecialChar sc = (SpecialChar)bch;
                    switch (sc) {
                        case SpecialChar.End:
                            // End of paragraph.
                            output.NewParagraph();
                            return;
                        case SpecialChar.Tab:
                            output.Tab();
                            break;
                        case SpecialChar.FontChange:
                            ushort newFont = RawData.ReadU16LE(buf, ref offset);
                            family = GSDocCommon.FindFont(newFont);
                            if (family is not null) {
                                output.SetFontFamily(family, GSDocCommon.GetFontMult(newFont));
                            } else {
                                output.Notes.AddW("Unable to change font family to $" +
                                    newFont.ToString("x4"));
                            }
                            break;
                        case SpecialChar.StyleChange:
                            output.SetGSFontStyle(buf[offset++]);
                            break;
                        case SpecialChar.SizeChange:
                            output.SetFontSize(buf[offset++]);
                            break;
                        case SpecialChar.ColorChange:
                            byte newColorIndex = buf[offset++];     // should be 0-15
                            if (newColorIndex < PALETTE_LENGTH) {
                                mCurColor = mColorTable[newColorIndex];
                                output.SetForeColor(mCurColor);
                                Debug.WriteLine("Set color " + newColorIndex + " #" +
                                    mCurColor.ToString("x8"));
                            } else {
                                output.Notes.AddW("Invalid color index " + newColorIndex);
                            }
                            break;
                        case SpecialChar.PageNumber:
                            PrintEmbedString("<page#>", output);
                            break;
                        case SpecialChar.Date:
                            PrintEmbedString("<date>", output);
                            break;
                        case SpecialChar.Time:
                            PrintEmbedString("<time>", output);
                            break;
                        default:
                            // Just a Mac OS Roman character.  We don't want to print raw control
                            // characters into the stream, so use the printable-control form.
                            char outch = MacChar.MacToUnicode(bch, MacChar.Encoding.RomanShowCtrl);
                            output.Append(outch);
                            break;
                    }
                }
            } catch (IndexOutOfRangeException) {
                output.Notes.AddW("Ran off end of file processing paragraph");
                // keep going
            }
        }

        /// <summary>
        /// Prints an embedded format string if configured to do so.
        /// </summary>
        /// <param name="str">String to print.</param>
        /// <param name="output">FancyText object.</param>
        private void PrintEmbedString(string str, FancyText output) {
            if (mShowEmbeds) {
                output.SetForeColor(EMBED_FG_COLOR);
                output.Append(str);
                output.SetForeColor(mCurColor);
            }
        }

        /// <summary>
        /// Prints an embedded format line if configured to do so.  This occupies a full line.
        /// </summary>
        /// <param name="str">String to print.</param>
        /// <param name="output">FancyText object.</param>
        private void PrintEmbedLine(string str, FancyText output) {
            if (mShowEmbeds) {
                output.SetFontFamily(DEFAULT_FONT);
                output.SetFontSize(DEFAULT_FONT_SIZE);
                output.SetForeColor(EMBED_FG_COLOR);
                output.SetGSFontStyle(0);
                output.SetJustification(FancyText.Justification.Center);
                output.Append(str);
                output.SetForeColor(mCurColor);
                output.NewParagraph();
                output.SetJustification(FancyText.Justification.Left);
            }
        }
    }
}
