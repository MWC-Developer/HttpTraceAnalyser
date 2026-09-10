using System.Windows;
using System.Windows.Controls;

namespace HttpTraceAnalyser
{
    public partial class AppSettingsWindow : Window
    {
        public AppSettingsWindow(bool useSplitView)
        {
            InitializeComponent();
            ViewLayoutCombo.SelectedIndex = useSplitView ? 1 : 0;
            ThemeCombo.SelectedIndex = (int)AppSettings.ThemePreference;
            HostMcpServerCheckBox.IsChecked = McpHostManager.IsRunning;
            PipeSuffixTextBox.Text = McpHostManager.PipeNameSuffix ?? string.Empty;
            McpStatusText.Text = McpHostManager.IsRunning
                ? "The bridge is currently running."
                : "The bridge is currently stopped.";
            UpdateClientEndpoint();
            PipeSuffixTextBox.TextChanged += (_, _) => UpdateClientEndpoint();
            SettingsCategoryList.SelectedIndex = 0;
        }

        public bool UseSplitView => ViewLayoutCombo.SelectedIndex == 1;

        public ThemePreference SelectedThemePreference => (ThemePreference)ThemeCombo.SelectedIndex;

        public bool HostMcpServer => HostMcpServerCheckBox.IsChecked == true;

        public string? McpPipeNameSuffix { get; private set; }

        private void SettingsCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppearancePage.Visibility = SettingsCategoryList.SelectedIndex == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            LayoutPage.Visibility = SettingsCategoryList.SelectedIndex == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            IntegrationsPage.Visibility = SettingsCategoryList.SelectedIndex == 2
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private string EffectivePipeSuffix => PipeSuffixTextBox.Text.Trim();

        private void UpdateClientEndpoint()
        {
            var suffix = EffectivePipeSuffix;
            ClientEndpointTextBox.Text = string.IsNullOrEmpty(suffix)
                ? "HttpTraceAnalyser.exe --mcp-stdio"
                : $"HttpTraceAnalyser.exe --mcp-stdio --mcp-pipe={suffix}";
            CopyClientConfigButton.IsEnabled = true;
            CopyStatusText.Text = string.Empty;
        }

        private void CopyClientConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var suffix = EffectivePipeSuffix;
            var argsJson = string.IsNullOrEmpty(suffix)
                ? "[\"--mcp-stdio\"]"
                : $"[\"--mcp-stdio\", \"--mcp-pipe={suffix}\"]";

            var config =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"httptraceanalyser\": {\n" +
                "      \"type\": \"stdio\",\n" +
                "      \"command\": \"HttpTraceAnalyser.exe\",\n" +
                $"      \"args\": {argsJson}\n" +
                "    }\n" +
                "  }\n" +
                "}";

            try
            {
                Clipboard.SetText(config);
                CopyStatusText.Text = "Copied";
            }
            catch
            {
                CopyStatusText.Text = "Could not access the clipboard";
            }
        }

        private void ResetAppearanceButton_Click(object sender, RoutedEventArgs e)
        {
            ThemeCombo.SelectedIndex = (int)ThemePreference.Auto;
        }

        private void ResetLayoutButton_Click(object sender, RoutedEventArgs e)
        {
            ViewLayoutCombo.SelectedIndex = 0;
        }

        private void ResetIntegrationsButton_Click(object sender, RoutedEventArgs e)
        {
            PipeSuffixTextBox.Text = string.Empty;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            McpPipeNameSuffix = string.IsNullOrWhiteSpace(PipeSuffixTextBox.Text) ? null : PipeSuffixTextBox.Text.Trim();
            DialogResult = true;
        }
    }
}
