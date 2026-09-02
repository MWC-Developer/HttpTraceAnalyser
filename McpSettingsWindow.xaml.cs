using System.Windows;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Lets the user configure the MCP server's listening port and shows the
    /// GitHub Copilot CLI configuration snippet needed to register the server.
    /// </summary>
    public partial class McpSettingsWindow : Window
    {
        public McpSettingsWindow()
        {
            InitializeComponent();
            PortTextBox.Text = McpHostManager.Port.ToString();
            UpdateConfigSnippet();
            PortTextBox.TextChanged += (_, _) => UpdateConfigSnippet();
        }

        private void UpdateConfigSnippet()
        {
            var port = int.TryParse(PortTextBox.Text, out var p) ? p : McpHostManager.Port;
            ConfigSnippetText.Text =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"httptraceanalyser\": {\n" +
                "      \"type\": \"http\",\n" +
                $"      \"url\": \"http://127.0.0.1:{port}\"\n" +
                "    }\n" +
                "  }\n" +
                "}";
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

            if (McpHostManager.IsRunning && port != McpHostManager.Port)
            {
                PortErrorText.Text = "The server is currently running. Disable it before changing the port; the new port will apply the next time it is enabled.";
                PortErrorText.Visibility = Visibility.Visible;
                return;
            }

            McpHostManager.Port = port;
            DialogResult = true;
        }
    }
}
