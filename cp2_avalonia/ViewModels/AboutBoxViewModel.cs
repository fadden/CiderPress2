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
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using AppCommon;

using cp2_avalonia.Common;
using cp2_avalonia.Services;

namespace cp2_avalonia.ViewModels;

/// <summary>
/// ViewModel for the About Box dialog.
/// </summary>
public class AboutBoxViewModel : ObservableObject
{
    private const string LEGAL_STUFF_FILE_NAME = "LegalStuff.txt";

    /// <summary>Version string, for display.</summary>
    public string ProgramVersionString => GlobalAppVersion.AppVersion.ToString();

    /// <summary>Operating system version, for display.</summary>
    public string OsPlatform => "OS: " + RuntimeInformation.OSDescription;

    /// <summary>Runtime information, for display.</summary>
    public string RuntimeInfo =>
        "Runtime: " + RuntimeInformation.FrameworkDescription + " / " +
        RuntimeInformation.RuntimeIdentifier +
        (Environment.IsPrivilegedProcess ? " (Admin)" : "");

    /// <summary>Determines whether a message about assertions is visible.</summary>
    public bool IsDebugBuild {
        get {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>Contents of LegalStuff.txt, for display.</summary>
    public string LegalStuffText { get; }

    public event Action<bool>? CloseRequested;

    public IRelayCommand CloseCommand { get; }

    public AboutBoxViewModel()
    {
        // Load legal text before commands are created so the binding finds the value.
        string pathName = Path.Combine(PlatformUtil.GetRuntimeDataDir(), LEGAL_STUFF_FILE_NAME);
        try {
            LegalStuffText = File.ReadAllText(pathName);
        } catch (Exception ex) {
            AppLog.W("About box: failed to load legal text from '" + pathName + "'", ex);
            LegalStuffText = ex.ToString();
        }

        CloseCommand = new AsyncRelayCommand(async () =>
            CloseRequested?.Invoke(true));
    }
}
