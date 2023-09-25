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
    public class AppleWorksDB : Converter {
        public const string TAG = "adb";
        public const string LABEL = "AppleWorks DB";
        public const string DESCRIPTION =
            "Converts an AppleWorks Data Base document to CSV.";
        public const string DISCRIMINATOR = "ProDOS ADB.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;

        private const int HEADER_FIXED_LEN = 379;
        private const int REPORT_LEN = 600;
        private const int FIRST_CAT_OFFSET = 357;
        private const int MAX_CAT_NAME_LEN = 20;
        private const int CAT_HEADER_LEN = 22;      // category name + 1 bonus byte
        private const int MAX_CATEGORIES = 30;
        private const byte DATE_ENTRY = 0xc0;
        private const int DATE_LEN = 6;
        private const byte TIME_ENTRY = 0xd4;
        private const int TIME_LEN = 4;

        private const int MIN_LEN = HEADER_FIXED_LEN + 2;
        private const int MAX_LEN = 256 * 1024;     // arbitrary


        private AppleWorksDB() { }

        public AppleWorksDB(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }

        protected override Applicability TestApplicability() {
            if (DataStream == null || DataStream.Length < MIN_LEN || DataStream.Length > MAX_LEN) {
                return Applicability.Not;
            }
            if (FileAttrs.FileType == FileAttribs.FILE_TYPE_ADB) {
                return Applicability.Yes;
            }
            return Applicability.Not;
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

            ushort headerLen = RawData.GetU16LE(fileBuf, 0);
            if (headerLen < HEADER_FIXED_LEN || headerLen >= DataStream.Length) {
                return new ErrorText("Invalid header length: " + headerLen);
            }

            CellGrid output = new CellGrid();

            // Offset +35: number of categories per record (1-30)
            byte numCats = fileBuf[35];
            if (numCats == 0 || numCats > MAX_CATEGORIES) {
                output.Notes.AddW("Invalid number of categories per record: " + numCats);
            }
            if (FIRST_CAT_OFFSET + CAT_HEADER_LEN * numCats > DataStream.Length) {
                output.Notes.AddE("File too short to hold " + numCats + " categories");
                return output;
            }

            // Offset +36-37: number of records in file; v3.0 uses the high bit as a signal.
            ushort numRecs = (ushort)(RawData.GetU16LE(fileBuf, 36) & 0x7fff);

            // Offset +38: number of reports in file (0-8, or 0-20 for v3.0).
            byte numReports = fileBuf[38];

            // Output the category names as the first record.
            int nameOffset = FIRST_CAT_OFFSET;
            for (int i = 0; i < numCats; i++) {
                string name;
                byte nameLen = fileBuf[nameOffset];
                if (nameLen == 0 || nameLen > MAX_CAT_NAME_LEN) {
                    output.Notes.AddE("Invalid category name length: " + nameLen);
                    name = "#INVALID#";
                } else {
                    name = GetString(fileBuf, nameOffset + 1, nameLen);
                }
                output.SetCellValue(i, 0, name);
                nameOffset += CAT_HEADER_LEN;
            }

            if (numReports != 0) {
                output.Notes.AddI("Number of reports found: " + numReports);
            }

            // Find the start of the records.
            int recOffset = FIRST_CAT_OFFSET + numCats * CAT_HEADER_LEN + numReports * REPORT_LEN;
            if (recOffset >= DataStream.Length) {
                output.Notes.AddE("File truncated after header");
                return output;
            }

            // The "standard values" entry is not counted in numRecs, so start count at -1.
            for (int rec = -1; rec < numRecs; rec++) {
                ushort recordRem = RawData.GetU16LE(fileBuf, recOffset);
                if (recOffset + 2 + recordRem > DataStream.Length) {
                    output.Notes.AddE("File truncated in record");
                    return output;
                }
                if (fileBuf[recOffset + 2 + recordRem - 1] != 0xff) {
                    output.Notes.AddW("Record " + rec + " does not have $ff at end");
                }

                // Process the record, skipping over the "standard values" entry.
                if (rec >= 0) {
                    ProcessRecord(rec, fileBuf, recOffset, recordRem, output);
                }

                recOffset += recordRem + 2;
            }

            // Confirm the presence of the end marker.
            ushort endMarker = RawData.GetU16LE(fileBuf, recOffset);
            if (endMarker != 0xffff) {
                output.Notes.AddW("Did not find end marker at expected location");
            }

            DumpTags(fileBuf, recOffset + 2, output);

            return output;
        }

        /// <summary>
        /// Processes a single database record.
        /// </summary>
        private static void ProcessRecord(int recNum, byte[] recordBuf, int offset,
                ushort recordLen, CellGrid output) {
            int startOffset = offset;

            offset += 2;        // skip "record remaining bytes" value
            try {
                // Walk through the variable-length data, populating each category for this record.
                int catNum = 0;
                while (offset - startOffset < recordLen) {
                    byte ctrl = recordBuf[offset++];
                    if (ctrl == 0xff) {
                        // End of record.
                        break;
                    } else if (ctrl >= 0x01 && ctrl <= 0x7f) {
                        // Data for current category; ctrl value is byte count (1-127).
                        if (recordBuf[offset] == DATE_ENTRY && ctrl == DATE_LEN) {
                            output.SetCellValue(catNum, recNum + 1,
                                ParseDate(recordBuf, ref offset));
                        } else if (recordBuf[offset] == TIME_ENTRY && ctrl == TIME_LEN) {
                            output.SetCellValue(catNum, recNum + 1,
                                ParseTime(recordBuf, ref offset));
                        } else {
                            offset--;   // back up so control byte is at the start
                            output.SetCellValue(catNum, recNum + 1,
                                ParseString(recordBuf, ref offset));
                        }
                    } else if (ctrl >= 0x81 && ctrl <= 0x9e) {
                        // Ctrl value minus $80 is number of categories to skip (1-30).  The
                        // count includes the category that this control value would be part of,
                        // so we need to subtract one.
                        catNum += ctrl - 0x80 - 1;
                    } else {
                        // Invalid value.
                        output.Notes.AddW("Found invalid ctrl value $" + ctrl.ToString("x2") +
                            " in record " + recNum);
                        return;
                    }

                    catNum++;
                }
            } catch (IndexOutOfRangeException) {
                output.Notes.AddE("Overran record " + recNum);
            }
        }

        private static readonly string[] sMonths = new string[] {
            "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
        };

        /// <summary>
        /// Parses a date record entry.
        /// </summary>
        /// <remarks>
        /// <para>This can be "01-Jan-70", "01-Jan", or "Jan-70".  Neither Excel nor Google Sheets
        /// seems to care whether there's a hyphen or a space.  Sheets doesn't like "Jan-70", but
        /// Excel just treats it as the first day of the month.</para>
        /// <para>We can try to help by converting dates to 4-digit format, perhaps by assuming
        /// that anything less than 70 is 20xx rather than 19xx, but it might be best to just
        /// pass it through unmodified so the user can apply an appropriate transform.</para>
        /// </remarks>
        private static string ParseDate(byte[] buf, ref int offset) {
            Debug.Assert(buf[offset] == DATE_ENTRY);
            offset++;
            char yearH = (char)buf[offset++];
            char yearL = (char)buf[offset++];
            int month = buf[offset++] - 'A';
            char dayH = (char)buf[offset++];
            char dayL = (char)buf[offset++];
            if (dayH == ' ') {
                dayH = '0';     // change " 3-Jan-70" to "03-Jan-70"
            }
            if (month < 0 || month > 12) {
                return "#BAD DATE#";
            }
            StringBuilder sb = new StringBuilder();
            if (dayH != '0' || dayL != '0') {
                // Day is nonzero, include it.
                sb.Append(dayH);
                sb.Append(dayL);
                //sb.Append('-');
                sb.Append(' ');
            }
            sb.Append(sMonths[month]);
            if (yearH != '0' || yearL != '0') {
                // Year is nonzero, include it.  There is no way to encode the year 2000.
                //sb.Append('-');
                sb.Append(' ');
                sb.Append(yearH);
                sb.Append(yearL);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses a time record entry.  The file format stores them as 24-hour clock values, but
        /// AppleWorks displays them as AM/PM, so we do that here.
        /// </summary>
        private static string ParseTime(byte[] buf, ref int offset) {
            Debug.Assert(buf[offset] == TIME_ENTRY);
            offset++;
            int hour = buf[offset++] - 'A';
            char minuteH = (char)buf[offset++];
            char minuteL = (char)buf[offset++];
            if (minuteH < '0' || minuteH > '9' && minuteL < '0' && minuteL > '9' ||
                    hour < 0 || hour > 23) {
                return "#BAD TIME";
            }
            int minute = (minuteH - '0') * 10 + (minuteL - '0');
            string ampm = (hour < 12) ? "AM" : "PM";
            // Convert to 12-hour clock.
            if (hour >= 12) {
                hour -= 12;
            }
            if (hour == 0) {
                hour = 12;
            }
            return string.Format("{0:D}:{1:D2} {2}", hour, minute, ampm);
        }

        /// <summary>
        /// Parses a string with a preceding length byte.
        /// </summary>
        /// <param name="buf">Data buffer.</param>
        /// <param name="offset">Offset of length byte in data buffer.  Will be updated to point
        ///   to the first byte past the end of the string.</param>
        /// <returns>String with converted data.</returns>
        public static string ParseString(byte[] buf, ref int offset) {
            byte strLen = buf[offset++];
            string str = GetString(buf, offset, strLen);
            offset += strLen;
            return str;
        }

        #region Common

        public static char GetChar(byte[] buf, int offset) {
            return (char)(buf[offset]);
        }

        public static string GetString(byte[] buf, int offset, int strLen) {
            // TODO: handle inverse and MouseText
            return Encoding.ASCII.GetString(buf, offset, strLen);
        }

        public static void DumpTags(byte[] fileBuf, int offset, IConvOutput output) {
            if (offset + 4 >= fileBuf.Length) {
                // No tags.
                return;
            }
            output.Notes.AddI("Found tags (" + (fileBuf.Length - offset) + " bytes)");
            // I have yet to find a file that actually has tags, so I'm not doing anything
            // with this yet.
        }

        #endregion Common
    }
}
