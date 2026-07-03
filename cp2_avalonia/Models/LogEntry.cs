/*
 * Copyright 2026 faddenSoft
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

using CommonUtil;

namespace cp2_avalonia.Models;

/// <summary>
/// Wrapper for <see cref="DebugMessageLog.LogEntry"/> that is visible to AXAML.
/// </summary>
public class LogEntry(DebugMessageLog.LogEntry entry)
{
    private static readonly string[] sSingleLetter = ["V", "D", "I", "W", "E", "S"];

    public int Index { get; } = entry.Index;
    public DateTime When { get; } = entry.When;
    /// <summary>Numeric priority level, used for filtering.</summary>
    public MessageLog.Priority Level { get; } = entry.Priority;
    /// <summary>Single-letter priority for display.</summary>
    public string Priority { get; } = sSingleLetter[(int)entry.Priority];
    public string Message { get; } = entry.Message;
}
