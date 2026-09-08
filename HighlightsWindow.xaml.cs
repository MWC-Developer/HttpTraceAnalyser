using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using HttpTraceAnalyser.Model;

namespace HttpTraceAnalyser
{
    public partial class HighlightsWindow : Window
    {
        public static IValueConverter ColorBrushConverter { get; } = new ColorToBrushConverter();

        private readonly System.Collections.ObjectModel.ObservableCollection<HighlightRule> _rules;
        private readonly uint[] _customColors = new uint[16];
        private Point _dragStartPoint;
        private HighlightRule? _draggedRule;
        private AdornerLayer? _dropAdornerLayer;
        private RuleDropAdorner? _dropAdorner;

        private const uint ChooseColorRgbInit = 0x00000001;
        private const uint ChooseColorFullOpen = 0x00000002;
        private const uint ChooseColorAnyColor = 0x00000100;

        public HighlightsWindow()
        {
            InitializeComponent();
            DataGridThemeHelper.ApplyThemedComboBoxColumnStyles(this, RulesGrid);
            _rules = new System.Collections.ObjectModel.ObservableCollection<HighlightRule>(
                HighlightRuleSet.Rules.Select(rule => rule.Clone()));
            RulesGrid.ItemsSource = _rules;
        }

        private void ApplyThemedComboBoxColumnStyles()
        {
            if (TryFindResource(typeof(ComboBox)) is not Style themedComboBoxStyle)
                return;

            foreach (var column in RulesGrid.Columns)
            {
                if (column is not DataGridComboBoxColumn comboColumn)
                    continue;

                var style = new Style(typeof(ComboBox), themedComboBoxStyle);
                style.Setters.Add(new Setter(ComboBox.IsSynchronizedWithCurrentItemProperty, false));
                style.Seal();

                comboColumn.ElementStyle = style;
                comboColumn.EditingElementStyle = style;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            _rules.Add(new HighlightRule
            {
                Column = HighlightColumn.Response,
                Operator = HighlightOperator.Equals,
                Value = string.Empty,
                BackgroundColor = Colors.LightGray,
            });
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = new List<HighlightRule>();
            foreach (var item in RulesGrid.SelectedItems)
            {
                if (item is HighlightRule rule)
                    selected.Add(rule);
            }
            foreach (var rule in selected)
                _rules.Remove(rule);
        }

        private void RuleDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(RulesGrid);
            _draggedRule = (sender as FrameworkElement)?.DataContext as HighlightRule;
        }

        private void RulesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (FindVisualAncestor<DataGridRow>(source) is null &&
                FindVisualAncestor<DataGridColumnHeader>(source) is null &&
                FindVisualAncestor<ScrollBar>(source) is null)
            {
                RulesGrid.UnselectAll();
            }
        }

        private void RuleDragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedRule is null)
                return;

            var position = e.GetPosition(RulesGrid);
            if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var rule = _draggedRule;
            _draggedRule = null;
            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, rule, DragDropEffects.Move);
            }
            finally
            {
                ClearDropIndicator();
            }
        }

        private void RulesGrid_DragOver(object sender, DragEventArgs e)
        {
            bool canMove = e.Data.GetDataPresent(typeof(HighlightRule));
            e.Effects = canMove ? DragDropEffects.Move : DragDropEffects.None;
            if (canMove)
                UpdateDropIndicator(e);
            else
                ClearDropIndicator();
            e.Handled = true;
        }

        private void RulesGrid_DragLeave(object sender, DragEventArgs e)
        {
            ClearDropIndicator();
        }

        private void RulesGrid_Drop(object sender, DragEventArgs e)
        {
            ClearDropIndicator();

            if (e.Data.GetData(typeof(HighlightRule)) is not HighlightRule rule)
                return;

            int oldIndex = _rules.IndexOf(rule);
            if (oldIndex < 0)
                return;

            int insertionIndex = _rules.Count;
            var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row?.Item is HighlightRule targetRule)
            {
                insertionIndex = _rules.IndexOf(targetRule);
                if (e.GetPosition(row).Y > row.ActualHeight / 2)
                    insertionIndex++;
            }

            if (insertionIndex > oldIndex)
                insertionIndex--;
            insertionIndex = Math.Clamp(insertionIndex, 0, _rules.Count - 1);

            if (insertionIndex != oldIndex)
            {
                _rules.Move(oldIndex, insertionIndex);
                RulesGrid.SelectedItem = rule;
                RulesGrid.ScrollIntoView(rule);
            }

            e.Handled = true;
        }

        private void UpdateDropIndicator(DragEventArgs e)
        {
            var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            bool showAfterRow;
            if (row is null)
            {
                row = RulesGrid.ItemContainerGenerator.ContainerFromIndex(RulesGrid.Items.Count - 1) as DataGridRow;
                showAfterRow = true;
            }
            else
            {
                showAfterRow = e.GetPosition(row).Y > row.ActualHeight / 2;
            }

            if (row is null || (_dropAdorner?.AdornedElement == row && _dropAdorner.ShowAfterRow == showAfterRow))
                return;

            ClearDropIndicator();
            _dropAdornerLayer = AdornerLayer.GetAdornerLayer(row);
            if (_dropAdornerLayer is null)
                return;

            _dropAdorner = new RuleDropAdorner(row, showAfterRow);
            _dropAdornerLayer.Add(_dropAdorner);
        }

        private void ClearDropIndicator()
        {
            if (_dropAdornerLayer is not null && _dropAdorner is not null)
                _dropAdornerLayer.Remove(_dropAdorner);
            _dropAdorner = null;
            _dropAdornerLayer = null;
        }

        private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source is not null)
            {
                if (source is T match)
                    return match;
                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }
            return null;
        }

        private sealed class RuleDropAdorner(DataGridRow row, bool showAfterRow) : Adorner(row)
        {
            public bool ShowAfterRow { get; } = showAfterRow;

            protected override void OnRender(DrawingContext drawingContext)
            {
                double y = ShowAfterRow ? AdornedElement.RenderSize.Height : 0;
                var pen = new Pen(SystemColors.HighlightBrush, 3);
                drawingContext.DrawLine(pen, new Point(0, y), new Point(AdornedElement.RenderSize.Width, y));
            }

            protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            bool useFactoryDefaults = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            string defaultDescription = useFactoryDefaults ? "app defaults" : "saved default rules";

            if (MessageBox.Show(this,
                    $"Replace the current highlight rules with the {defaultDescription}?",
                    "Reset Highlights", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var defaults = useFactoryDefaults
                    ? HighlightRuleSet.GetFactoryDefaults()
                    : HighlightRuleSet.GetSavedDefaultOrFactory();
                _rules.Clear();
                foreach (var rule in defaults)
                    _rules.Add(rule);
            }
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load highlight rules",
                Filter = "Highlight rule files (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = RulePersistence.HighlightDirectory,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                var rules = RulePersistence.LoadHighlights(dialog.FileName);
                _rules.Clear();
                foreach (var rule in rules)
                    _rules.Add(rule);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load highlight rules:\n{ex.Message}",
                    "Load Highlights", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            RulesGrid.CommitEdit();
            RulesGrid.CommitEdit();

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save highlight rules",
                Filter = "Highlight rule files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                InitialDirectory = RulePersistence.HighlightDirectory,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                RulePersistence.SaveHighlights(dialog.FileName, _rules);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to save highlight rules:\n{ex.Message}",
                    "Save Highlights", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            RulesGrid.CommitEdit();
            RulesGrid.CommitEdit();

            if (MessageBox.Show(this,
                    "Overwrite the default highlight rules with the current rules? These rules will load when the application starts.",
                    "Set Default Highlights", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                RulePersistence.SaveHighlights(RulePersistence.HighlightDefaultPath, _rules);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to save the default highlight rules:\n{ex.Message}",
                    "Set Default Highlights", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            RulesGrid.CommitEdit();
            RulesGrid.CommitEdit();

            HighlightRuleSet.Replace(_rules);
            DialogResult = true;
        }

        private void ColorPickerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: HighlightRule rule, Tag: string target })
                return;

            var currentColor = target == "Foreground"
                ? rule.ForegroundColor ?? ThemeManager.GetContrastingColor(rule.BackgroundColor)
                : rule.BackgroundColor;

            var customColorsHandle = GCHandle.Alloc(_customColors, GCHandleType.Pinned);
            try
            {
                var dialog = new ChooseColorData
                {
                    StructSize = Marshal.SizeOf<ChooseColorData>(),
                    Owner = new WindowInteropHelper(this).Handle,
                    Color = ToColorRef(currentColor),
                    CustomColors = customColorsHandle.AddrOfPinnedObject(),
                    Flags = ChooseColorRgbInit | ChooseColorFullOpen | ChooseColorAnyColor,
                };

                if (!ChooseColor(ref dialog))
                    return;

                var selected = FromColorRef(dialog.Color);
                if (target == "Foreground")
                    rule.ForegroundColor = selected;
                else
                    rule.BackgroundColor = selected;
            }
            finally
            {
                customColorsHandle.Free();
            }
        }

        private static uint ToColorRef(Color color)
            => (uint)(color.R | color.G << 8 | color.B << 16);

        private static Color FromColorRef(uint color)
            => Color.FromRgb((byte)color, (byte)(color >> 8), (byte)(color >> 16));

        private void ClearForegroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: HighlightRule rule })
                rule.ForegroundColor = null;
        }

        private sealed class ColorToBrushConverter : IValueConverter
        {
            public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
                => value is Color color ? new SolidColorBrush(color) : Brushes.Transparent;

            public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
                => Binding.DoNothing;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ChooseColorData
        {
            public int StructSize;
            public IntPtr Owner;
            public IntPtr Instance;
            public uint Color;
            public IntPtr CustomColors;
            public uint Flags;
            public IntPtr CustomData;
            public IntPtr Hook;
            public IntPtr TemplateName;
        }

        [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChooseColor(ref ChooseColorData chooseColor);
    }
}
