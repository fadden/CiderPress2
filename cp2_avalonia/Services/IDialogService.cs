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

namespace cp2_avalonia.Services;

using System;
using System.Threading.Tasks;
using Avalonia.Controls;

public enum MBButton { OK, OKCancel, YesNo, YesNoCancel }
public enum MBIcon { None, Information, Warning, Error, Question }
public enum MBResult { OK, Cancel, Yes, No }

public interface IDialogService
{
    /// <summary>Registers a ViewModel→View pair for ShowDialogAsync/ShowModeless.</summary>
    void Register<TViewModel, TView>() where TView : Window, new();

    /// <summary>Shows a modal dialog. Returns true if accepted, false/null otherwise.</summary>
    Task<bool?> ShowDialogAsync<TViewModel>(TViewModel viewModel);

    /// <summary>Shows a standard message box. Returns which button was clicked.</summary>
    Task<MBResult> ShowMessageAsync(string text, string caption,
        MBButton buttons = MBButton.OK, MBIcon icon = MBIcon.None);

    /// <summary>
    /// Shows a message box with a "Don't show this again" checkbox.
    /// Returns the button clicked and whether the user checked the suppression box.
    /// </summary>
    Task<(MBResult result, bool suppress)> ShowSuppressibleMessageAsync(string text,
        string caption, string suppressLabel,
        MBButton buttons = MBButton.OK, MBIcon icon = MBIcon.None);

    /// <summary>Convenience yes/no prompt. Returns true if Yes was clicked.</summary>
    Task<bool> ShowConfirmAsync(string text, string caption);

    /// <summary>Shows a non-blocking window. Each call creates a new instance.</summary>
    void ShowModeless<TViewModel>(TViewModel viewModel);

    /// <summary>
    /// If a modal dialog for TViewModel is currently open, activates and focuses it.
    /// Returns true if such a dialog was found, false otherwise.
    /// </summary>
    bool TryActivate<TViewModel>();

    /// <summary>
    /// Shows a chromeless modal "Loading" window, fires <paramref name="work"/> once the
    /// window is displayed, then closes the window when the task completes. Any exception
    /// thrown by <paramref name="work"/> is re-thrown to the caller after the window closes.
    /// </summary>
    Task ShowLoadingAsync(string message, Func<Task> work);
}