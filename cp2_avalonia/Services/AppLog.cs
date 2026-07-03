/*
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
using System;
using System.Diagnostics;

using cp2_avalonia.Models;

namespace cp2_avalonia.Services {
    /// <summary>
    /// <para>Application-wide logging facade.  Routes warnings and errors to the shared
    /// <see cref="DebugMessageLog"/> instance, which is the same one displayed by the
    /// DEBUG &gt; Show Debug Log dialog.  This lets code anywhere in the application
    /// surface exceptions and common errors to the user-visible log without having to
    /// thread an <c>AppHook</c> reference through every constructor.</para>
    ///
    /// <para>Every method also emits to <see cref="Debug.WriteLine(string)"/>, so output
    /// remains visible to an attached debugger exactly as before.  Before the log has been
    /// attached (very early startup), messages still reach the debugger; they simply are
    /// not retained for the dialog.</para>
    ///
    /// <para>All methods are safe to call from any thread; <see cref="DebugMessageLog"/>
    /// marshals its change notification to the UI thread.</para>
    /// </summary>
    public static class AppLog {
        private static DebugMessageLog? sLog;

        /// <summary>
        /// Attaches the shared debug log.  Called once, when the log is created.
        /// </summary>
        public static void Initialize(DebugMessageLog log) {
            sLog = log;
        }

        /// <summary>Logs a low-level diagnostic (debug) message.</summary>
        public static void D(string msg) {
            Debug.WriteLine("MsgD: " + msg);
            sLog?.LogD(msg);
        }

        /// <summary>Logs a low-level diagnostic (debug) message with exception details.</summary>
        public static void D(string msg, Exception ex) {
            D(msg + ": " + Describe(ex));
        }

        /// <summary>Logs an informational message.</summary>
        public static void I(string msg) {
            Debug.WriteLine("MsgI: " + msg);
            sLog?.LogI(msg);
        }

        /// <summary>Logs a warning.</summary>
        public static void W(string msg) {
            Debug.WriteLine("MsgW: " + msg);
            sLog?.LogW(msg);
        }

        /// <summary>Logs a warning with the details of an exception.</summary>
        public static void W(string msg, Exception ex) {
            W(msg + ": " + Describe(ex));
        }

        /// <summary>Logs an error.</summary>
        public static void E(string msg) {
            Debug.WriteLine("MsgE: " + msg);
            sLog?.LogE(msg);
        }

        /// <summary>Logs an error with the details of an exception.</summary>
        public static void E(string msg, Exception ex) {
            E(msg + ": " + Describe(ex));
        }

        /// <summary>
        /// Produces a one-line description of an exception (type and message).
        /// </summary>
        private static string Describe(Exception ex) {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
