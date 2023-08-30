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
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

using AppCommon;
using CommonUtil;
using cp2_wpf.WPFCommon;

namespace cp2_wpf.Actions {
    public static class ProgressUtil {
        public const string FINISHING_MSG = "Saving changes...";

        /// <summary>
        /// Handles a callback from the library code.
        /// </summary>
        /// <param name="what">What happened.</param>
        /// <param name="actionStr">Verb that describes the action being performed.</param>
        /// <param name="leafNode">Leaf node of tree, if we're making updates.</param>
        /// <param name="bkWorker">Background worker that receives progress updates.</param>
        /// <returns>Result code from user query, if any.</returns>
        public static CallbackFacts.Results HandleCallback(CallbackFacts what, string actionStr,
                DiskArcNode? leafNode, BackgroundWorker bkWorker) {
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
                    // DEBUG: sleep briefly so we can see the progress
                    //System.Threading.Thread.Sleep(500);
                    break;
                case CallbackFacts.Reasons.FileNameExists:
                    leafNode?.FlushStreams();   // flush progress before waiting for user input

                    string ovwr = "Overwrite '" + what.OrigPathName + "' ?";
                    WorkProgress.MessageBoxQuery query = new WorkProgress.MessageBoxQuery(ovwr,
                        "Overwrite File?", MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);
                    bkWorker.ReportProgress(0, query);
                    MessageBoxResult res = query.WaitForResult();
                    Debug.WriteLine("Overwrite '" + what.OrigPathName + "' -> " + res);
                    switch (res) {
                        case MessageBoxResult.Cancel:
                            result = CallbackFacts.Results.Cancel;
                            break;
                        case MessageBoxResult.OK:
                        case MessageBoxResult.Yes:
                            result = CallbackFacts.Results.Overwrite;
                            break;
                        case MessageBoxResult.No:
                        default:
                            result = CallbackFacts.Results.Skip;
                            break;
                    }
                    break;
                case CallbackFacts.Reasons.ResourceForkIgnored:
                    // TODO: do something with this?
                    break;
                case CallbackFacts.Reasons.PathTooLong:
                case CallbackFacts.Reasons.AttrFailure:
                case CallbackFacts.Reasons.OverwriteFailure:
                case CallbackFacts.Reasons.Failure:
                    WorkProgress.MessageBoxQuery failMsg =
                        new WorkProgress.MessageBoxQuery(what.FailMessage,
                            "Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    bkWorker.ReportProgress(0, failMsg);
                    failMsg.WaitForResult();
                    break;
                case CallbackFacts.Reasons.ConversionFailure:
                    WorkProgress.MessageBoxQuery cfailMsg =
                        new WorkProgress.MessageBoxQuery("Conversion failed: " + what.FailMessage,
                            "Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    bkWorker.ReportProgress(0, cfailMsg);
                    cfailMsg.WaitForResult();
                    break;
            }
            return result;
        }

        /// <summary>
        /// Displays a message box with an informative or error message.
        /// </summary>
        /// <param name="msg">Message to show.</param>
        /// <param name="isError">True if this is an error message.</param>
        /// <param name="bkWorker">Background worker that receives progress updates.</param>
        public static void ShowMessage(string msg, bool isError, BackgroundWorker bkWorker) {
            WorkProgress.MessageBoxQuery failMsg =
                new WorkProgress.MessageBoxQuery(msg, isError ? "Failed" : "Info",
                    MessageBoxButton.OK,
                    isError ? MessageBoxImage.Error : MessageBoxImage.Information);
            bkWorker.ReportProgress(0, failMsg);
            failMsg.WaitForResult();
        }

        /// <summary>
        /// Informs the user that the action has been cancelled.
        /// </summary>
        /// <param name="bkWorker">Background worker that receives progress updates.</param>
        public static void ShowCancelled(BackgroundWorker bkWorker) {
            ShowMessage("Cancelled.", false, bkWorker);
        }
    }
}
