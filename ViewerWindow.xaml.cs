using System.Windows;
using System.Windows.Controls;

namespace HttpTraceAnalyser
{
    public partial class ViewerWindow : Window
    {
        public ViewerWindow()
        {
            InitializeComponent();
        }

        public ContentControl Host => ViewerHost;
    }
}
