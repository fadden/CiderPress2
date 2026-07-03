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
using System.Text;

using CommonUtil;
using DiskArc.FS;
using DiskArc;

using CommunityToolkit.Mvvm.ComponentModel;

using static DiskArc.Defs;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the status bar at the bottom of the main window.
/// Owns CenterText (entry counts / messages) and RightText (future use).
/// </summary>
public class StatusBarViewModel : ObservableObject {
    private string mCenterText = string.Empty;
    public string CenterText {
        get => mCenterText;
        set => SetProperty(ref mCenterText, value);
    }

    private string mRightText = string.Empty;
    public string RightText {
        get => mRightText;
        set => SetProperty(ref mRightText, value);
    }

    /// <summary>
    /// Formats and sets the center status text to show directory count,
    /// file count, and (for IFileSystem) free-space information.
    /// Called by MainViewModel after file list population.
    /// </summary>
    public void SetEntryCounts(IFileSystem? fs, int dirCount, int fileCount,
            Formatter formatter) {
        StringBuilder sb = new StringBuilder();
        sb.Append(fileCount);
        if (fileCount == 1) {
            sb.Append(" file, ");
        } else {
            sb.Append(" files, ");
        }
        sb.Append(dirCount);
        if (dirCount == 1) {
            sb.Append(" directory");
        } else {
            sb.Append(" directories");
        }
        if (fs != null) {
            int baseUnit;
            if (fs is DOS || fs is RDOS || fs is Gutenberg) {
                baseUnit = SECTOR_SIZE;
            } else if (fs is ProDOS || fs is Pascal) {
                baseUnit = BLOCK_SIZE;
            } else {    // HFS, MFS, CP/M
                baseUnit = KBLOCK_SIZE;
            }
            sb.Append(", ");
            sb.Append(formatter.FormatSizeOnDisk(fs.FreeSpace, baseUnit));
            sb.Append(" free");
        }
        CenterText = sb.ToString();
    }

    /// <summary>
    /// Clears the entry-count portion of the status bar.
    /// Called when the workspace is closed or the archive tree selection is cleared.
    /// </summary>
    public void ClearEntryCounts() {
        CenterText = string.Empty;
    }
}
