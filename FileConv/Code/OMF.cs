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

namespace FileConv.Code {
    public class OMF : Converter {
        public const string TAG = "omf";
        public const string LABEL = "Object Module Format";
        public const string DESCRIPTION =
            "Formats the contents of the segment headers of an OMF file.";
        public const string DISCRIMINATOR =
            "ProDOS S16, EXE, PIF, TIF, NDA, CDA, TOL, DVR, LDF, FST.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int MIN_LEN = OmfFile.MIN_FILE_SIZE;
        private const int MAX_LEN = OmfFile.MAX_FILE_SIZE;


        private OMF() { }

        public OMF(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            return EvaluateApplicability(out bool unused);
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            if (Applic <= Applicability.Not) {
                Debug.Assert(false);
                return new ErrorText("ERROR");
            }
            // Load the entire file into memory.  Even for a large IIgs program this shouldn't
            // be a strain.
            Debug.Assert(DataStream != null);
            byte[] fileBuf = new byte[DataStream.Length];
            DataStream.Position = 0;
            DataStream.ReadExactly(fileBuf, 0, (int)DataStream.Length);

            // Check if this appears to be a library file.  v1 LIB files require different parsing.
            EvaluateApplicability(out bool isLibraryFile);
            OmfFile omfFile = new OmfFile(fileBuf);
            OmfSegment.ParseResult result = omfFile.DoAnalyze(isLibraryFile);
            if (result == OmfSegment.ParseResult.IsLibrary) {
                // Shouldn't happen if we got the file type right.
                Debug.WriteLine("Retrying as library");
                result = omfFile.DoAnalyze(true);
            }
            if (result != OmfSegment.ParseResult.Success) {
                return new ErrorText("Unable to parse as OMF.");
            }

            return FormatFile(omfFile);
        }

        private Applicability EvaluateApplicability(out bool isLibraryFile) {
            isLibraryFile = false;
            if (DataStream == null || DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            Applicability applic = Applicability.Yes;
            switch (FileAttrs.FileType) {
                case FileAttribs.FILE_TYPE_OBJ:
                    // Object file.
                    break;
                case FileAttribs.FILE_TYPE_LIB:
                    // Library file.
                    isLibraryFile = true;
                    break;
                case FileAttribs.FILE_TYPE_RTL:
                    // Run-time library file.  (Shouldn't exist for v1?)
                    break;
                case FileAttribs.FILE_TYPE_S16:
                case FileAttribs.FILE_TYPE_EXE:
                case FileAttribs.FILE_TYPE_PIF:
                case FileAttribs.FILE_TYPE_TIF:
                case FileAttribs.FILE_TYPE_NDA:
                case FileAttribs.FILE_TYPE_CDA:
                case FileAttribs.FILE_TYPE_TOL:
                case FileAttribs.FILE_TYPE_DVR:
                case FileAttribs.FILE_TYPE_LDF:
                case FileAttribs.FILE_TYPE_FST:
                    // Load file.
                    break;
                case FileAttribs.FILE_TYPE_OS:
                    applic = Applicability.ProbablyNot;
                    break;
                default:
                    applic = Applicability.Not;
                    break;
            }
            return applic;
        }

        private static IConvOutput FormatFile(OmfFile omfFile) {
            SimpleText output = new SimpleText();
            string EOL = Environment.NewLine;

            Formatter formatter = new Formatter(new Formatter.FormatConfig());

            output.AppendLine("Object Module Format file has " + omfFile.SegmentList.Count +
                ((omfFile.SegmentList.Count == 1) ? " segment:" : " segments:"));
            foreach (OmfSegment seg in omfFile.SegmentList) {
                output.AppendLine();
                output.AppendFormat("Segment #{0}: {1} {2} \"{3}\"" + EOL,
                    seg.SegNum, OmfSegment.VersionToString(seg.Version),
                    OmfSegment.KindToString(seg.Kind), seg.SegName);
                foreach (OmfSegment.NameValueNote nvn in seg.RawValues) {
                    string value;
                    if (nvn.Value is int) {
                        int byteWidth = nvn.Width;
                        if (byteWidth > 3) {
                            byteWidth = 3;
                        }
                        value = formatter.FormatHexValue((int)nvn.Value, byteWidth * 2);
                    } else {
                        value = '"' + nvn.Value.ToString()! + '"';
                    }
                    output.AppendFormat("  {0,-12} {1,-10} {2}" + EOL,
                        nvn.Name, value, nvn.Note);
                }
            }

            // Add notes generated by OMF parser.
            foreach (string str in omfFile.MessageList) {
                output.Notes.AddI(str);
            }
            return output;
        }
    }
}
