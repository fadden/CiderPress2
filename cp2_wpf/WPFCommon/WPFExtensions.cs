﻿/*
 * Copyright 2019 faddenSoft
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
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace cp2_wpf.WPFCommon {
    /// <summary>
    /// Generic Visual helper.
    /// </summary>
    public static class VisualHelper {
        /// <summary>
        /// Find a child object in a WPF visual tree.
        /// </summary>
        /// <remarks>
        /// Sample usage:
        ///   <code>GridViewHeaderRowPresenter headerRow = listView.GetVisualChild&lt;GridViewHeaderRowPresenter&gt;();</code>
        ///
        /// From <see href="https://social.msdn.microsoft.com/Forums/vstudio/en-US/7d0626cb-67e8-4a09-a01e-8e56ee7411b2/gridviewcolumheader-radiobuttons?forum=wpf"/>.
        /// </remarks>
        /// <typeparam name="T"></typeparam>
        /// <param name="referenceVisual">Start point.</param>
        /// <returns>Object of appropriate type, or null if not found.</returns>
        public static T? GetVisualChild<T>(this Visual referenceVisual) where T : Visual {
            Visual? child = null;
            for (Int32 i = 0; i < VisualTreeHelper.GetChildrenCount(referenceVisual); i++) {
                child = VisualTreeHelper.GetChild(referenceVisual, i) as Visual;
                if (child != null && child is T) {
                    break;
                } else if (child != null) {
                    child = GetVisualChild<T>(child);
                    if (child != null && child is T) {
                        break;
                    }
                }
            }
            return child as T;
        }
    }

#if false
    /// <summary>
    /// Helper functions for working with a ListView.
    ///
    /// ListViews are generalized to an absurd degree, so simple things like "what column did
    /// I click on" and "what row is at the top" that were easy in WinForms are not provided
    /// by WPF.
    /// </summary>
    public static class ListViewExtensions {
        /// <summary>
        /// Figures out which item index is at the top of the window.  This only works for a
        /// ListView with a VirtualizingStackPanel.
        /// </summary>
        /// <remarks>
        /// See https://stackoverflow.com/q/2926722/294248 for an alternative approach that
        /// uses hit-testing, as well as a copy of this approach.
        ///
        /// Looks like we get the same values from ScrollViewer.VerticalOffset.  I don't know
        /// if there's a reason to favor one over the other.
        /// </remarks>
        /// <returns>The item index, or -1 if the list is empty.</returns>
        public static int GetTopItemIndex(this ListView lv) {
            if (lv.Items.Count == 0) {
                return -1;
            }

            VirtualizingStackPanel vsp = lv.GetVisualChild<VirtualizingStackPanel>();
            if (vsp == null) {
                Debug.Assert(false, "ListView does not have a VirtualizingStackPanel");
                return -1;
            }
            return (int)vsp.VerticalOffset;
        }

        /// <summary>
        /// Scrolls the ListView so that the specified item is at the top.  The standard
        /// ListView.ScrollIntoView() makes the item visible but doesn't ensure a
        /// specific placement.
        /// </summary>
        /// <remarks>
        /// Equivalent to setting myListView.TopItem in WinForms.  Unfortunately, the
        /// ScrollIntoView call takes 60-100ms on a list with fewer than 1,000 items.  And
        /// sometimes it just silently fails.  Prefer ScrollToIndex() to this.
        /// </remarks>
        public static void ScrollToTopItem(this ListView lv, object item) {
            ScrollViewer sv = lv.GetVisualChild<ScrollViewer>();
            sv.ScrollToBottom();
            lv.ScrollIntoView(item);
        }

        /// <summary>
        /// Scrolls the ListView to the specified vertical index.  The ScrollViewer should
        /// be operating in "logical" units (lines) rather than "physical" units (pixels).
        /// </summary>
        public static void ScrollToIndex(this ListView lv, int index) {
            ScrollViewer sv = lv.GetVisualChild<ScrollViewer>();
            sv.ScrollToVerticalOffset(index);
        }

        /// <summary>
        /// Returns the ListViewItem that was clicked on, or null if an LVI wasn't the target
        /// of a click (e.g. off the bottom of the list).
        /// </summary>
        public static ListViewItem GetClickedItem(this ListView lv, MouseButtonEventArgs e) {
            DependencyObject dep = (DependencyObject)e.OriginalSource;

            // Should start at something like a TextBlock.  Walk up the tree until we hit the
            // ListViewItem.
            while (dep != null && !(dep is ListViewItem)) {
                dep = VisualTreeHelper.GetParent(dep);
            }
            if (dep == null) {
                return null;
            }
            return (ListViewItem)dep;
        }

        /// <summary>
        /// Determines which column was the target of a mouse click.  Only works for ListView
        /// with GridView.
        /// </summary>
        /// <remarks>
        /// There's just no other way to do this with ListView.  With DataGrid you can do this
        /// somewhat reasonably (see below), but ListView just doesn't want to help.
        /// </remarks>
        /// <returns>Column index, or -1 if the click was outside the columns (e.g. off the right
        ///   edge).</returns>
        public static int GetClickEventColumn(this ListView lv, MouseButtonEventArgs e) {
            // There's a bit of padding that seems to offset things.  Not sure how to account
            // for it, so for now just fudge it.
            const int FUDGE = 4;

            // Need to take horizontal scrolling into account.
            ScrollViewer sv = lv.GetVisualChild<ScrollViewer>();
            double scrollPos = sv.HorizontalOffset;

            Point p = e.GetPosition(lv);
            GridView gv = (GridView)lv.View;
            double startPos = FUDGE - scrollPos;
            for (int index = 0; index < gv.Columns.Count; index++) {
                GridViewColumn col = gv.Columns[index];

                if (p.X < startPos + col.ActualWidth) {
                    return index;
                }
                startPos += col.ActualWidth;
            }

            return -1;
        }
    }
#endif

    /// <summary>
    /// Helper functions for working with DataGrids.
    /// </summary>
    /// <remarks>
    /// <para>It's tempting to simply handle double-click actions by using the selected row.  This
    /// gets a little weird, though, because double-clicking on a header or blank area doesn't
    /// clear the selection.</para>
    /// <para>Great explanation of row/cell selection in a DataGrid:
    /// <see href="https://blog.magnusmontin.net/2013/11/08/how-to-programmatically-select-and-focus-a-row-or-cell-in-a-datagrid-in-wpf/"/>;
    /// summarized here: <see href="https://stackoverflow.com/a/42583726/294248"/>.</para>
    /// <para>Also used here: <see href="https://stackoverflow.com/a/29081353/294248"/>.</para>
    /// </remarks>
    public static class DataGridExtensions {
        /// <summary>
        /// Determines which row and column was the target of a mouse button action.
        /// </summary>
        /// <remarks>
        /// Based on
        /// <see href="https://blog.scottlogic.com/2008/12/02/wpf-datagrid-detecting-clicked-cell-and-row.html"/>.
        /// </remarks>
        /// <param name="e">Arguments received from event.</param>
        /// <param name="rowIndex">Result: index of clicked row.</param>
        /// <param name="colIndex">Result: index of clicked column.</param>
        /// <param name="item">Result: if a data item was clicked, the data item; otherwise
        ///   null.</param>
        /// <returns>True if the click was on a data item.</returns>
        public static bool GetClickRowColItem(this DataGrid dg, MouseButtonEventArgs e,
                out int rowIndex, out int colIndex, [NotNullWhen(true)] out object? item) {
            return GetRowColItem(dg, (DependencyObject)e.OriginalSource, out rowIndex,
                out colIndex, out item);
        }

        /// <summary>
        /// Determines which row and column was the target of a drop action.
        /// </summary>
        public static bool GetDropRowColItem(this DataGrid dg, DragEventArgs e,
                out int rowIndex, out int colIndex, [NotNullWhen(true)] out object? item) {
            return GetRowColItem(dg, (DependencyObject)e.OriginalSource, out rowIndex,
                out colIndex, out item);
        }

        private static bool GetRowColItem(DataGrid dg, DependencyObject dep,
                out int rowIndex, out int colIndex, [NotNullWhen(true)] out object? item) {
            rowIndex = colIndex = -1;
            item = null;

            // The initial dep will likely be a TextBlock.  Walk up the tree until we find
            // an object for the cell.  If we don't find one, this might be a click in the
            // header or off the bottom of the list.
            while (!(dep is DataGridCell)) {
                dep = VisualTreeHelper.GetParent(dep);
                if (dep == null) {
                    return false;
                }
            }
            DataGridCell cell = (DataGridCell)dep;

            // Now search up for the DataGridRow object.
            do {
                dep = VisualTreeHelper.GetParent(dep);
                if (dep == null) {
                    Debug.Assert(false, "Found cell but not row?");
                    return false;
                }
            } while (!(dep is DataGridRow));
            DataGridRow row = (DataGridRow)dep;

            // Get a row index for the entry.
            DataGrid rowGrid = (DataGrid)ItemsControl.ItemsControlFromItemContainer(row);
            rowIndex = rowGrid.ItemContainerGenerator.IndexFromContainer(row);

            // Column index is, weirdly enough, just sitting in a property.
            colIndex = cell.Column.DisplayIndex;

            // Item is part of the row.
            item = row.Item;
            return true;
        }

        /// <summary>
        /// Gets a specific cell, scrolling the list around to cope with virtualization.
        /// </summary>
        public static DataGridCell? GetCell(DataGrid dataGrid, DataGridRow rowContainer,
                int column) {
            if (rowContainer != null) {
                DataGridCellsPresenter? presenter =
                        rowContainer.GetVisualChild<DataGridCellsPresenter>();
                if (presenter == null) {
                    /* if the row has been virtualized away, call its ApplyTemplate() method
                     * to build its visual tree in order for the DataGridCellsPresenter
                     * and the DataGridCells to be created */
                    rowContainer.ApplyTemplate();
                    presenter = rowContainer.GetVisualChild<DataGridCellsPresenter>();
                }
                if (presenter != null) {
                    DataGridCell? cell =
                        presenter.ItemContainerGenerator.ContainerFromIndex(column) as DataGridCell;
                    if (cell == null) {
                        /* bring the column into view
                         * in case it has been virtualized away */
                        try {
                            dataGrid.ScrollIntoView(rowContainer, dataGrid.Columns[column]);
                        } catch (InvalidOperationException ex) {
                            Debug.WriteLine("GLITCH: GetCell failed: " + ex.Message);
                            return null;
                        }
                        cell = presenter.ItemContainerGenerator.ContainerFromIndex(column)
                            as DataGridCell;
                    }
                    return cell;
                }
            }
            return null;
        }

        /// <summary>
        /// Sets the selection to a specific row and column, and gives it focus.
        /// </summary>
        /// <param name="rowIndex">Row index.</param>
        /// <param name="colIndex">Column index.</param>
        /// <returns>True on success, false if the cell could not be selected.</returns>
        public static bool SelectRowColAndFocus(this DataGrid dataGrid, int rowIndex,
                int colIndex) {
            if (rowIndex < 0 || rowIndex >= dataGrid.Items.Count) {
                Debug.Assert(false, "bad rowIndex " + rowIndex);
                return false;
            }
            DataGridRow? row =
                dataGrid.ItemContainerGenerator.ContainerFromIndex(rowIndex) as DataGridRow;
            if (row == null) {
                return false;
            }
            DataGridCell? cell = GetCell(dataGrid, row, colIndex);
            if (cell == null) {
                return false;
            }
            cell.Focus();
            //Keyboard.Focus(cell);
            cell.IsSelected = true;
            return true;
        }

        /// <summary>
        /// Sets the selection to a specific row, and gives it focus.
        /// </summary>
        /// <remarks>
        /// <para>The DataGrid must be configured for "FullRow" selection.</para>
        /// </remarks>
        /// <param name="rowIndex">Row index.</param>
        /// <exception cref="ArgumentException"></exception>
        public static void SelectRowByIndex(this DataGrid dataGrid, int rowIndex) {
            if (!dataGrid.SelectionUnit.Equals(DataGridSelectionUnit.FullRow)) {
                throw new ArgumentException("SelectionUnit of DataGrid is not FullRow");
            }
            if (rowIndex < 0 || rowIndex >= dataGrid.Items.Count) {
                throw new ArgumentException("Invalid row index: " + rowIndex);
            }
            dataGrid.SelectedItems.Clear();
            object item = dataGrid.Items[rowIndex];
            dataGrid.SelectedItem = item;

            DataGridRow? row =
                dataGrid.ItemContainerGenerator.ContainerFromIndex(rowIndex) as DataGridRow;
            if (row == null) {
                /* bring the data item (Product object) into view
                 * in case it has been virtualized away */
                dataGrid.ScrollIntoView(item);
                row = dataGrid.ItemContainerGenerator.ContainerFromIndex(rowIndex) as DataGridRow;
            }
            if (row != null) {
                DataGridCell? cell = GetCell(dataGrid, row, 0);
                if (cell != null) {
                    cell.Focus();
                }
            }
        }

#if false
        public static DataGridRow GetRow(this DataGrid grid, int index) {
            DataGridRow row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
            if (row == null) {
                // May be virtualized, bring into view and try again.
                grid.UpdateLayout();
                grid.ScrollIntoView(grid.Items[index]);
                row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
            }
            return row;
        }
#endif

        /// <summary>
        /// Resets the DataGrid to default sorting after it has been changed by clicking on
        /// the headers.  Clears the sort and the indicator arrows.
        /// </summary>
        /// <remarks>
        /// From <see href="https://stackoverflow.com/a/9533076/294248"/>.
        /// </remarks>
        public static void ResetSort(this DataGrid grid) {
            ICollectionView view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
            if (view != null) {
                if (view.SortDescriptions.Count > 0) {
                    view.SortDescriptions.Clear();
                }
                foreach (DataGridColumn column in grid.Columns) {
                    column.SortDirection = null;
                }
            }
        }

        /// <summary>
        /// Scrolls the DataGrid to the top.
        /// </summary>
        public static void ScrollToTop(this DataGrid dg) {
            VirtualizingStackPanel? virtPanel =
                VisualHelper.GetVisualChild<VirtualizingStackPanel>(dg);
            if (virtPanel != null) {
                virtPanel.SetVerticalOffset(0);
            } else {
                Debug.Assert(false, "Unable to find datagrid VSP");
            }
        }
    }

    /// <summary>
    /// RichTextBox extensions.
    /// </summary>
    public static class RichTextBoxExtensions {
        /// <summary>
        /// Overloads RichTextBox.AppendText() with a version that takes a color as an argument.
        /// NOTE: color is "sticky", and will affect the next call to the built-in AppendText()
        /// method.
        /// </summary>
        /// <remarks>
        /// Adapted from https://stackoverflow.com/a/23402165/294248
        ///
        /// TODO(someday): figure out how to reset the color for future calls.
        /// </remarks>
        public static void AppendText(this RichTextBox box, string text, Color color) {

            TextRange tr = new TextRange(box.Document.ContentEnd, box.Document.ContentEnd);
            tr.Text = text;
            try {
                tr.ApplyPropertyValue(TextElement.ForegroundProperty,
                    new SolidColorBrush(color));
            } catch (FormatException ex) {
                Debug.WriteLine("RTB AppendText extension failed: " + ex);
            }
        }
    }

    /// <summary>
    /// BitmapSource extensions.
    /// </summary>
    public static class BitmapSourceExtensions {
        /// <summary>
        /// Creates a scaled copy of a BitmapSource.  Only scales up, using nearest-neighbor.
        /// </summary>
        public static BitmapSource CreateScaledCopy(this BitmapSource src, int scale) {
            // Simple approach always does a "blurry" scale.
            //return new TransformedBitmap(src, new ScaleTransform(scale, scale));

            // Adapted from https://weblogs.asp.net/bleroy/resizing-images-from-the-server-using-wpf-wic-instead-of-gdi
            // (found via https://stackoverflow.com/a/25570225/294248)
            BitmapScalingMode scalingMode = BitmapScalingMode.NearestNeighbor;

            int newWidth = (int)src.Width * scale;
            int newHeight = (int)src.Height * scale;

            var group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, scalingMode);
            group.Children.Add(new ImageDrawing(src,
                    new Rect(0, 0, newWidth, newHeight)));
            var targetVisual = new DrawingVisual();
            var targetContext = targetVisual.RenderOpen();
            targetContext.DrawDrawing(group);
            var target = new RenderTargetBitmap(
                newWidth, newHeight, 96, 96, PixelFormats.Default);
            targetContext.Close();
            target.Render(targetVisual);
            var targetFrame = BitmapFrame.Create(target);
            return targetFrame;
        }
    }

    /// <summary>
    /// VirtualizingPanel extensions.
    /// </summary>
    public static class VirtualizingPanelExtensions {
        // Use reflection to get at internal method.
        private static readonly MethodInfo BringIndexIntoViewMethodInfo =
            typeof(VirtualizingPanel).GetMethod("BringIndexIntoView",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

        /// <summary>
        /// Invokes VirtualizingPanel.BringIndexIntoView(int).
        /// </summary>
        public static void BringIndexIntoView_Public(this VirtualizingPanel virtPanel, int index) {
            Debug.Assert(virtPanel != null);
            try {
                BringIndexIntoViewMethodInfo.Invoke(virtPanel, new object[] { index });
            } catch (TargetInvocationException ex) {
                Debug.WriteLine("BringIndexIntoView_Public GLITCH: " + ex);
            }
        }
    }

    /// <summary>
    /// TreeView extensions.
    /// </summary>
    public static class TreeViewExtensions {
        /// <summary>
        /// Scrolls the TreeView to the top.
        /// </summary>
        public static void ScrollToTop(this TreeView tv) {
            VirtualizingStackPanel? virtPanel =
                VisualHelper.GetVisualChild<VirtualizingStackPanel>(tv);
            if (virtPanel != null) {
                virtPanel.SetVerticalOffset(0);
            } else {
                Debug.Assert(false, "Unable to find treeview VSP");
            }
        }
    }
}
