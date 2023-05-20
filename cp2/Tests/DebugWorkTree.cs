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
using static DiskArc.Defs;

namespace cp2.Tests {
    public static class DebugWorkTree {
        public static bool HandleDumpTree(string cmdName, string[] args, ParamsBag parms) {
            if (args.Length != 1) {
                CP2Main.ShowUsage(cmdName);
                return false;
            }

            string extArchive = args[0];

            using WorkTree tree = new WorkTree(extArchive,
                delegate(FileKind parentKind, FileKind childKind, string candidateName) {
                        return DepthLimit(parentKind, childKind, candidateName, parms);
                    }, parms.AppHook);
            DumpTreeSummary(tree.RootNode, 0, "", true);

            return true;
        }

        /// <summary>
        /// WorkTree depth limiter function (<see cref="WorkTree.DepthLimiter"/>).
        /// </summary>
        private static bool DepthLimit(FileKind parentKind, FileKind childKind,
                string candidateName, ParamsBag parms) {
            return true;
        }

        private static void DumpTreeSummary(Node node, int depth, string indent,
                bool isLastSib) {
            Console.Write(indent);
            Console.Write("+-");
            Console.WriteLine(node.Label);

            Node[] children = node.Children;
            for (int i = 0; i < children.Length; i++) {
                string newIndent = indent + (isLastSib ? "  " : "| ");
                DumpTreeSummary(children[i], depth + 1, newIndent, i == children.Length - 1);
            }
        }
    }
}
