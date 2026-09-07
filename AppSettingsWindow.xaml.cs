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
            DarkModeCheckBox.IsChecked = ThemeManager.Current == AppTheme.Dark;
            HostMcpServerCheckBox.IsChecked = McpHostManager.IsRunning;
            PortTextBox.Text = McpHostManager.Port.ToString();
            McpStatusText.Text = McpHostManager.IsRunning
                ? "The server is currently running."
                : "The server is currently stopped.";
            UpdateClientEndpoint();
            PortTextBox.TextChanged += (_, _) => UpdateClientEndpoint();
            SettingsCategoryList.SelectedIndex = 0;
        }

        public bool UseSplitView => ViewLayoutCombo.SelectedIndex == 1;

        public bool UseDarkMode => DarkModeCheckBox.IsChecked == true;

        public bool HostMcpServer => HostMcpServerCheckBox.IsChecked == true;

        public int McpPort { get; private set; }

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

        private void UpdateClientEndpoint()
        {
            bool isValid = int.TryParse(PortTextBox.Text, out var port) && port is >= 1 and <= 65535;
            ClientEndpointTextBox.Text = isValid
                ? $"http://127.0.0.1:{port}"
                : "Enter a valid listening port";
            CopyClientConfigButton.IsEnabled = isValid;
            CopyStatusText.Text = string.Empty;
        }

        private void CopyClientConfigButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
                return;

            var config =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"httptraceanalyser\": {\n" +
                "      \"type\": \"http\",\n" +
                $"      \"url\": \"http://127.0.0.1:{port}\"\n" +
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

        private void ResetPortButton_Click(object sender, RoutedEventArgs e)
        {
            PortTextBox.Text = McpHostManager.DefaultPort.ToString();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortTextBox.Text, out var port) || port < 1 || port > 65535)
            {
                PortErrorText.Text = "Enter a valid port number between 1 and 65535.";
                PortErrorText.Visibility = Visibility.Visible;
                return;
            }

            McpPort = port;
            DialogResult = true;
        }
    }
}