// https://stackoverflow.com/questions/660554/how-to-automatically-select-all-text-on-focus-in-wpf-textbox
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// Dependency property that changes the behavior of a TextBox.  When the user clicks on
    /// or navigates to a TextBox that has this property active, all of the text will be selected.
    /// </summary>
    /// <remarks>
    /// This is from <see href="https://stackoverflow.com/a/2674291/294248"/>.  The obvious
    /// solution - calling SelectAll() when the TextBox gets focus - doesn't work, because the
    /// selection is lost when the mouse button is released.
    /// </remarks>
    public static class SelectTextOnFocus {
        public static readonly DependencyProperty ActiveProperty =
            DependencyProperty.RegisterAttached(
                "Active",
                typeof(bool),
                typeof(SelectTextOnFocus),
                new PropertyMetadata(false, ActivePropertyChanged));

        private static void ActivePropertyChanged(DependencyObject d,
                DependencyPropertyChangedEventArgs e) {
            if (d is TextBox) {
                TextBox? textBox = d as TextBox;
                if (textBox == null) {
                    return;
                }
                if ((e.NewValue as bool?).GetValueOrDefault(false)) {
                    textBox.GotKeyboardFocus += OnKeyboardFocusSelectText;
                    textBox.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
                } else {
                    textBox.GotKeyboardFocus -= OnKeyboardFocusSelectText;
                    textBox.PreviewMouseLeftButtonDown -= OnMouseLeftButtonDown;
                }
            }
        }

        private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            DependencyObject? dependencyObject = GetParentFromVisualTree(e.OriginalSource);
            if (dependencyObject == null) {
                return;
            }

            var textBox = (TextBox)dependencyObject;
            if (!textBox.IsKeyboardFocusWithin) {
                textBox.Focus();
                e.Handled = true;
            }
        }

        private static DependencyObject? GetParentFromVisualTree(object source) {
            DependencyObject? parent = source as UIElement;
            while (parent != null && !(parent is TextBox)) {
                parent = VisualTreeHelper.GetParent(parent);
            }

            return parent;
        }

        private static void OnKeyboardFocusSelectText(object sender,
                KeyboardFocusChangedEventArgs e) {
            TextBox? textBox = e.OriginalSource as TextBox;
            if (textBox != null) {
                textBox.SelectAll();
            }
        }

        [AttachedPropertyBrowsableForChildrenAttribute(IncludeDescendants = false)]
        [AttachedPropertyBrowsableForType(typeof(TextBox))]
        public static bool GetActive(DependencyObject @object) {
            return (bool)@object.GetValue(ActiveProperty);
        }

        public static void SetActive(DependencyObject @object, bool value) {
            @object.SetValue(ActiveProperty, value);
        }
    }
}
