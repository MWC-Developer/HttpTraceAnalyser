using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using HttpTraceAnalyser.Model;

namespace HttpTraceAnalyser
{
    public partial class FilterWindow : Window
    {
        private readonly ObservableCollection<FilterRule> _rules;

        public FilterWindow()
        {
            InitializeComponent();
            DataGridThemeHelper.ApplyThemedComboBoxColumnStyles(this, RulesGrid);
            _rules = new ObservableCollection<FilterRule>(
                FilterRuleSet.Rules.Select(rule => rule.Clone()));
            RulesGrid.ItemsSource = _rules;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var rule = new FilterRule
            {
                Combinator = FilterCombinator.And,
                Field = FilterField.Response,
                Comparator = FilterComparator.Equals,
                Value = string.Empty,
            };
            _rules.Add(rule);
            RulesGrid.SelectedItem = rule;
            RulesGrid.ScrollIntoView(rule);
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var rule in RulesGrid.SelectedItems.OfType<FilterRule>().ToArray())
                _rules.Remove(rule);
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load filter rules",
                Filter = "Filter rule files (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = RulePersistence.FilterDirectory,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                var rules = RulePersistence.LoadFilters(dialog.FileName);
                _rules.Clear();
                foreach (var rule in rules)
                    _rules.Add(rule);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load filter rules:\n{ex.Message}",
                    "Load Filters", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            RulesGrid.CommitEdit();
            RulesGrid.CommitEdit();

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save filter rules",
                Filter = "Filter rule files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                InitialDirectory = RulePersistence.FilterDirectory,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                RulePersistence.SaveFilters(dialog.FileName, _rules);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to save filter rules:\n{ex.Message}",
                    "Save Filters", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            RulesGrid.CommitEdit();
            RulesGrid.CommitEdit();

            if (MessageBox.Show(this,
                    "Overwrite the default filter rules with the current rules? These rules will load when the application starts.",
                    "Set Default Filters", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                RulePersistence.SaveFilters(RulePersistence.FilterDefaultPath, _rules);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to save the default filter rules:\n{ex.Message}",
                    "Set Default Filters", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            RulesGrid.CommitEdit();
            RulesGrid.CommitEdit();

            FilterRuleSet.Replace(_rules);
            DialogResult = true;
        }
    }
}
