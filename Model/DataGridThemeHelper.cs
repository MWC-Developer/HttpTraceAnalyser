using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HttpTraceAnalyser.Model
{
    /// <summary>Shared styling helpers for the rule-editing DataGrids (Filter, Highlights, Custom Columns).</summary>
    internal static class DataGridThemeHelper
    {
        /// <summary>
        /// Applies the Fluent-themed ComboBox style to every <see cref="DataGridComboBoxColumn"/> in
        /// <paramref name="grid"/>, resolving the style from <paramref name="resourceHost"/>.
        /// </summary>
        internal static void ApplyThemedComboBoxColumnStyles(FrameworkElement resourceHost, DataGrid grid)
        {
            if (resourceHost.TryFindResource(typeof(ComboBox)) is not Style themedComboBoxStyle)
                return;

            foreach (var comboColumn in grid.Columns.OfType<DataGridComboBoxColumn>())
            {
                var style = new Style(typeof(ComboBox), themedComboBoxStyle);
                style.Setters.Add(new Setter(ComboBox.IsSynchronizedWithCurrentItemProperty, false));
                style.Seal();
                comboColumn.ElementStyle = style;
                comboColumn.EditingElementStyle = style;
            }
        }
    }
}
