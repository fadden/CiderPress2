/*
 * Copyright 2019 faddenSoft
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
using Avalonia.Controls;
using Avalonia.Input;
using cp2_avalonia.ViewModels;

namespace cp2_avalonia.Tools {
    public partial class ShowText : Window {
        public ShowText() {
            InitializeComponent();
            DataContextChanged += (_, __) => {
                if (DataContext is ShowTextViewModel vm) {
                    vm.CloseRequested += result => Close(result);
                    vm.CloseRequested_Modeless += () => Close();
                }
            };
        }

        private void Window_KeyDown(object? sender, KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                Close();
                e.Handled = true;
            }
        }
    }
}
