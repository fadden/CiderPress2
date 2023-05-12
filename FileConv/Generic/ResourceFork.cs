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

namespace FileConv.Generic {
    /// <summary>
    /// Formats a Mac or IIgs resource fork.
    /// </summary>
    /// <remarks>
    /// <para>This is not a subclass of Converter, it's just a utility class.</para>
    /// </remarks>
    public static class ResourceFork {
        /// <summary>
        /// Generates a detailed listing of the contents of the resource fork.
        /// </summary>
        public static IConvOutput FormatResourceFork(ResourceMgr resMgr) {
            SimpleText output = new SimpleText();

            Formatter.FormatConfig fmtConfig = new Formatter.FormatConfig();
            fmtConfig.HexDumpConvFunc = Formatter.CharConv_MOR;
            Formatter fmt = new Formatter(fmtConfig);

            byte[] tmpBuf = new byte[32768];

            int index = 0;
            foreach (ResourceMgr.ResourceEntry entry in resMgr) {
                if (index != 0) {
                    output.AppendLine();
                }
                output.Append("Entry #");
                output.Append(index);
                string entFmt = ": type={0:x4} ({1}), ID={2}, length={3} (${3:x4})";
                if (resMgr.IsMac) {
                    output.AppendFormat(entFmt, entry.Type, entry.TypeStr, (short)entry.ID,
                        entry.Length);
                } else {
                    output.AppendFormat(entFmt, entry.Type, entry.TypeStr, (int)entry.ID,
                        entry.Length);
                }
                if (entry.Name != string.Empty) {
                    output.Append(" \"");
                    output.Append(entry.Name);
                    output.Append('"');
                }
                //output.AppendFormat(" OFFSET={0} ${0:x4}", entry.FileOffset);
                output.AppendLine();

                if (entry.Length > tmpBuf.Length) {
                    tmpBuf = new byte[entry.Length];
                }
                resMgr.GetEntryData(entry, tmpBuf, 0);

                fmt.FormatHexDump(tmpBuf, 0, entry.Length, output.Text);
                index++;
            }

            output.Notes.MergeFrom(resMgr.Notes);

            return output;
        }
    }
}
