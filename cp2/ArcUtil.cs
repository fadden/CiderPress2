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

using AppCommon;
using CommonUtil;
using DiskArc;
using DiskArc.Arc;
using static DiskArc.Defs;

namespace cp2 {
    internal class ArcUtil {
        /// <summary>
        /// Handles the "create-file-archive" command.
        /// </summary>
        public static bool HandleCreateFileArchive(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 1) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }
            string outFileName = args[0];

            if (Directory.Exists(outFileName)) {
                Console.Error.WriteLine("Cannot create archive: is a directory: '" +
                    outFileName + "'");
                return false;
            }

            // Convert the extension to a format.
            string ext = Path.GetExtension(outFileName);
            if (!FileAnalyzer.ExtensionToKind(ext, out FileKind createKind,
                    out SectorOrder orderHint, out FileKind unused1, out FileKind unused2,
                    out bool okayForCreate)) {
                Console.Error.WriteLine("File archive file extension not recognized");
                return false;
            }
            if (!okayForCreate) {
                Console.Error.WriteLine("Filename extension (" + ext +
                    ") is ambiguous or not supported for file creation");
                return false;
            }
            // Only disk images; need special handling for NuFX ".SDK".
            if (Defs.IsDiskImageFile(createKind)) {
                Console.Error.WriteLine("File extension is not a file archive type");
                return false;
            }

            IArchive archive;
            switch (createKind) {
                case FileKind.AppleSingle:
                case FileKind.GZip:
                case FileKind.MacBinary:
                    Console.Error.WriteLine("Cannot create single-record archive files");
                    return false;
                case FileKind.AppleLink:
                    Console.Error.WriteLine("Not currently supported");
                    return false;
                case FileKind.Binary2:
                    archive = Binary2.CreateArchive(parms.AppHook);
                    break;
                case FileKind.NuFX:
                    archive = NuFX.CreateArchive(parms.AppHook);
                    break;
                case FileKind.Zip:
                    archive = Zip.CreateArchive(parms.AppHook);
                    break;
                default:
                    throw new NotImplementedException("Not handled: " + createKind);
            }

            try {
                using (FileStream imgStream = new FileStream(outFileName, FileMode.CreateNew)) {
                    archive.StartTransaction();
                    archive.CommitTransaction(imgStream);
                }
            } catch (IOException ex) {
                Console.Error.WriteLine("Unable to create archive: " + ex.Message);
                return false;
            }

            return true;
        }
    }
}
