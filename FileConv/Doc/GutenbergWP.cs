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

namespace FileConv.Doc {
    public class GutenbergWP : Converter {
        public const string TAG = "guten";
        public const string LABEL = "Gutenberg word processor";
        public const string DESCRIPTION = "Converts a Gutenberg word processor document to text. " +
            "Arbitrary characters are output for 'alternate' character values.";
        public const string DISCRIMINATOR = "Gutenberg filesystem TXT.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;


        private GutenbergWP() { }

        public GutenbergWP(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream is not DiskArc.FS.Gutenberg_FileDesc) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType != FileAttribs.FILE_TYPE_TXT) {
                return Applicability.Not;
            }
            return Applicability.Yes;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
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

            // Not currently doing much with this.
            return ConvertDocument(fileBuf);
        }

        private const int CHUNK_LEN = 250;      // usable bytes per sector

        /// <summary>
        /// Converts a Gutenberg text document.
        /// </summary>
        /// <remarks>
        /// <para>This is mostly a place-holder.  We convert the characters but don't attempt to
        /// process the formatting.</para>
        /// </remarks>
        private static IConvOutput ConvertDocument(byte[] fileBuf) {
            SimpleText output = new SimpleText();
            for (int i = 0; i < fileBuf.Length; i++) {
                // Gutenberg uses high ASCII for the "standard" character set, including control
                // codes.  Byte with the high bit clear are "alternate" characters.  The value $00
                // indicates end of file.
                //
                // An incorrect but straightforward way to handle this is by inverting the high
                // bit and mapping the values to Mac OS Roman or ISO-8859-1.  That allows us to
                // show the alternate characters as unique values.  ISO-8859-1 has no glyphs for
                // the C1 control range (0x80-0x9f), so we use MOR.
                char ch = (char)(fileBuf[i] ^ 0x80);
                if (ch == 0x80) {
                    if (fileBuf.Length - i >= CHUNK_LEN) {
                        output.Notes.AddI("Output ended early: found $00 at offset " + i);
                    }
                    return output;
                } else if (ch == '\r') {
                    output.AppendLine();
                } else if (ch < 0x20) {
                    output.Append(ASCIIUtil.MakePrintable(ch));
                } else {
                    //output.Append(ch);
                    output.Append(MacChar.MacToUnicode((byte)ch, MacChar.Encoding.RomanShowCtrl));
                }
            }
            return output;
        }
    }
}
