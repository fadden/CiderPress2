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
using static AppCommon.WorkTree;

namespace cp2.Tests {
    public static class DebugWorkTree {
        public static bool HandleDumpTree(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 1) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string extArchive = args[0];

            // Include the config parameters in the closure.
            DepthLimiter limiter =
                delegate (DepthParentKind parentKind, DepthChildKind childKind) {
                    return DepthLimit(parentKind, childKind, parms.Depth);
            };
            using (WorkTree tree = new WorkTree(extArchive, limiter, true, null, parms.AppHook)) {
                Console.Write(WorkTree.GenerateTreeSummary(tree));
            }

            return true;
        }

        /// <summary>
        /// WorkTree depth limiter function (<see cref="WorkTree.DepthLimiter"/>).  This mimics
        /// the behavior of "catalog".
        /// </summary>
        private static bool DepthLimit(DepthParentKind parentKind, DepthChildKind childKind,
                ParamsBag.ScanDepth depth) {
            if (depth == ParamsBag.ScanDepth.Max) {
                // Always descend.
                return true;
            } else if (depth == ParamsBag.ScanDepth.Shallow) {
                // We always descend into gzip, because gzip isn't very interesting for us,
                // and we want to treat .SDK generally like a disk image.  Otherwise, never
                // descend.
                if (childKind == DepthChildKind.AnyFile) {
                    return (parentKind == DepthParentKind.GZip ||
                            parentKind == DepthParentKind.NuFX);
                }
                if (parentKind == DepthParentKind.GZip ||
                        (parentKind == DepthParentKind.NuFX &&
                         childKind == DepthChildKind.DiskPart)) {
                    return true;
                }
            } else {
                // Depth is "SubVol".  Explore ZIP, multi-part, .SDK, and embeds.  Don't examine
                // any files in a filesystem.
                if (childKind == DepthChildKind.AnyFile) {
                    return (parentKind != DepthParentKind.FileSystem);
                }
                switch (parentKind) {
                    case DepthParentKind.GZip:
                        return true;
                    case DepthParentKind.Zip:
                        // Descend into disk images, but don't open archives.
                        if (childKind == DepthChildKind.DiskImage) {
                            return true;
                        }
                        break;
                    case DepthParentKind.NuFX:
                        // Descend into .SDK, but don't otherwise open the contents.
                        if (parentKind == DepthParentKind.NuFX &&
                                childKind == DepthChildKind.DiskPart) {
                            return true;
                        }
                        break;
                    case DepthParentKind.FileSystem:
                        // Descend into embedded volumes, otherwise stop.
                        if (childKind == DepthChildKind.Embed) {
                            return true;
                        }
                        break;
                    case DepthParentKind.MultiPart:
                        return true;
                    default:
                        Debug.Assert(false, "Unhandled case: " + parentKind);
                        break;
                }
            }
            return false;
        }
    }
}
