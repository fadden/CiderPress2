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
using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using cp2_avalonia.ViewModels;
using cp2_avalonia.Services;
using cp2_avalonia.Views;

namespace cp2_avalonia
{
    public partial class App : Application
    {
        /// <summary>
        /// Theme mode choices persisted via AppSettings.
        /// </summary>
        public enum ThemeMode { Light, Dark, System }

        // Icon brush colors and disabled opacity, per theme.
        private static readonly Color LightIconColor = Color.Parse("#212121");
        private static readonly Color DarkIconColor = Color.Parse("#E0E0E0");
        private const double LightIconDisabledOpacity = 0.4;
        private const double DarkIconDisabledOpacity = 0.5;

        /// <summary>
        /// Application-wide service provider. Populated during
        /// OnFrameworkInitializationCompleted().
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>
        /// Settings service created before DI is initialised so that
        /// <see cref="Initialize"/> can read theme/font settings during Avalonia
        /// startup.  The same instance is registered as the DI singleton so the
        /// entire application shares one <c>SettingsHolder</c>.
        /// </summary>
        private readonly ISettingsService _settingsService = new SettingsService();

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            _settingsService.Load();
            ApplyTheme();
            ApplyFonts();
        }

        private MainViewModel? GetMainViewModel() => (GetMainWindow()?.DataContext) as MainViewModel;

        public override void OnFrameworkInitializationCompleted()
        {
            var serviceCollection = new ServiceCollection();

            // Singletons (container-managed)
            serviceCollection.AddSingleton<ISettingsService>(_settingsService);
            serviceCollection.AddSingleton<IClipboardService, ClipboardService>();
            serviceCollection.AddSingleton<IDialogServiceFactory, DialogServiceFactory>();
            serviceCollection.AddSingleton<IFilePickerServiceFactory, FilePickerServiceFactory>();
            serviceCollection.AddSingleton<IViewerService, ViewerService>();
            serviceCollection.AddSingleton<IWorkspaceService>(
                sp => new WorkspaceService(sp.GetRequiredService<ISettingsService>()));

            // IDialogHost is NOT registered. Host-specific services are created
            // through factories in Step 10.

            Services = serviceCollection.BuildServiceProvider();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Exit += (_, _) => TempDirectoryTracker.CleanupAll();

                var mainWindow = new MainWindow(
                    Services.GetRequiredService<ISettingsService>(),
                    Services.GetRequiredService<IClipboardService>(),
                    Services.GetRequiredService<IDialogServiceFactory>(),
                    Services.GetRequiredService<IFilePickerServiceFactory>(),
                    Services.GetRequiredService<IViewerService>(),
                    Services.GetRequiredService<IWorkspaceService>()
                );

                // Avalonia (especially on macOS) tends to show the window before all
                // theme resources and layout have settled, producing a brief "flash"
                // of the wrong theme.  Additionally, on macOS the window often fails
                // to fully claim app focus on first appearance, leaving native menus
                // in a broken state.
                //
                // Workaround: hide the window (Opacity=0, ShowActivated=false) so the
                // desktop lifetime's automatic Show() happens invisibly.  Once the
                // window has finished its initial Open/layout pass, wait ~100ms for
                // the theme + native window state to settle, then explicitly reveal,
                // Show, Activate, and Focus it.
                mainWindow.Opacity = 0;
                mainWindow.ShowActivated = false;

                mainWindow.Opened += (s, e) =>
                {
                    var timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(10)
                    };
                    timer.Tick += (ts, te) =>
                    {
                        timer.Stop();
                        mainWindow.Opacity = 1;
                        mainWindow.Show();
                        mainWindow.Activate();
                        mainWindow.Focus();
                    };
                    timer.Start();
                };

                desktop.MainWindow = mainWindow;
            }
            base.OnFrameworkInitializationCompleted();
        }

        private MainWindow? GetMainWindow() =>
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow as MainWindow;

        private void OnNativeAboutClick(object? sender, EventArgs e) =>
           GetMainViewModel()?.AboutCommand.Execute(null);

        private void OnNativeSettingsClick(object? sender, EventArgs e) =>
            GetMainViewModel()?.EditAppSettingsCommand.Execute(null);

        private void OnNativeQuitClick(object? sender, EventArgs e) =>
            GetMainViewModel()?.ExitCommand.Execute(null);

        /// <summary>
        /// Applies the theme based on the current setting.
        /// </summary>
        public void ApplyTheme()
        {
            ThemeMode mode = _settingsService.GetEnum(AppSettings.THEME_MODE, ThemeMode.Light);
            RequestedThemeVariant = mode switch
            {
                ThemeMode.Dark => ThemeVariant.Dark,
                ThemeMode.System => ThemeVariant.Default,
                _ => ThemeVariant.Light,
            };

            // Update icon brushes and disabled opacity imperatively.
            // We mutate the existing brush Color rather than replacing with a new
            // instance, because DrawingImage resources in Icons.axaml hold direct
            // references to the original brush objects.
            bool isDark = ActualThemeVariant == ThemeVariant.Dark;
            Color iconColor = isDark ? DarkIconColor : LightIconColor;
            if (Resources["IconForegroundBrush"] is SolidColorBrush fgBrush)
            {
                fgBrush.Color = iconColor;
            }
            if (Resources["IconForegroundFillBrush"] is SolidColorBrush fillBrush)
            {
                fillBrush.Color = iconColor;
            }
            Resources["IconDisabledOpacity"] = isDark ? DarkIconDisabledOpacity : LightIconDisabledOpacity;
        }

        /// <summary>
        /// Raised after ApplyFonts() updates the font resources.
        /// </summary>
        public static event Action? FontsChanged;

        // Cross-platform default monospace fallback chain:
        // Win (Cascadia/Consolas), macOS (Menlo/Monaco), Linux (DejaVu/Liberation/Noto).
        private const string DefaultMonoFamily =
            "Cascadia Mono, Consolas, Menlo, Monaco, DejaVu Sans Mono, Liberation Mono, " +
            "Noto Sans Mono, monospace";
        private const int DefaultFontSize = 13;

        /// <summary>
        /// Pushes the default font resources into the application resource dictionary so
        /// StaticResource/DynamicResource lookups pick up the standard mono family/size.
        /// </summary>
        public void ApplyFonts()
        {
            Resources["ViewerMonoFont"] = new FontFamily(DefaultMonoFamily);
            // GeneralMonoFont is used by editing dialogs; keep it in sync.
            Resources["GeneralMonoFont"] = new FontFamily(DefaultMonoFamily);
            Resources["ViewerDefaultFontSize"] = (double)DefaultFontSize;
            FontsChanged?.Invoke();
        }
    }
}
