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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace cp2_avalonia.Views;

/// <summary>
/// A chromeless modal "Loading..." dialog. The caller passes a message string and a
/// <see cref="Func{Task}"/> to execute. Once the window is fully displayed (Opened event),
/// the task is fired. The window closes itself automatically when the task completes.
/// Exceptions propagate back through <see cref="Services.IDialogService.ShowLoadingAsync"/>.
/// </summary>
public partial class LoadingDialog : Window
{
    private readonly Func<Task> mWork;

    // Parameterless constructor required by the Avalonia XAML runtime loader.
    public LoadingDialog()
    {
        mWork = () => Task.CompletedTask;
        InitializeComponent();
    }

    public LoadingDialog(string message, Func<Task> work)
    {
        mWork = work;
        InitializeComponent();
        messageText.Text = message;
    }

    private async void Window_Opened(object? sender, EventArgs e)
    {
        // Re-center after layout. We derive everything from PointToScreen so
        // no manual scale-factor arithmetic is needed — Avalonia handles all
        // coordinate-system and DPI conversions internally.
        //
        // ownerCenter: physical screen pixel at the center of the owner's client area.
        // halfW/halfH:  half the dialog's physical pixel dimensions, obtained by
        //               converting (0,0)→(w,h) through PointToScreen so the result
        //               is in the same physical-pixel space as ownerCenter.
        if (Owner is Window owner)
        {
            var ownerCenter = owner.PointToScreen(
                new Point(owner.ClientSize.Width / 2.0, owner.ClientSize.Height / 2.0));
            var dlgP0 = this.PointToScreen(new Point(0, 0));
            var dlgP1 = this.PointToScreen(new Point(ClientSize.Width, ClientSize.Height));
            int halfW = (dlgP1.X - dlgP0.X) / 2;
            int halfH = (dlgP1.Y - dlgP0.Y) / 2;
            Position = new PixelPoint(ownerCenter.X - halfW, ownerCenter.Y - halfH);
        }
        try
        {
            await mWork();
        }
        finally
        {
            Close();
        }
    }
}
