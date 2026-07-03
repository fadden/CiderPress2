/*
 * Copyright 2023 faddenSoft
 * Copyright 2026 Lydian Scale Software
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

using System.ComponentModel;
using System.Diagnostics;

using AppCommon;
using cp2_avalonia.Services;
using cp2_avalonia.ViewModels;

namespace cp2_avalonia.Actions {
    public static class ProgressUtil {
        public const string FINISHING_MSG = "Saving changes...";

        internal enum Choice {
            Unknown = 0, Unset, All, None
        }
        public class PersistentChoices {
            internal Choice Overwrite { get; set; } = Choice.Unset;
        }

        /// <summary>
        /// Handles a callback from the library code.
        /// </summary>
        /// <param name="what">What happened.</param>
        /// <param name="actionStr">Verb that describes the action being performed.</param>
        /// <param name="choices">Persistent choice values.</param>
        /// <param name="leafNode">Leaf node of tree, if we're making updates.</param>
        /// <param name="bkWorker">Background worker that receives progress updates.</param>
        /// <returns>Result code from user query, if any.</returns>
        public static CallbackFacts.Results HandleCallback(CallbackFacts what, string actionStr,
                PersistentChoices choices, DiskArcNode? leafNode, BackgroundWorker bkWorker) {
            CallbackFacts.Results result = CallbackFacts.Results.Continue;
            switch (what.Reason) {
                case CallbackFacts.Reasons.QueryCancel:
                    if (bkWorker.CancellationPending) {
                        result = CallbackFacts.Results.Cancel;
                    }
                    break;
                case CallbackFacts.Reasons.Progress:
                    if (bkWorker.CancellationPending) {
                        result = CallbackFacts.Results.Cancel;
                        break;
                    }
                    if (what.Part == DiskArc.Defs.FilePart.RsrcFork) {
                        bkWorker.ReportProgress(what.ProgressPercent,
                            what.OrigPathName + " (rsrc)");
                    } else {
                        bkWorker.ReportProgress(what.ProgressPercent, what.OrigPathName);
                    }
                    break;
                case CallbackFacts.Reasons.FileNameExists:
                    leafNode?.FlushStreams();   // flush progress before waiting for user input
                    if (choices.Overwrite == Choice.All) {
                        Debug.WriteLine("Overwrite '" + what.OrigPathName + "' -> overwrite all");
                        return CallbackFacts.Results.Overwrite;
                    } else if (choices.Overwrite == Choice.None) {
                        Debug.WriteLine("Overwrite '" + what.OrigPathName + "' -> overwrite none");
                        return CallbackFacts.Results.Skip;
                    } else {
                        Debug.Assert(choices.Overwrite == Choice.Unset);
                    }
                    WorkProgressViewModel.OverwriteQuery query = new WorkProgressViewModel.OverwriteQuery(what);
                    bkWorker.ReportProgress(0, query);
                    result = query.WaitForResult(out bool useForAll);
                    Debug.WriteLine("Overwrite '" + what.OrigPathName + "' -> " + result +
                        ", all=" + useForAll);
                    if (useForAll) {
                        if (result == CallbackFacts.Results.Overwrite) {
                            choices.Overwrite = Choice.All;
                        } else if (result == CallbackFacts.Results.Skip) {
                            choices.Overwrite = Choice.None;
                        }
                    }
                    break;
                case CallbackFacts.Reasons.ResourceForkIgnored:
                    // TODO: do something with this?
                    break;
                case CallbackFacts.Reasons.PathTooLong:
                case CallbackFacts.Reasons.AttrFailure:
                case CallbackFacts.Reasons.OverwriteFailure:
                case CallbackFacts.Reasons.Failure:
                    AppLog.E(actionStr + ": " + what.Reason + ": " + what.FailMessage);
                    WorkProgressViewModel.MessageBoxQuery failMsg =
                        new WorkProgressViewModel.MessageBoxQuery(what.FailMessage,
                            "Failed", MBButton.OK, MBIcon.Error);
                    bkWorker.ReportProgress(0, failMsg);
                    failMsg.WaitForResult();
                    break;
                case CallbackFacts.Reasons.ConversionFailure:
                    AppLog.E(actionStr + ": conversion failed: " + what.FailMessage);
                    WorkProgressViewModel.MessageBoxQuery cfailMsg =
                        new WorkProgressViewModel.MessageBoxQuery("Conversion failed: " + what.FailMessage,
                            "Failed", MBButton.OK, MBIcon.Error);
                    bkWorker.ReportProgress(0, cfailMsg);
                    cfailMsg.WaitForResult();
                    break;
            }
            return result;
        }

        /// <summary>
        /// Displays a message box with an informative or error message.
        /// </summary>
        public static void ShowMessage(string msg, bool isError, BackgroundWorker bkWorker) {
            if (isError) { AppLog.E(msg); } else { AppLog.I(msg); }
            WorkProgressViewModel.MessageBoxQuery failMsg =
                new WorkProgressViewModel.MessageBoxQuery(msg, isError ? "Failed" : "Info",
                    MBButton.OK,
                    isError ? MBIcon.Error : MBIcon.Information);
            bkWorker.ReportProgress(0, failMsg);
            failMsg.WaitForResult();
        }

        /// <summary>
        /// Informs the user that the action has been canceled.
        /// </summary>
        public static void ShowCancelled(BackgroundWorker bkWorker) {
            ShowMessage("Cancelled.", false, bkWorker);
        }
    }
}
