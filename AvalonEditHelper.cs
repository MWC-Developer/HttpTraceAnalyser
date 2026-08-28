using System.Windows;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Helper class to configure AvalonEdit TextEditor controls globally.
    /// Prevents performance issues caused by LinkElementGenerator's expensive regex operations
    /// when displaying large HTTP trace files.
    /// </summary>
    public static class AvalonEditHelper
    {
        /// <summary>
        /// Attached property to control whether link detection should be disabled.
        /// </summary>
        public static readonly DependencyProperty DisableLinkDetectionProperty =
            DependencyProperty.RegisterAttached(
                "DisableLinkDetection",
                typeof(bool),
                typeof(AvalonEditHelper),
                new PropertyMetadata(false, OnDisableLinkDetectionChanged));

        public static bool GetDisableLinkDetection(DependencyObject obj)
        {
            return (bool)obj.GetValue(DisableLinkDetectionProperty);
        }

        public static void SetDisableLinkDetection(DependencyObject obj, bool value)
        {
            obj.SetValue(DisableLinkDetectionProperty, value);
        }

        private static void OnDisableLinkDetectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextEditor editor && e.NewValue is true)
            {
                // Hook into Loaded event to ensure TextArea is fully initialized
                if (editor.IsLoaded)
                {
                    DisableLinkDetectionCore(editor);
                }
                else
                {
                    editor.Loaded += OnEditorLoaded;
                }
            }
        }

        private static void OnEditorLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextEditor editor)
            {
                editor.Loaded -= OnEditorLoaded;
                DisableLinkDetectionCore(editor);
            }
        }

        /// <summary>
        /// Removes LinkElementGenerator from the TextEditor to prevent regex catastrophic backtracking.
        /// </summary>
        private static void DisableLinkDetectionCore(TextEditor editor)
        {
            if (editor.TextArea?.TextView == null)
                return;

            var generators = editor.TextArea.TextView.ElementGenerators;
            var linkGenerators = new System.Collections.Generic.List<VisualLineElementGenerator>();

            // Collect all LinkElementGenerator instances
            foreach (var generator in generators)
            {
                if (generator is LinkElementGenerator)
                {
                    linkGenerators.Add(generator);
                }
            }

            // Remove them
            foreach (var linkGen in linkGenerators)
            {
                generators.Remove(linkGen);
            }
        }

        /// <summary>
        /// Globally disables link detection for all TextEditor controls in the application.
        /// Call this once during application startup.
        /// </summary>
        public static void DisableLinkDetectionGlobally()
        {
            // Register a handler for all TextEditor controls as they are loaded
            EventManager.RegisterClassHandler(
                typeof(TextEditor),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAnyTextEditorLoaded));
        }

        private static void OnAnyTextEditorLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextEditor editor)
            {
                DisableLinkDetectionCore(editor);
            }
        }
    }
}
