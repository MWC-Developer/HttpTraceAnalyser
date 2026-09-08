using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using HttpTraceAnalyser.Model;

namespace HttpTraceAnalyser
{
    public partial class CustomColumnsWindow : Window
    {
        private readonly ObservableCollection<CustomColumnDefinition> _columns;
        private RegexCheatSheetWindow? _cheatSheetWindow;

        public CustomColumnsWindow()
        {
            InitializeComponent();
            DataGridThemeHelper.ApplyThemedComboBoxColumnStyles(this, ColumnsGrid);
            _columns = new ObservableCollection<CustomColumnDefinition>(
                CustomColumnSet.Columns.Select(column => column.Clone()));
            ColumnsGrid.ItemsSource = _columns;
        }

        private void RegexCheatSheetLink_Click(object sender, RoutedEventArgs e)
        {
            if (_cheatSheetWindow is null)
            {
                _cheatSheetWindow = new RegexCheatSheetWindow { Owner = this };
                _cheatSheetWindow.Closed += (_, _) => _cheatSheetWindow = null;
            }
            _cheatSheetWindow.Show();
            _cheatSheetWindow.Activate();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var column = new CustomColumnDefinition
            {
                Name = "CustomHeader",
                Source = HeaderSource.Request,
                HeaderNamePattern = "^Custom-Header$",
            };
            _columns.Add(column);
            ColumnsGrid.SelectedItem = column;
            ColumnsGrid.ScrollIntoView(column);
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var column in ColumnsGrid.SelectedItems.OfType<CustomColumnDefinition>().ToArray())
                _columns.Remove(column);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitAndValidate())
                return;

            CustomColumnSet.Replace(_columns);
            DialogResult = true;
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load custom columns",
                Filter = "Custom column files (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = CustomColumnSet.CustomColumnDirectory,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                var columns = CustomColumnSet.Load(dialog.FileName);
                if (!ValidateColumns(columns))
                    return;

                _columns.Clear();
                foreach (var column in columns)
                    _columns.Add(column);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load custom columns:\n{ex.Message}",
                    "Load Custom Columns", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitAndValidate())
                return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save custom columns",
                Filter = "Custom column files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                InitialDirectory = CustomColumnSet.CustomColumnDirectory,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                CustomColumnSet.Save(dialog.FileName, _columns);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to save custom columns:\n{ex.Message}",
                    "Save Custom Columns", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CommitAndValidate()
        {
            ColumnsGrid.CommitEdit();
            ColumnsGrid.CommitEdit();
            return ValidateColumns(_columns);
        }

        private bool ValidateColumns(IEnumerable<CustomColumnDefinition> columns)
        {
            var definitions = columns.ToList();
            foreach (var column in definitions)
            {
                column.Name = column.Name?.Trim() ?? string.Empty;
                column.HeaderNamePattern ??= string.Empty;
                column.ValuePattern ??= string.Empty;
            }

            var duplicate = definitions
                .GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                ShowValidationError($"Column name '{duplicate.Key}' is duplicated.");
                return false;
            }

            foreach (var column in definitions)
            {
                var columnName = column.Name;
                if (!Regex.IsMatch(columnName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    ShowValidationError("Column names must start with a letter or underscore and contain only letters, numbers, and underscores.");
                    return false;
                }
                if (TraceColumnCatalog.IsReservedName(columnName))
                {
                    ShowValidationError($"Column name '{columnName}' is reserved by a built-in or plugin field.");
                    return false;
                }
                if (!Enum.IsDefined(column.Source))
                {
                    ShowValidationError($"Column '{columnName}' has an invalid header source.");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(column.HeaderNamePattern))
                {
                    ShowValidationError($"Column '{columnName}' requires a header name pattern.");
                    return false;
                }
                if (!IsValidRegex(column.HeaderNamePattern) ||
                    (!string.IsNullOrEmpty(column.ValuePattern) && !IsValidRegex(column.ValuePattern)))
                {
                    ShowValidationError($"Column '{columnName}' contains an invalid regular expression.");
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidRegex(string pattern)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private void ShowValidationError(string message)
            => MessageBox.Show(this, message, "Custom Columns", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}