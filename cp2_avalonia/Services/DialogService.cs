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
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Avalonia.Controls;

using ViewModels;
using Views;

public class DialogService(IDialogHost host) : IDialogService
{
    private readonly Dictionary<Type, Func<Window>> mMap = new();

    // Tracks the innermost currently-showing modal so nested dialogs are
    // owned by their parent dialog rather than always by the main window.
    private Window? mCurrentOwner;

    // Tracks open modal windows by ViewModel type so TryActivate can find them.
    // Only populated by ShowDialogAsync; ShowModeless is intentionally excluded
    // because modeless windows (e.g. FileViewer) support multiple concurrent instances.
    private readonly Dictionary<Type, Window> mOpenModals = new();

    public void Register<TViewModel, TView>() where TView : Window, new()
    {
        mMap[typeof(TViewModel)] = () => new TView();
    }

    public async Task<bool?> ShowDialogAsync<TViewModel>(TViewModel viewModel)
    {
        if (!mMap.TryGetValue(typeof(TViewModel), out Func<Window>? factory))
        {
            throw new InvalidOperationException(
                $"No view registered for {typeof(TViewModel).Name}. " +
                $"Call Register<TViewModel, TView>() in RegisterDialogMappings().");
        }
        Window owner = mCurrentOwner ?? host.GetOwnerWindow();
        Window view = factory();
        view.DataContext = viewModel;
        var savedOwner = mCurrentOwner;
        mCurrentOwner = view;
        mOpenModals[typeof(TViewModel)] = view;
        try
        {
            return await view.ShowDialog<bool?>(owner);
        }
        finally
        {
            mCurrentOwner = savedOwner;
            mOpenModals.Remove(typeof(TViewModel));
        }
    }

    public bool TryActivate<TViewModel>()
    {
        if (mOpenModals.TryGetValue(typeof(TViewModel), out Window? win))
        {
            win.Activate();
            return true;
        }
        return false;
    }

    public async Task<MBResult> ShowMessageAsync(string text, string caption,
            MBButton buttons = MBButton.OK, MBIcon icon = MBIcon.None)
    {
        var vm = new MessageBoxViewModel(text, caption, buttons, icon);
        await ShowDialogAsync(vm);
        return vm.Result;
    }

    public async Task<(MBResult result, bool suppress)> ShowSuppressibleMessageAsync(
            string text, string caption, string suppressLabel,
            MBButton buttons = MBButton.OK, MBIcon icon = MBIcon.None)
    {
        var vm = new SuppressibleMessageViewModel(text, caption, suppressLabel, buttons, icon);
        await ShowDialogAsync(vm);
        return (vm.Result, vm.SuppressDontShow);
    }

    public async Task<bool> ShowConfirmAsync(string text, string caption)
    {
        return await ShowMessageAsync(text, caption, MBButton.YesNo) == MBResult.Yes;
    }

    public void ShowModeless<TViewModel>(TViewModel viewModel)
    {
        // Each call creates a new independent Window — this is intentional.
        // Modeless windows (e.g. FileViewer) support multiple concurrent instances
        // and are not tracked in mOpenModals.
        if (!mMap.TryGetValue(typeof(TViewModel), out Func<Window>? factory))
        {
            throw new InvalidOperationException(
                $"No view registered for {typeof(TViewModel).Name}.");
        }
        Window view = factory();
        view.DataContext = viewModel;
        view.Show(host.GetOwnerWindow());
    }

    public async Task ShowLoadingAsync(string message, Func<Task> work)
    {
        Exception? caught = null;
        var dlg = new LoadingDialog(message, async () => {
            try { await work(); }
            catch (Exception ex) { caught = ex; }
        });
        Window owner = mCurrentOwner ?? host.GetOwnerWindow();
        await dlg.ShowDialog<bool?>(owner);
        if (caught != null)
        {
            ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }
}