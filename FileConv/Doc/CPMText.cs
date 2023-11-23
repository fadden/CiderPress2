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
using FileConv.Generic;

namespace FileConv.Doc {
    public class CPMText : Converter {
        public const string TAG = "cpmtext";
        public const string LABEL = "CP/M Text File";
        public const string DESCRIPTION =
            "Converts a CP/M text file, which stops at the first Ctrl+Z, to plain text.  The " +
            "trick is identifying which files are text-only.";
        public const string DISCRIMINATOR = "any CP/M file.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int MAX_LEN = 1 * 1024 * 1024;        // arbitrary 1MB limit
        private const byte CTRL_Z = 0x1a;


        private CPMText() { }

        public CPMText(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream is not DiskArc.FS.CPM_FileDesc) {
                return Applicability.Not;
            }
            if (DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            // Should we check the filename extension?  We can trivially eliminate ".COM" and
            // might want to whitelist ".DOC".
            if (!LooksLikeCPMText(DataStream)) {
                return Applicability.Not;
            }
            return Applicability.Probably;
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            Debug.Assert(DataStream != null);
            SimpleText output = new SimpleText((int)DataStream.Length);
            PlainText.ConvertStream(DataStream, ConvUtil.ExportCharSrc.Latin, true,
                CTRL_Z, output.Text);
            return output;
        }

        // This table was copied from the original CiderPress.
        /*
         * Table determining what's a binary character and what isn't.  This is
         * roughly the same table as is used in GenericArchive.cpp.  The code will
         * additionally allow Ctrl-Z, and will allow occurrences of 0x00 that appear
         * after the Ctrl-Z.
         *
         * Even if we don't allow high ASCII, we must still allow 0xe5 if it occurs
         * after a Ctrl-Z.
         *
         * After looking at the generic ISO-latin-1 table, Paul Schlyter writes:
         * -----
         * Remove 88, 89, 8A, 8C and 8D as well from this table.  The CP/M version of
         * Wordstar uses the hi bit of any character for its own uses - for instance
         * 0D 0A is a "soft end-of-line" which Wordstar can move around, while 8D 8A is
         * a "hard end-of-line" which WordStar does not move around.  Other characters
         * can have this bit used to signal hilighted text.  On a lot of CP/M systems
         * the hi bit is ignored when displaying characters (= sending the characters to
         * the standard console output), thus one can often "type" a WordStar file and
         * have it displayed as readable text.
         * -----
         */
        private static int[] sIsBinary = new int[256] {
            1, 1, 1, 1, 1, 1, 1, 1,  0, 0, 0, 1, 0, 0, 1, 1,    /* ^@-^O */
            1, 1, 1, 1, 1, 1, 1, 1,  1, 1, 1, 1, 1, 1, 1, 1,    /* ^P-^_ */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /*   - / */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* 0 - ? */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* @ - O */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* P - _ */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* ` - o */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 1,    /* p - DEL */
            1, 1, 1, 1, 1, 1, 1, 1,  0, 0, 0, 1, 0, 0, 1, 1,    /* 0x80 */
            1, 1, 1, 1, 1, 1, 1, 1,  1, 1, 1, 1, 1, 1, 1, 1,    /* 0x90 */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* 0xa0 */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* 0xb0 */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* 0xc0 */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* 0xd0 */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* 0xe0 */
            0, 0, 0, 0, 0, 0, 0, 0,  0, 0, 0, 0, 0, 0, 0, 0,    /* 0xf0 */
        };

        /// <summary>
        /// Determines whether the data stream appears to be purely text.
        /// </summary>
        private static bool LooksLikeCPMText(Stream dataStream) {
            bool foundCtrlZ = false;

            // Scan file, looking for illegal chars.
            dataStream.Position = 0;
            byte[] tmpBuf = new byte[4096];
            while (true) {
                int actual = dataStream.Read(tmpBuf, 0, tmpBuf.Length);
                if (actual == 0) {
                    break;
                }
                for (int i = 0; i < actual; i++) {
                    byte bval = tmpBuf[i];
                    if (bval == CTRL_Z) {
                        foundCtrlZ = true;
                    } else if (bval == 0x00 && foundCtrlZ) {
                        // This is fine, since it comes after the EOF marker.
                    } else if (sIsBinary[bval] != 0) {
                        // This doesn't appear to be text.
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
