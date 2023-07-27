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
using System.Text;

using CommonUtil;
using DiskArc;
using DiskArc.FS;
using static FileConv.Converter;

namespace FileConv.Generic {
    /// <summary>
    /// Imports a text file, converting the end-of-line markers and character set.
    /// </summary>
    public class PlainTextImport : Importer {
        public const string TAG = "text";
        public const string LABEL = "Plain Text";
        public const string DESCRIPTION =
            "Converts a text document for use on an Apple II or vintage Macintosh.  " +
            "End-of-line markers are converted to carriage returns.\n\n" +
            "The handling of non-ASCII characters is configurable, by specifying the host " +
            "file format and the destination file character encoding.  Files imported to DOS " +
            "disks will be stored as high ASCII.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;

        public const string OPT_INCHAR = "inchar";
        public const string OPT_OUTCHAR = "outchar";

        public override List<OptionDefinition> OptionDefs { get; protected set; } =
            new List<OptionDefinition>() {
                new OptionDefinition(OPT_INCHAR, "Host file encoding",
                    OptionDefinition.OptType.Multi, "utf8",
                    ConvUtil.ImportCharSrcTags, ConvUtil.ImportCharSrcDescrs),
                new OptionDefinition(OPT_OUTCHAR, "Output file encoding",
                    OptionDefinition.OptType.Multi, "ascii",
                    ConvUtil.ImportCharDstTags, ConvUtil.ImportCharDstDescrs),
    };

        private const string TXT_EXT = ".txt";


        private PlainTextImport() { }

        public PlainTextImport(AppHook appHook) : base(appHook) {
            HasDataFork = true;
            HasRsrcFork = false;
        }

        public override Applicability TestApplicability(string fileName) {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext == ".txt") {
                return Applicability.Yes;
            } else {
                return Applicability.Maybe;
            }
        }

        public override string StripExtension(string fullPath) {
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || fullPath.Length == ext.Length) {
                return fullPath;        // no extension, or nothing but extension
            }
            if (ext != TXT_EXT) {
                return fullPath;
            }
            return fullPath.Substring(0, fullPath.Length - ext.Length);
        }

        public override void GetFileTypes(out byte proType, out ushort proAux,
                out uint hfsFileType, out uint hfsCreator) {
            hfsFileType = FileAttribs.TYPE_TEXT;
            hfsCreator = FileAttribs.CREATOR_CPII;
            proType = FileAttribs.FILE_TYPE_TXT;
            proAux = 0x0000;
        }

        public override void ConvertFile(Stream inStream, Dictionary<string, string> options,
                Stream? dataOutStream, Stream? rsrcOutStream) {
            if (dataOutStream == null) {
                return;
            }

            string charConvStr = GetStringOption(options, OPT_INCHAR, ConvUtil.CHAR_MODE_UTF8);
            if (!ConvUtil.TryParseImportCharSrc(charConvStr, out ConvUtil.ImportCharSrc srcMode)) {
                // Unknown value, just use UTF-8.
                srcMode = ConvUtil.ImportCharSrc.UTF8;
            }
            charConvStr = GetStringOption(options, OPT_OUTCHAR, ConvUtil.CHAR_MODE_ASCII);
            if (!ConvUtil.TryParseImportCharDst(charConvStr, out ConvUtil.ImportCharDst dstMode)) {
                // Unknown value, just use UTF-8.
                dstMode = ConvUtil.ImportCharDst.ASCII;
            }
            if (dstMode == ConvUtil.ImportCharDst.ASCII && dataOutStream is DOS_FileDesc) {
                mAppHook!.LogI("Switching output mode from ASCII to HighASCII");
                dstMode = ConvUtil.ImportCharDst.HighASCII;
            }

            Encoding srcEnc;
            switch (srcMode) {
                case ConvUtil.ImportCharSrc.CP1252:
                    srcEnc = Encoding.GetEncoding(1252);
                    break;
                case ConvUtil.ImportCharSrc.Latin:
                    srcEnc = Encoding.GetEncoding("iso-8859-1");
                    break;
                case ConvUtil.ImportCharSrc.UTF8:
                default:
                    srcEnc = Encoding.GetEncoding("utf-8");
                    break;
            }

            // Not sure what checking for a byte-order mark will do if one of the other encodings
            // is specified.  If it's present we want it removed.  It shouldn't appear by
            // accident in a file that's being converted as text.
            bool checkBOM = (srcMode == ConvUtil.ImportCharSrc.UTF8);
            const char CR = '\r';
            const char LF = '\n';
            int asciiOr = (dstMode == ConvUtil.ImportCharDst.HighASCII ? 0x80 : 0x00);

            using (StreamReader reader = new StreamReader(inStream, srcEnc, checkBOM, -1, true)) {
                // Read characters from the source, using the specified encoding.  EOL markers
                // are always converted to CR, since that's what's expected for DOS, ProDOS,
                // and old HFS text.
                int ich;
                bool lastWasCR = false;
                while ((ich = reader.Read()) >= 0) {
                    if (ich == CR) {
                        dataOutStream.WriteByte((byte)(CR | asciiOr));
                    } else if (ich == LF) {
                        if (lastWasCR) {
                            // Found second half of CRLF, do nothing.
                        } else {
                            // Found solo LF, convert it to CR.
                            dataOutStream.WriteByte((byte)(CR | asciiOr));
                        }
                    } else {
                        switch (dstMode) {
                            case ConvUtil.ImportCharDst.ASCII:
                            case ConvUtil.ImportCharDst.HighASCII:
                                if (ich >= 0 && ich <= 0x7f) {
                                    dataOutStream.WriteByte((byte)(ich | asciiOr));
                                } else {
                                    char ch = ASCIIUtil.ReduceToASCII((char)ich, '?');
                                    dataOutStream.WriteByte((byte)(ch | asciiOr));
                                }
                                break;
                            case ConvUtil.ImportCharDst.MacOSRoman:
                                byte val = MacChar.UnicodeToMac((char)ich, (byte)'?',
                                    MacChar.Encoding.Roman);
                                dataOutStream.WriteByte(val);
                                break;
                            default:
                                throw new NotImplementedException();
                        }
                    }
                    lastWasCR = (ich == CR);
                }
            }
        }
    }
}
