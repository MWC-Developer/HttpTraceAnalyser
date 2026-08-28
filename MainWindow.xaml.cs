using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using HttpTraceAnalyser.Model;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using SharpVectors.Converters;

namespace HttpTraceAnalyser
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private HttpTraceFile? _trace;

        // Cached request/response payload + headers for the currently selected row,
        // so switching the payload format doesn't require re-fetching from the DataTable.
        private byte[]? _requestPayload;
        private IReadOnlyList<KeyValuePair<string, string>>? _requestHeaders;
        private byte[]? _responsePayload;
        private IReadOnlyList<KeyValuePair<string, string>>? _responseHeaders;

        private enum PayloadFormat { PlainText = 0, Json = 1, Xml = 2, Html = 3, JavaScript = 4, Image = 5, Svg = 6 }

        // Word-wrap state for the RichTextBox viewers (Summary, Mapi). RichTextBox has
        // no built-in wrap toggle; we simulate it by pinning Document.PageWidth. State
        // is tracked here so that rebuilding the FlowDocument preserves the user's choice.
        // Default (checked) = wrap, so no unnecessary horizontal scroll bar is shown.
        private bool _summaryWrap = true;
        private bool _mapiWrap = true;

        // Cached loader-level summary (e.g. ETL provider event counts). Shown in the
        // Summary viewer whenever no row is selected, so it stays visible even when
        // the loader extracted no HTTP messages.
        private FlowDocument? _loaderSummary;

        // Column-sort state. Cycle per column: none -> ascending -> descending -> none.
        private GridViewColumn? _sortColumn;
        private ListSortDirection? _sortDirection;
        private readonly Dictionary<GridViewColumn, string> _originalHeaders = new();

        private const string AscendingArrow = " \u25B2";  // ▲
        private const string DescendingArrow = " \u25BC"; // ▼

        // Deferred rendering state: track which tabs need their payload editors populated
        // when they become visible. This prevents expensive AvalonEdit rendering for invisible tabs.
        private bool _requestPayloadNeedsRender;
        private bool _responsePayloadNeedsRender;
        private PayloadFormat _pendingRequestFormat;
        private PayloadFormat _pendingResponseFormat;

        // Track which tabs have been activated at least once to handle initial visibility
        private bool _requestTabEverActivated;
        private bool _responseTabEverActivated;

        // Track if we're currently switching tabs to prevent re-entrancy
        private bool _isHandlingTabSwitch;

        // Track if we're populating viewers to suppress format change events
        private bool _isPopulatingViewers;

        public MainWindow()
        {
            InitializeComponent();

            // Disable link detection after controls are loaded to prevent regex performance issues.
            // This is a redundant safety measure in addition to the global handler in App.OnStartup.
            Loaded += (_, _) => DisableLinkDetection();

            HighlightRuleSet.RulesChanged += OnHighlightRulesChanged;
            FilterRuleSet.FiltersChanged += OnFilterRulesChanged;
            ActiveFiltersList.ItemsSource = FilterRuleSet.Rules;
            DarkModeToggle.IsChecked = ThemeManager.Current == AppTheme.Dark;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (_, _) =>
            {
                HighlightRuleSet.RulesChanged -= OnHighlightRulesChanged;
                FilterRuleSet.FiltersChanged -= OnFilterRulesChanged;
                ThemeManager.ThemeChanged -= OnThemeChanged;
            };
        }

        /// <summary>
        /// Disables link detection in AvalonEdit controls to prevent expensive regex operations
        /// that can cause UI hangs when displaying large HTTP trace files.
        /// This method is called after window load as a redundant safety measure.
        /// </summary>
        private void DisableLinkDetection()
        {
            DisableLinkDetectionForEditor(RequestPayloadEditor);
            DisableLinkDetectionForEditor(ResponsePayloadEditor);
        }

        /// <summary>
        /// Helper method to safely remove LinkElementGenerator from a TextEditor control.
        /// </summary>
        private void DisableLinkDetectionForEditor(ICSharpCode.AvalonEdit.TextEditor? editor)
        {
            if (editor?.TextArea?.TextView == null)
                return;

            try
            {
                var generators = editor.TextArea.TextView.ElementGenerators.ToList();
                foreach (var generator in generators.OfType<LinkElementGenerator>())
                {
                    editor.TextArea.TextView.ElementGenerators.Remove(generator);
                }
            }
            catch
            {
                // Silently catch any exceptions during removal to prevent app startup failures
            }
        }

        private void DarkModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            var target = DarkModeToggle.IsChecked == true ? AppTheme.Dark : AppTheme.Light;
            if (ThemeManager.Current != target)
                ThemeManager.Apply(target);
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            DarkModeToggle.IsChecked = ThemeManager.Current == AppTheme.Dark;
            // Row foreground uses a value converter that resolves the theme's
            // default brush when the row has no explicit colour, so re-run the
            // bindings to pick up the new palette.
            RequestList.Items.Refresh();

            // Reset syntax highlighting definitions to reload with new theme
            SyntaxHighlightingManager.ResetHighlightings();

            // Reapply syntax highlighting to visible editors
            ReapplySyntaxHighlighting();
        }

        private void OnFilterRulesChanged(object? sender, EventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (_trace is null)
                return;
            try
            {
                _trace.View.RowFilter = FilterRuleSet.BuildRowFilter();
            }
            catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or InvalidExpressionException)
            {
                // Malformed rule; leave previous filter in place.
            }
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            var rule = new FilterRule
            {
                Combinator = (FilterCombinator)(RuleCombinatorCombo.SelectedItem ?? FilterCombinator.And),
                Field = (FilterField)(RuleFieldCombo.SelectedItem ?? FilterField.Response),
                Comparator = (FilterComparator)(RuleComparatorCombo.SelectedItem ?? FilterComparator.Equals),
                Value = RuleValueText.Text ?? string.Empty,
            };
            FilterRuleSet.Rules.Add(rule);
            RuleValueText.Clear();
        }

        private void RemoveRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is FilterRule rule)
                FilterRuleSet.Rules.Remove(rule);
        }

        private void ClearRules_Click(object sender, RoutedEventArgs e)
        {
            FilterRuleSet.Rules.Clear();
        }

        private void OnHighlightRulesChanged(object? sender, EventArgs e)
        {
            _trace?.RecomputeHighlights();
            // The Brush columns changed in-place; nudge the view to redraw.
            RequestList.Items.Refresh();
        }

        private void HighlightsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new HighlightsWindow { Owner = this };
            window.ShowDialog();
        }

        private void NextErrorButton_Click(object sender, RoutedEventArgs e)
            => NavigateToError(forward: true);

        private void PreviousErrorButton_Click(object sender, RoutedEventArgs e)
            => NavigateToError(forward: false);

        private void NavigateToError(bool forward)
        {
            var count = RequestList.Items.Count;
            if (count == 0)
                return;

            int start = RequestList.SelectedIndex;
            int step = forward ? 1 : -1;
            int startProbe = start < 0
                ? (forward ? 0 : count - 1)
                : ((start + step) % count + count) % count;

            for (int i = 0; i < count; i++)
            {
                int idx = ((startProbe + step * i) % count + count) % count;
                if (RequestList.Items[idx] is DataRowView drv
                    && drv.Row[TraceDataSchema.Response] is int code
                    && code >= 400 && code < 600)
                {
                    RequestList.SelectedItems.Clear();
                    RequestList.SelectedIndex = idx;
                    RequestList.ScrollIntoView(RequestList.Items[idx]);
                    return;
                }
            }
        }

        private void RequestList_HeaderClick(object sender, RoutedEventArgs e)
        {
            if (_trace is null)
                return;
            if (e.OriginalSource is not GridViewColumnHeader header)
                return;
            // The right-most padding header has a null Column.
            if (header.Column is null)
                return;

            var sortMember = GetSortMemberPath(header.Column);
            if (string.IsNullOrEmpty(sortMember))
                return;

            ListSortDirection? next;
            if (_sortColumn != header.Column)
            {
                next = ListSortDirection.Ascending;
            }
            else
            {
                next = _sortDirection switch
                {
                    ListSortDirection.Ascending => ListSortDirection.Descending,
                    ListSortDirection.Descending => null,
                    _ => ListSortDirection.Ascending,
                };
            }

            // Restore the previously-sorted column's header text.
            if (_sortColumn is not null && _sortColumn != header.Column &&
                _originalHeaders.TryGetValue(_sortColumn, out var prevOriginal))
            {
                _sortColumn.Header = prevOriginal;
            }

            if (next is null)
            {
                // Clear sort and restore header text.
                if (_originalHeaders.TryGetValue(header.Column, out var original))
                    header.Column.Header = original;
                _trace.View.Sort = string.Empty;
                _sortColumn = null;
                _sortDirection = null;
                return;
            }

            if (!_originalHeaders.ContainsKey(header.Column))
                _originalHeaders[header.Column] = header.Column.Header?.ToString() ?? string.Empty;

            var arrow = next == ListSortDirection.Ascending ? AscendingArrow : DescendingArrow;
            header.Column.Header = _originalHeaders[header.Column] + arrow;

            _trace.View.Sort = sortMember +
                (next == ListSortDirection.Ascending ? " ASC" : " DESC");

            _sortColumn = header.Column;
            _sortDirection = next;
        }

        private static string? GetSortMemberPath(GridViewColumn column)
        {
            if (column.DisplayMemberBinding is Binding binding)
                return binding.Path?.Path;
            return null;
        }

        private void ResetSortIndicator()
        {
            if (_sortColumn is not null &&
                _originalHeaders.TryGetValue(_sortColumn, out var original))
            {
                _sortColumn.Header = original;
            }
            _sortColumn = null;
            _sortDirection = null;
        }

        private readonly Dictionary<GridViewColumn, double> _savedColumnWidths = new();

        private void RemoveItems_Click(object sender, RoutedEventArgs e)
        {
            if (_trace is null || RequestList.SelectedItems.Count == 0)
                return;

            var indices = new List<int>(RequestList.SelectedItems.Count);
            foreach (var obj in RequestList.SelectedItems)
            {
                if (obj is DataRowView drv && drv.Row[TraceDataSchema.Index] is int idx)
                    indices.Add(idx);
            }

            foreach (var idx in indices)
                _trace.RemoveByIndex(idx);

            if (RequestList.SelectedItem is null)
                ClearViewers();
        }

        private void ColumnVisibility_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not GridViewColumn column)
                return;

            const double DefaultWidth = 200;

            if (item.IsChecked)
            {
                double width = _savedColumnWidths.TryGetValue(column, out var saved) && saved > 0
                    ? saved
                    : DefaultWidth;
                column.Width = width;
            }
            else
            {
                if (column.Width > 0)
                    _savedColumnWidths[column] = column.Width;
                column.Width = 0;
            }
        }

        private void AutoSizeColumns_Click(object sender, RoutedEventArgs e)
        {
            AutoSizeGridViewColumns();
        }

        /// <summary>
        /// Auto-sizes all visible GridView columns to fit their content.
        /// </summary>
        private void AutoSizeGridViewColumns()
        {
            if (RequestList.View is not GridView gridView)
                return;

            // First pass: measure all columns
            var columnMeasurements = new List<(GridViewColumn Column, double MeasuredWidth)>();

            foreach (var column in gridView.Columns)
            {
                // Skip hidden columns (width 0)
                if (column.Width == 0)
                    continue;

                // Set to auto to measure content
                column.Width = double.NaN;
                RequestList.UpdateLayout();

                // Get the measured width
                var measuredWidth = column.ActualWidth;
                columnMeasurements.Add((column, measuredWidth));
            }

            if (columnMeasurements.Count == 0)
                return;

            // Calculate available width (account for scrollbar, arrow indicator, padding)
            const double ScrollbarWidth = 20;
            const double ArrowIndicatorWidth = 24;
            const double SafetyMargin = 40;

            double availableWidth = RequestList.ActualWidth - ScrollbarWidth - ArrowIndicatorWidth - SafetyMargin;

            // Ensure we have a reasonable available width
            if (availableWidth < 300)
                availableWidth = 800; // Fallback if ListView hasn't been sized yet

            // Calculate total measured width with per-column max limits
            const double MinWidth = 50;
            const double MaxWidth = 300;  // Further reduced to ensure all columns fit
            const double Padding = 8;

            // Apply max width cap to measurements
            var cappedMeasurements = columnMeasurements
                .Select(cm => (cm.Column, Width: Math.Min(cm.MeasuredWidth, MaxWidth)))
                .ToList();

            double totalWidth = cappedMeasurements.Sum(cm => cm.Width) + Padding * cappedMeasurements.Count;

            if (totalWidth <= availableWidth)
            {
                // All columns fit - use capped widths with padding
                foreach (var (column, width) in cappedMeasurements)
                {
                    column.Width = Math.Clamp(width + Padding, MinWidth, MaxWidth);
                }
            }
            else
            {
                // Need to scale down - distribute available width proportionally
                double scale = availableWidth / totalWidth;

                foreach (var (column, width) in cappedMeasurements)
                {
                    double targetWidth = (width + Padding) * scale;
                    column.Width = Math.Clamp(targetWidth, MinWidth, MaxWidth);
                }
            }
        }

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open HTTP trace file",
                Filter = "HTTP trace files (*.saz;*.har;*.etl)|*.saz;*.har;*.etl|" +
                         "Fiddler session archive (*.saz)|*.saz|" +
                         "HTTP archive (*.har)|*.har|" +
                         "Event Trace for Windows (*.etl)|*.etl|" +
                         "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            var path = dialog.FileName;

            _loaderSummary = null;
            SetBusy(true, $"Loading {Path.GetFileName(path)}...");
            HttpTraceFile? loaded = null;
            Exception? error = null;
            try
            {
                loaded = await Task.Run(() => HttpTraceFile.Load(path)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                SetBusy(false);
            }

            if (error is not null)
            {
                _trace = null;
                MessageBox.Show(this, $"Failed to open trace file:\n{error.Message}",
                    "Open file", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                _trace = loaded;
            }

            if (_trace is not null)
                _loaderSummary = BuildLoaderSummary(_trace);

            PopulateList();
            ClearViewers();
            Title = _trace is null
                ? "HTTP Trace Analyser"
                : $"HTTP Trace Analyser - {Path.GetFileName(path)}";
        }

        private void SetBusy(bool busy, string? message = null)
        {
            if (busy)
            {
                if (!string.IsNullOrEmpty(message))
                    BusyText.Text = message;
                BusyOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                BusyOverlay.Visibility = Visibility.Collapsed;
            }
            OpenFileButton.IsEnabled = !busy;
        }

        private static FlowDocument? BuildLoaderSummary(HttpTraceFile trace)
        {
            var counts = trace.ProviderEventCounts;
            bool hasProviderCounts = counts is not null && counts.Count > 0;
            bool hasRows = trace.Count > 0;

            if (!hasProviderCounts && !hasRows)
                return null;

            var doc = NewDocument();
            AddSectionHeader(doc, "Trace summary");
            AddLine(doc, "File", Path.GetFileName(trace.FilePath));
            AddLine(doc, "Rows extracted", trace.Count.ToString());

            if (hasRows)
                AddRequestStatistics(doc, trace);

            if (hasProviderCounts)
            {
                AddLine(doc, "Distinct providers", counts!.Count.ToString());

                long total = 0;
                foreach (var v in counts.Values)
                    total += v;
                AddLine(doc, "Total events", total.ToString("N0"));

                AddSectionHeader(doc, "Provider event counts");
                foreach (var kvp in counts.OrderByDescending(k => k.Value))
                    AddLine(doc, kvp.Key, kvp.Value.ToString("N0"));
            }

            return doc;
        }

        /// <summary>
        /// Adds request/response statistics (time range, error/throttle counts, latency)
        /// computed from the trace's in-memory rows. Used for HAR and SAZ traces where
        /// each row represents a request/response pair.
        /// </summary>
        private static void AddRequestStatistics(FlowDocument doc, HttpTraceFile trace)
        {
            DateTime? earliest = null;
            DateTime? latest = null;
            int requestCount = 0;
            int errorCount = 0;
            int throttleCount = 0;
            double minLatency = double.MaxValue;
            double maxLatency = double.MinValue;
            double totalLatency = 0;
            int latencyCount = 0;

            foreach (DataRow row in trace.Messages.Rows)
            {
                requestCount++;

                if (row[TraceDataSchema.RequestTimestamp] is DateTime reqTs)
                {
                    if (earliest is null || reqTs < earliest)
                        earliest = reqTs;
                    if (latest is null || reqTs > latest)
                        latest = reqTs;
                }

                if (row[TraceDataSchema.ResponseTimestamp] is DateTime respTs)
                {
                    if (earliest is null || respTs < earliest)
                        earliest = respTs;
                    if (latest is null || respTs > latest)
                        latest = respTs;
                }

                if (row[TraceDataSchema.Response] is int statusCode)
                {
                    var statusInfo = HTTPStatusCodes.Instance.GetStatusInfo(statusCode);
                    if (statusInfo.IsError)
                        errorCount++;
                    if (statusInfo.IsThrottling)
                        throttleCount++;
                }

                if (row[TraceDataSchema.Latency] is double latency && latency >= 0)
                {
                    if (latency < minLatency)
                        minLatency = latency;
                    if (latency > maxLatency)
                        maxLatency = latency;
                    totalLatency += latency;
                    latencyCount++;
                }
            }

            AddSectionHeader(doc, "Trace time range");
            AddLine(doc, "Start of trace", earliest?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "(unknown)");
            AddLine(doc, "End of trace", latest?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "(unknown)");

            AddSectionHeader(doc, "Request statistics");
            AddLine(doc, "Number of requests", requestCount.ToString("N0"));
            AddLine(doc, "Number of errors", errorCount.ToString("N0"));
            AddLine(doc, "Number of throttle responses", throttleCount.ToString("N0"));

            AddSectionHeader(doc, "Latency (ms)");
            if (latencyCount > 0)
            {
                AddLine(doc, "Minimum latency", minLatency.ToString("N0"));
                AddLine(doc, "Maximum latency", maxLatency.ToString("N0"));
                AddLine(doc, "Average latency", (totalLatency / latencyCount).ToString("N0"));
            }
            else
            {
                AddLine(doc, "Minimum latency", "(unknown)");
                AddLine(doc, "Maximum latency", "(unknown)");
                AddLine(doc, "Average latency", "(unknown)");
            }
        }

        private void PopulateList()
        {
            ResetSortIndicator();
            RequestList.ItemsSource = _trace?.View;
            ApplyFilter();

            // Auto-size columns to fit content after loading
            if (_trace?.View != null && _trace.View.Count > 0)
            {
                AutoSizeGridViewColumns();
            }
        }

        private void ClearViewers()
        {
            SummaryViewer.Document = _loaderSummary ?? new FlowDocument();
            ApplyRichTextBoxWrap(SummaryViewer, _summaryWrap);
            MapiViewer.Document = new FlowDocument();
            ApplyRichTextBoxWrap(MapiViewer, _mapiWrap);

            _requestPayload = null;
            _requestHeaders = null;
            _responsePayload = null;
            _responseHeaders = null;

            RequestHeadersText.Text = string.Empty;
            ApplyRequestPayloadLayout(hasPayload: false);
            RequestNoPayloadText.Visibility = Visibility.Collapsed;
            _requestPayloadNeedsRender = false;

            ResponseHeadersText.Text = string.Empty;
            ApplyResponsePayloadLayout(hasPayload: false);
            ResponseNoPayloadText.Visibility = Visibility.Collapsed;
            _responsePayloadNeedsRender = false;
        }

        /// <summary>
        /// Intercepts tab clicks to show busy indicator BEFORE WPF performs expensive layout.
        /// For first-time tab activation or deferred rendering, we cancel the event,
        /// show the indicator, then manually switch tabs.
        /// </summary>
        private async void MainTabControl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isHandlingTabSwitch)
                return; // Prevent re-entrancy

            // Find which tab header was clicked
            var source = e.OriginalSource as DependencyObject;
            if (source == null)
                return;

            // Walk up the visual tree to find TabItem
            while (source != null && source is not TabItem)
            {
                // VisualTreeHelper only works with Visual elements.
                // FlowDocument and other FrameworkContentElements will throw an exception.
                if (source is Visual)
                    source = VisualTreeHelper.GetParent(source);
                else
                    break; // Can't continue up the visual tree from a non-Visual element
            }

            if (source is not TabItem clickedTab)
                return;

            // Get the index of the clicked tab
            var clickedIndex = MainTabControl.Items.IndexOf(clickedTab);

            // Check if this requires special handling
            bool isFirstTimeRequest = clickedIndex == 1 && !_requestTabEverActivated;
            bool isFirstTimeResponse = clickedIndex == 2 && !_responseTabEverActivated;
            bool willRenderRequest = clickedIndex == 1 && _requestPayloadNeedsRender;
            bool willRenderResponse = clickedIndex == 2 && _responsePayloadNeedsRender;

            bool needsSpecialHandling = isFirstTimeRequest || isFirstTimeResponse || willRenderRequest || willRenderResponse;

            if (!needsSpecialHandling)
                return; // Normal tab switch, no intervention needed

            // SPECIAL HANDLING REQUIRED
            // Cancel the event to prevent immediate tab switch
            e.Handled = true;

            _isHandlingTabSwitch = true;

            try
            {
                // Show busy indicator FIRST
                SetBusy(true, isFirstTimeRequest || isFirstTimeResponse ? "Initializing tab..." : "Loading payload...");

                // Force UI to update and render the indicator
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                await Task.Delay(50);

                // NOW switch the tab manually
                MainTabControl.SelectedIndex = clickedIndex;

                // SelectionChanged handler will do the rest
            }
            finally
            {
                _isHandlingTabSwitch = false;
            }
        }

        /// <summary>
        /// Handles tab switching to implement deferred rendering of AvalonEdit payloads.
        /// Large payloads are only rendered when their tab becomes visible, preventing UI hangs.
        /// </summary>
        private async void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != MainTabControl)
                return; // Ignore bubbled events from nested controls

            // Make tab content visible if it's the first time visiting this tab
            if (IsRequestTabSelected() && !_requestTabEverActivated)
            {
                _requestTabEverActivated = true;
                RequestViewerGrid.Visibility = Visibility.Visible;
            }
            else if (IsResponseTabSelected() && !_responseTabEverActivated)
            {
                _responseTabEverActivated = true;
                ResponseViewerGrid.Visibility = Visibility.Visible;
            }

            // Small delay to allow tab visual transition before starting heavy work
            await Task.Delay(10);

            // Render deferred Request payload if switching to Request tab
            if (_requestPayloadNeedsRender && IsRequestTabSelected())
            {
                _requestPayloadNeedsRender = false;
                await RenderRequestPayload(_pendingRequestFormat, showBusyIndicator: true);
            }

            // Render deferred Response payload if switching to Response tab
            if (_responsePayloadNeedsRender && IsResponseTabSelected())
            {
                _responsePayloadNeedsRender = false;
                await RenderResponsePayload(_pendingResponseFormat, showBusyIndicator: true);
            }

            // Clear busy indicator if no rendering was needed
            // (first-time tab activation without payload to render)
            SetBusy(false);
        }

        private bool IsRequestTabSelected()
        {
            return MainTabControl.SelectedIndex == 1; // Request is the second tab (index 1)
        }

        private bool IsResponseTabSelected()
        {
            return MainTabControl.SelectedIndex == 2; // Response is the third tab (index 2)
        }

        private async void RequestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_trace is null || RequestList.SelectedItem is not DataRowView drv)
            {
                ClearViewers();
                return;
            }

            HttpRequest request = _trace.GetRequest(drv.Row);
            HttpResponse? response = _trace.GetResponse(drv.Row);

            // Threshold for showing busy indicator (1MB) - same as in RenderPayload
            const int BusyIndicatorThreshold = 1_048_576;
            bool hasLargePayload = (request.Payload?.Length ?? 0) >= BusyIndicatorThreshold
                                   || (response?.Payload?.Length ?? 0) >= BusyIndicatorThreshold;

            try
            {
                if (hasLargePayload)
                {
                    SetBusy(true, "Loading trace data...");
                    // Allow UI to update and show the busy indicator
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                }

                SummaryViewer.Document = BuildSummary(request, response);
                ApplyRichTextBoxWrap(SummaryViewer, _summaryWrap);
                await PopulateRequestViewer(request);
                await PopulateResponseViewer(response);
                MapiViewer.Document = BuildMapiDocument(request, response);
                ApplyRichTextBoxWrap(MapiViewer, _mapiWrap);
            }
            finally
            {
                if (hasLargePayload)
                {
                    SetBusy(false);
                }
            }
        }

        private async Task PopulateRequestViewer(HttpRequest request)
        {
            _requestHeaders = request.Headers;
            _requestPayload = request.Payload;

            var sb = new StringBuilder();
            sb.Append(request.Method).Append(' ').Append(request.Url?.ToString() ?? string.Empty).AppendLine();
            AppendHeaderLines(sb, request.Headers);
            RequestHeadersText.Text = sb.ToString();

            bool hasPayload = _requestPayload is { Length: > 0 };
            ApplyRequestPayloadLayout(hasPayload);

            if (hasPayload)
            {
                var format = DetectPayloadFormat(request.Headers);

                // Suppress format change event during population
                _isPopulatingViewers = true;
                try
                {
                    RequestPayloadFormatCombo.SelectedIndex = (int)format;
                }
                finally
                {
                    _isPopulatingViewers = false;
                }

                // Defer rendering until the Request tab is visible
                _pendingRequestFormat = format;
                _requestPayloadNeedsRender = true;

                // If the Request tab is currently selected, render immediately
                if (IsRequestTabSelected())
                {
                    await RenderRequestPayload(format, showBusyIndicator: false);
                    _requestPayloadNeedsRender = false;
                }
            }
        }

        private async Task PopulateResponseViewer(HttpResponse? response)
        {
            if (response is null)
            {
                _responseHeaders = null;
                _responsePayload = null;
                ResponseHeadersText.Text = "(no response captured)";
                ApplyResponsePayloadLayout(hasPayload: false);
                _responsePayloadNeedsRender = false;
                return;
            }

            _responseHeaders = response.Headers;
            _responsePayload = response.Payload;

            var sb = new StringBuilder();
            var status = GetResponseStatus(response);
            if (!string.IsNullOrEmpty(status))
                sb.AppendLine(status);
            AppendHeaderLines(sb, response.Headers);
            ResponseHeadersText.Text = sb.ToString();

            bool hasPayload = _responsePayload is { Length: > 0 };
            ApplyResponsePayloadLayout(hasPayload);

            if (hasPayload)
            {
                var format = DetectPayloadFormat(response.Headers);

                // Suppress format change event during population
                _isPopulatingViewers = true;
                try
                {
                    ResponsePayloadFormatCombo.SelectedIndex = (int)format;
                }
                finally
                {
                    _isPopulatingViewers = false;
                }

                // Defer rendering until the Response tab is visible
                _pendingResponseFormat = format;
                _responsePayloadNeedsRender = true;

                // If the Response tab is currently selected, render immediately
                if (IsResponseTabSelected())
                {
                    await RenderResponsePayload(format, showBusyIndicator: false);
                    _responsePayloadNeedsRender = false;
                }
            }
            else
            {
                _responsePayloadNeedsRender = false;
            }
        }

        private void ApplyRequestPayloadLayout(bool hasPayload)
        {
            if (!hasPayload)
            {
                RequestPayloadImageScroll.Visibility = Visibility.Collapsed;
                RequestPayloadSvgScroll.Visibility = Visibility.Collapsed;
                RequestPayloadImage.Source = null;
                RequestPayloadSvg.StreamSource = null;
            }
            ApplyPayloadLayout(
                hasPayload,
                RequestViewerGrid,
                RequestHeadersRow,
                RequestHeadersText,
                RequestSplitterRow,
                RequestPayloadSplitter,
                RequestPayloadFormatPanel,
                RequestPayloadRow,
                RequestPayloadEditor,
                RequestNoPayloadText);
        }

        private void ApplyResponsePayloadLayout(bool hasPayload)
        {
            if (!hasPayload)
            {
                ResponsePayloadImageScroll.Visibility = Visibility.Collapsed;
                ResponsePayloadSvgScroll.Visibility = Visibility.Collapsed;
                ResponsePayloadImage.Source = null;
                ResponsePayloadSvg.StreamSource = null;
            }
            ApplyPayloadLayout(
                hasPayload,
                ResponseViewerGrid,
                ResponseHeadersRow,
                ResponseHeadersText,
                ResponseSplitterRow,
                ResponsePayloadSplitter,
                ResponsePayloadFormatPanel,
                ResponsePayloadRow,
                ResponsePayloadEditor,
                ResponseNoPayloadText);
        }

        private static void ApplyPayloadLayout(
            bool hasPayload,
            Grid viewerGrid,
            RowDefinition headersRow,
            TextBox headersText,
            RowDefinition splitterRow,
            GridSplitter splitter,
            FrameworkElement formatPanel,
            RowDefinition payloadRow,
            ICSharpCode.AvalonEdit.TextEditor editor,
            FrameworkElement noPayloadText)
        {
            if (hasPayload)
            {
                // Headers auto-size but capped at half of the viewer.
                headersRow.Height = GridLength.Auto;
                headersText.MaxHeight = Math.Max(0, viewerGrid.ActualHeight / 2);
                splitterRow.Height = new GridLength(4);
                splitter.Visibility = Visibility.Visible;
                formatPanel.Visibility = Visibility.Visible;
                payloadRow.Height = new GridLength(1, GridUnitType.Star);
                editor.Visibility = Visibility.Visible;
                noPayloadText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Headers take all available space; payload area collapses to the message.
                headersRow.Height = new GridLength(1, GridUnitType.Star);
                headersText.MaxHeight = double.PositiveInfinity;
                splitterRow.Height = new GridLength(0);
                splitter.Visibility = Visibility.Collapsed;
                formatPanel.Visibility = Visibility.Collapsed;
                payloadRow.Height = GridLength.Auto;
                editor.Visibility = Visibility.Collapsed;
                editor.Text = string.Empty;
                editor.SyntaxHighlighting = null;
                noPayloadText.Visibility = Visibility.Visible;
            }
        }

        private void WordWrap_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi)
                return;

            var target = (mi.Parent as ContextMenu)?.PlacementTarget;
            bool wrap = mi.IsChecked;

            switch (target)
            {
                case TextBox tb:
                    tb.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    break;
                case ICSharpCode.AvalonEdit.TextEditor ed:
                    ed.WordWrap = wrap;
                    break;
                case RichTextBox rtb:
                    if (rtb == SummaryViewer) _summaryWrap = wrap;
                    else if (rtb == MapiViewer) _mapiWrap = wrap;
                    ApplyRichTextBoxWrap(rtb, wrap);
                    break;
            }
        }

        private static void ApplyRichTextBoxWrap(RichTextBox rtb, bool wrap)
        {
            if (rtb.Document is null)
                return;
            // NaN = auto = wraps to the viewport width. WPF does not accept
            // double.PositiveInfinity for PageWidth (throws ArgumentException), so we
            // always use NaN and let the document auto-size to the viewport.
            rtb.Document.PageWidth = double.NaN;
        }

        private void RequestViewerGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_requestPayload is { Length: > 0 })
                RequestHeadersText.MaxHeight = Math.Max(0, RequestViewerGrid.ActualHeight / 2);
        }

        private void ResponseViewerGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_responsePayload is { Length: > 0 })
                ResponseHeadersText.MaxHeight = Math.Max(0, ResponseViewerGrid.ActualHeight / 2);
        }

        private async void RequestPayloadFormat_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Ignore during population - format is set programmatically, not by user
            if (_isPopulatingViewers || _requestPayload is null)
                return;
            await RenderRequestPayload((PayloadFormat)RequestPayloadFormatCombo.SelectedIndex);
        }

        private async void ResponsePayloadFormat_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Ignore during population - format is set programmatically, not by user
            if (_isPopulatingViewers || _responsePayload is null)
                return;
            await RenderResponsePayload((PayloadFormat)ResponsePayloadFormatCombo.SelectedIndex);
        }

        private Task RenderRequestPayload(PayloadFormat format, bool showBusyIndicator = true)
            => RenderPayload(format, _requestPayload!, _requestHeaders,
                RequestPayloadEditor, RequestPayloadImageScroll, RequestPayloadImage,
                RequestPayloadSvgScroll, RequestPayloadSvg, showBusyIndicator);

        private Task RenderResponsePayload(PayloadFormat format, bool showBusyIndicator = true)
            => RenderPayload(format, _responsePayload!, _responseHeaders,
                ResponsePayloadEditor, ResponsePayloadImageScroll, ResponsePayloadImage,
                ResponsePayloadSvgScroll, ResponsePayloadSvg, showBusyIndicator);

        private async Task RenderPayload(
            PayloadFormat format,
            byte[] payload,
            IReadOnlyList<KeyValuePair<string, string>>? headers,
            ICSharpCode.AvalonEdit.TextEditor editor,
            ScrollViewer imageScroll, Image imageControl,
            ScrollViewer svgScroll, SvgViewbox svgControl,
            bool showBusyIndicator = true)
        {
            // Threshold for showing busy indicator (1MB)
            const int BusyIndicatorThreshold = 1_048_576;
            bool shouldShowBusy = showBusyIndicator && payload.Length >= BusyIndicatorThreshold;

            try
            {
                if (shouldShowBusy)
                {
                    SetBusy(true, "Loading payload...");
                    // Give the busy indicator time to fully render
                    await Task.Delay(50); // Short delay to ensure overlay is visible
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                }

                if (payload.Length == 0)
                {
                    ShowEditor(editor, imageScroll, svgScroll);
                    editor.Text = string.Empty;
                    editor.SyntaxHighlighting = null;
                    return;
                }

                switch (format)
                {
                    case PayloadFormat.Image:
                        if (TryLoadBitmap(payload, out var bitmap))
                        {
                            ShowImage(editor, imageScroll, svgScroll);
                            imageControl.Source = bitmap;
                        }
                        else
                        {
                            ShowEditor(editor, imageScroll, svgScroll);
                            editor.Text = $"[Unable to decode as image: {payload.Length} byte(s)]";
                            editor.SyntaxHighlighting = null;
                        }
                        return;

                    case PayloadFormat.Svg:
                        if (TryLoadSvg(payload, svgControl))
                        {
                            ShowSvg(editor, imageScroll, svgScroll);
                        }
                        else
                        {
                            ShowEditor(editor, imageScroll, svgScroll);
                            editor.Text = $"[Unable to decode as SVG: {payload.Length} byte(s)]";
                            editor.SyntaxHighlighting = null;
                        }
                        return;
                }

                // Text-based formats route through the AvalonEdit editor.
                ShowEditor(editor, imageScroll, svgScroll);

                // Decode payload on background thread for large payloads
                string text;
                if (shouldShowBusy)
                {
                    text = await Task.Run(() => DecodePayloadText(payload, headers));
                }
                else
                {
                    text = DecodePayloadText(payload, headers);
                }

                // Set text immediately WITHOUT syntax highlighting to show content right away
                editor.SyntaxHighlighting = null;

                // For very large files, skip pretty-printing as it's slow and disable word wrap
                const int LargeFileThreshold = 100_000; // 100KB
                bool isLargeFile = text.Length > LargeFileThreshold;

                // Pretty-print on background thread for large files
                string displayText;
                if (shouldShowBusy)
                {
                    displayText = await Task.Run(() =>
                    {
                        switch (format)
                        {
                            case PayloadFormat.Json:
                                return isLargeFile ? text : (TryPrettyPrintJson(text, out var pretty) ? pretty : text);
                            case PayloadFormat.Xml:
                                return isLargeFile ? text : (TryPrettyPrintXml(text, out var xml) ? xml : text);
                            default:
                                return text;
                        }
                    });
                }
                else
                {
                    switch (format)
                    {
                        case PayloadFormat.Json:
                            displayText = isLargeFile ? text : (TryPrettyPrintJson(text, out var pretty) ? pretty : text);
                            break;
                        case PayloadFormat.Xml:
                            displayText = isLargeFile ? text : (TryPrettyPrintXml(text, out var xml) ? xml : text);
                            break;
                        default:
                            displayText = text;
                            break;
                    }
                }

                // For large files, disable performance-intensive features
                if (isLargeFile)
                {
                    editor.WordWrap = false;
                    editor.ShowLineNumbers = false; // Line numbers are expensive with many lines
                }
                else
                {
                    // Re-enable for smaller files (in case user switched from large to small)
                    editor.ShowLineNumbers = true;
                }

                // Always disable hyperlink regex processing to prevent catastrophic backtracking
                // on any payload content, regardless of size
                editor.Options.EnableHyperlinks = false;
                editor.Options.EnableEmailHyperlinks = false;

                // For large payloads, set text in chunks to allow UI updates
                if (shouldShowBusy)
                {
                    // Update status message to indicate we're setting content
                    SetBusy(true, "Rendering payload...");

                    // Yield to ensure the status update is visible
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);

                    // Set the text - this is the expensive operation
                    // Run at Background priority so the UI can remain responsive
                    await editor.Dispatcher.InvokeAsync(() =>
                    {
                        editor.Text = displayText;
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                else
                {
                    // Small payload, set directly
                    editor.Text = displayText;
                }

                // Apply syntax highlighting asynchronously on background thread to avoid UI freeze
                ApplySyntaxHighlightingAsync(editor, format);
            }
            finally
            {
                if (shouldShowBusy)
                {
                    SetBusy(false);
                }
            }
        }

        /// <summary>
        /// Applies syntax highlighting asynchronously to avoid blocking the UI thread.
        /// This allows the text to be displayed immediately while highlighting is applied in the background.
        /// </summary>
        private static async void ApplySyntaxHighlightingAsync(ICSharpCode.AvalonEdit.TextEditor editor, PayloadFormat format)
        {
            // Yield to let the UI thread render the plain text first
            await editor.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            IHighlightingDefinition? highlighting = null;

            // Get the highlighting definition on a background thread
            await Task.Run(() =>
            {
                highlighting = format switch
                {
                    PayloadFormat.Json => SyntaxHighlightingManager.GetHighlighting("json"),
                    PayloadFormat.Xml => SyntaxHighlightingManager.GetHighlighting("xml"),
                    PayloadFormat.Html => SyntaxHighlightingManager.GetHighlighting("html"),
                    PayloadFormat.JavaScript => SyntaxHighlightingManager.GetHighlighting("javascript"),
                    _ => null
                };
            });

            // Apply the highlighting back on the UI thread
            editor.SyntaxHighlighting = highlighting;
        }

        /// <summary>
        /// Reapplies syntax highlighting to visible payload editors when the theme changes.
        /// </summary>
        private void ReapplySyntaxHighlighting()
        {
            // Reapply highlighting to request payload editor if it has text
            if (!string.IsNullOrEmpty(RequestPayloadEditor.Text))
            {
                var requestFormat = (PayloadFormat)RequestPayloadFormatCombo.SelectedIndex;
                if (requestFormat is PayloadFormat.Json or PayloadFormat.Xml or PayloadFormat.Html or PayloadFormat.JavaScript)
                {
                    ApplySyntaxHighlightingAsync(RequestPayloadEditor, requestFormat);
                }
            }

            // Reapply highlighting to response payload editor if it has text
            if (!string.IsNullOrEmpty(ResponsePayloadEditor.Text))
            {
                var responseFormat = (PayloadFormat)ResponsePayloadFormatCombo.SelectedIndex;
                if (responseFormat is PayloadFormat.Json or PayloadFormat.Xml or PayloadFormat.Html or PayloadFormat.JavaScript)
                {
                    ApplySyntaxHighlightingAsync(ResponsePayloadEditor, responseFormat);
                }
            }
        }

        private static void ShowEditor(FrameworkElement editor, FrameworkElement imageScroll, FrameworkElement svgScroll)
        {
            editor.Visibility = Visibility.Visible;
            imageScroll.Visibility = Visibility.Collapsed;
            svgScroll.Visibility = Visibility.Collapsed;
        }

        private static void ShowImage(FrameworkElement editor, FrameworkElement imageScroll, FrameworkElement svgScroll)
        {
            editor.Visibility = Visibility.Collapsed;
            imageScroll.Visibility = Visibility.Visible;
            svgScroll.Visibility = Visibility.Collapsed;
        }

        private static void ShowSvg(FrameworkElement editor, FrameworkElement imageScroll, FrameworkElement svgScroll)
        {
            editor.Visibility = Visibility.Collapsed;
            imageScroll.Visibility = Visibility.Collapsed;
            svgScroll.Visibility = Visibility.Visible;
        }

        private static bool TryLoadBitmap(byte[] payload, out BitmapImage bitmap)
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(payload, writable: false);
                img.EndInit();
                img.Freeze();
                bitmap = img;
                return true;
            }
            catch
            {
                bitmap = null!;
                return false;
            }
        }

        private static bool TryLoadSvg(byte[] payload, SvgViewbox svgControl)
        {
            try
            {
                svgControl.StreamSource = new MemoryStream(payload, writable: false);
                return true;
            }
            catch
            {
                svgControl.StreamSource = null;
                return false;
            }
        }

        private static PayloadFormat DetectPayloadFormat(IReadOnlyList<KeyValuePair<string, string>>? headers)
        {
            if (headers is null)
                return PayloadFormat.PlainText;
            foreach (var h in headers)
            {
                if (!string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;
                var v = h.Value ?? string.Empty;
                if (v.Contains("svg", StringComparison.OrdinalIgnoreCase))
                    return PayloadFormat.Svg;
                if (v.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return PayloadFormat.Image;
                if (v.Contains("json", StringComparison.OrdinalIgnoreCase))
                    return PayloadFormat.Json;
                if (v.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("ecmascript", StringComparison.OrdinalIgnoreCase))
                    return PayloadFormat.JavaScript;
                if (v.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return PayloadFormat.Html;
                if (v.Contains("xml", StringComparison.OrdinalIgnoreCase))
                    return PayloadFormat.Xml;
                break;
            }
            return PayloadFormat.PlainText;
        }

        private static string DecodePayloadText(byte[] payload, IReadOnlyList<KeyValuePair<string, string>>? headers)
        {
            var encoding = Encoding.UTF8;
            if (headers is not null)
            {
                foreach (var h in headers)
                {
                    if (!string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = h.Value ?? string.Empty;
                    var idx = value.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var charset = value[(idx + 8)..].Trim().Trim('"', ';').Split(';')[0].Trim();
                        try { encoding = Encoding.GetEncoding(charset); } catch { }
                    }
                    break;
                }
            }

            try { return encoding.GetString(payload); }
            catch { return $"[{payload.Length} bytes of binary data]"; }
        }

        private static bool TryPrettyPrintJson(string text, out string pretty)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                pretty = JsonSerializer.Serialize(doc.RootElement, JsonPrettyOptions);
                return true;
            }
            catch
            {
                pretty = text;
                return false;
            }
        }

        private static readonly JsonSerializerOptions JsonPrettyOptions = new() { WriteIndented = true };

        private static bool TryPrettyPrintXml(string text, out string pretty)
        {
            try
            {
                var doc = XDocument.Parse(text);
                pretty = doc.ToString(SaveOptions.None);
                return true;
            }
            catch
            {
                pretty = text;
                return false;
            }
        }

        private static void AppendHeaderLines(StringBuilder sb, IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            if (headers is null)
                return;
            foreach (var h in headers)
                sb.Append(h.Key).Append(": ").AppendLine(h.Value);
        }

        private static FlowDocument BuildSummary(HttpRequest request, HttpResponse? response)
        {
            var doc = NewDocument();

            AddSectionHeader(doc, "Request");
            AddLine(doc, "Method", request.Method);
            AddLine(doc, "URL", request.Url?.ToString() ?? string.Empty);
            AddLine(doc, "Timestamp", FormatTimestamp(request.Timestamp));
            AddLine(doc, "Payload size", request.Payload.Length + " byte(s)");

            AddSectionHeader(doc, "Response");
            if (response is null)
            {
                doc.Blocks.Add(new Paragraph(new Run("(no response captured)") { FontStyle = FontStyles.Italic }));
            }
            else
            {
                AddStatusLine(doc, response);
                AddLine(doc, "Timestamp", FormatTimestamp(response.Timestamp));
                AddLine(doc, "Payload size", response.Payload.Length + " byte(s)");
            }

            return doc;
        }

        private static void AddStatusLine(FlowDocument doc, HttpResponse response)
        {
            // Create the status paragraph
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run("Status: ") { FontWeight = FontWeights.Bold });
            p.Inlines.Add(new Run(GetResponseStatus(response)));

            // Apply highlight rules based on status code
            if (response.StatusCode.HasValue)
            {
                // Create a simple object with Response property for highlight matching
                var highlightItem = new { Response = response.StatusCode.Value };
                var matchedRule = HighlightRuleSet.Match(highlightItem);

                if (matchedRule is not null)
                {
                    p.Background = new SolidColorBrush(matchedRule.BackgroundColor);

                    // If rule has explicit foreground, use it; otherwise choose contrasting color
                    if (matchedRule.ForegroundColor.HasValue)
                    {
                        p.Foreground = new SolidColorBrush(matchedRule.ForegroundColor.Value);
                    }
                    else
                    {
                        // Calculate brightness and use black for light backgrounds, white for dark
                        p.Foreground = GetContrastingForeground(matchedRule.BackgroundColor);
                    }
                }

                // Add status description if available
                var statusInfo = HTTPStatusCodes.Instance.GetStatusInfo(response.StatusCode.Value);
                if (!string.IsNullOrEmpty(statusInfo.Description))
                {
                    p.Inlines.Add(new LineBreak());
                    p.Inlines.Add(new Run(statusInfo.Description) { FontStyle = FontStyles.Italic });
                }
            }

            doc.Blocks.Add(p);
        }

        private static FlowDocument BuildMapiDocument(HttpRequest request, HttpResponse? response)
        {
            var doc = NewDocument();

            bool reqIsMapi = MapiHttpDecoder.IsMapiHttp(request);
            bool respIsMapi = MapiHttpDecoder.IsMapiHttp(response);

            if (!reqIsMapi && !respIsMapi)
            {
                doc.Blocks.Add(new Paragraph(new Run("(no MAPI/HTTP content detected)")
                { FontStyle = FontStyles.Italic }));
                return doc;
            }

            if (reqIsMapi)
                AppendMapiSection(doc, "Request", MapiHttpDecoder.Decode(request, isResponse: false), request.Payload);

            if (respIsMapi && response is not null)
                AppendMapiSection(doc, "Response", MapiHttpDecoder.Decode(response, isResponse: true), response.Payload);

            return doc;
        }

        private static void AppendMapiSection(FlowDocument doc, string title, MapiDecodeResult decoded, byte[] payload)
        {
            AddSectionHeader(doc, title);

            foreach (var h in decoded.MapiHeaders)
                AddLine(doc, h.Key, h.Value);

            if (decoded.MetaTags.Count > 0)
                AddLine(doc, "Meta-tags", string.Join(" -> ", decoded.MetaTags));

            var bodyPara = new Paragraph
            {
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 4, 0, 8),
            };
            bodyPara.Inlines.Add(new Run($"Body ({decoded.BodyLength} byte(s), offset 0x{decoded.BodyOffset:X}):")
            { FontWeight = FontWeights.Bold });
            bodyPara.Inlines.Add(new LineBreak());
            bodyPara.Inlines.Add(new Run(MapiHttpDecoder.HexDump(payload, decoded.BodyOffset, decoded.BodyLength)));
            doc.Blocks.Add(bodyPara);
        }

        private static FlowDocument NewDocument() => new()
        {
            FontFamily = new FontFamily("Consolas"),
            PagePadding = new Thickness(6),
        };

        private static void AddSectionHeader(FlowDocument doc, string text)
        {
            var p = new Paragraph(new Run(text)
            {
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
            })
            {
                Background = Brushes.SteelBlue,
                Margin = new Thickness(0, 4, 0, 4),
                Padding = new Thickness(4, 2, 4, 2),
            };
            doc.Blocks.Add(p);
        }

        private static void AddLine(FlowDocument doc, string label, string value)
        {
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run(label + ": ") { FontWeight = FontWeights.Bold });
            p.Inlines.Add(new Run(value ?? string.Empty));
            doc.Blocks.Add(p);
        }

        private static string GetResponseStatus(HttpResponse response)
        {
            if (response.StatusCode is null)
                return string.Empty;
            return string.IsNullOrEmpty(response.ReasonPhrase)
                ? response.StatusCode.Value.ToString()
                : $"{response.StatusCode.Value} {response.ReasonPhrase}";
        }

        private static string FormatTimestamp(DateTimeOffset? timestamp)
            => timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "(unknown)";

        /// <summary>
        /// Returns a contrasting foreground brush (black or white) based on the brightness
        /// of the given background color, ensuring text remains readable.
        /// </summary>
        private static Brush GetContrastingForeground(Color backgroundColor)
        {
            // Calculate relative luminance using the standard formula (Rec. 709)
            // https://www.w3.org/TR/WCAG20/#relativeluminancedef
            double r = backgroundColor.R / 255.0;
            double g = backgroundColor.G / 255.0;
            double b = backgroundColor.B / 255.0;

            // Apply gamma correction
            r = r <= 0.03928 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
            g = g <= 0.03928 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
            b = b <= 0.03928 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

            double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;

            // Use white text for dark backgrounds (luminance < 0.5), black for light backgrounds
            return luminance < 0.5 ? Brushes.White : Brushes.Black;
        }

            }
        }