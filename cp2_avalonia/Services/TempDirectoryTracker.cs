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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace cp2_avalonia.Services;

/// <summary>
/// Tracks temporary directories that should live for the lifetime of the app
/// and be deleted on shutdown.
/// </summary>
internal static class TempDirectoryTracker
{
    private static readonly object sLock = new();
    private static readonly HashSet<string> sTrackedDirs = new(System.StringComparer.Ordinal);

    public static void Track(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        lock (sLock)
        {
            sTrackedDirs.Add(path);
        }
    }

    public static void CleanupAll()
    {
        string[] dirs;
        lock (sLock)
        {
            dirs = new string[sTrackedDirs.Count];
            sTrackedDirs.CopyTo(dirs);
            sTrackedDirs.Clear();
        }

        foreach (string dir in dirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (System.Exception ex)
            {
                AppLog.W("Temp directory cleanup failed", ex);
            }
        }
    }
}
