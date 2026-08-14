using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using HttpTraceAnalyser.Model;
using ICSharpCode.AvalonEdit.Highlighting;
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
        // Default (unchecked) = no wrap.
        private bool _summaryWrap;
        private bool _mapiWrap;
        private const double NoWrapPageWidth = 5000d;

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

        public MainWindow()
        {
            InitializeComponent();
            HighlightRuleSet.RulesChanged += OnHighlightRulesChanged;
            FilterRuleSet.FiltersChanged += OnFilterRulesChanged;
            ActiveFiltersList.ItemsSource = FilterRuleSet.Rules;
            Closed += (_, _) =>
            {
                HighlightRuleSet.RulesChanged -= OnHighlightRulesChanged;
                FilterRuleSet.FiltersChanged -= OnFilterRulesChanged;
            };
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
            if (counts is null || counts.Count == 0)
                return null;

            var doc = NewDocument();
            AddSectionHeader(doc, "Trace summary");
            AddLine(doc, "File", Path.GetFileName(trace.FilePath));
            AddLine(doc, "Rows extracted", trace.Count.ToString());
            AddLine(doc, "Distinct providers", counts.Count.ToString());

            long total = 0;
            foreach (var v in counts.Values)
                total += v;
            AddLine(doc, "Total events", total.ToString("N0"));

            AddSectionHeader(doc, "Provider event counts");
            foreach (var kvp in counts.OrderByDescending(k => k.Value))
                AddLine(doc, kvp.Key, kvp.Value.ToString("N0"));

            return doc;
        }

        private void PopulateList()
        {
            ResetSortIndicator();
            RequestList.ItemsSource = _trace?.View;
            ApplyFilter();
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

            ResponseHeadersText.Text = string.Empty;
            ApplyResponsePayloadLayout(hasPayload: false);
            ResponseNoPayloadText.Visibility = Visibility.Collapsed;
        }

        private void RequestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_trace is null || RequestList.SelectedItem is not DataRowView drv)
            {
                ClearViewers();
                return;
            }

            var request = _trace.GetRequest(drv.Row);
            var response = _trace.GetResponse(drv.Row);

            SummaryViewer.Document = BuildSummary(request, response);
            ApplyRichTextBoxWrap(SummaryViewer, _summaryWrap);
            PopulateRequestViewer(request);
            PopulateResponseViewer(response);
            MapiViewer.Document = BuildMapiDocument(request, response);
            ApplyRichTextBoxWrap(MapiViewer, _mapiWrap);
        }

        private void PopulateRequestViewer(HttpRequest request)
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
                RequestPayloadFormatCombo.SelectedIndex = (int)format;
                RenderRequestPayload(format);
            }
        }

        private void PopulateResponseViewer(HttpResponse? response)
        {
            if (response is null)
            {
                _responseHeaders = null;
                _responsePayload = null;
                ResponseHeadersText.Text = "(no response captured)";
                ApplyResponsePayloadLayout(hasPayload: false);
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
                ResponsePayloadFormatCombo.SelectedIndex = (int)format;
                RenderResponsePayload(format);
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
            // NaN = auto = wraps to container; a large fixed page width prevents wrapping
            // and lets the horizontal scroll bar appear.
            rtb.Document.PageWidth = wrap ? double.NaN : NoWrapPageWidth;
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

        private void RequestPayloadFormat_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_requestPayload is null)
                return;
            RenderRequestPayload((PayloadFormat)RequestPayloadFormatCombo.SelectedIndex);
        }

        private void ResponsePayloadFormat_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_responsePayload is null)
                return;
            RenderResponsePayload((PayloadFormat)ResponsePayloadFormatCombo.SelectedIndex);
        }

        private void RenderRequestPayload(PayloadFormat format)
            => RenderPayload(format, _requestPayload!, _requestHeaders,
                RequestPayloadEditor, RequestPayloadImageScroll, RequestPayloadImage,
                RequestPayloadSvgScroll, RequestPayloadSvg);

        private void RenderResponsePayload(PayloadFormat format)
            => RenderPayload(format, _responsePayload!, _responseHeaders,
                ResponsePayloadEditor, ResponsePayloadImageScroll, ResponsePayloadImage,
                ResponsePayloadSvgScroll, ResponsePayloadSvg);

        private static void RenderPayload(
            PayloadFormat format,
            byte[] payload,
            IReadOnlyList<KeyValuePair<string, string>>? headers,
            ICSharpCode.AvalonEdit.TextEditor editor,
            ScrollViewer imageScroll, Image imageControl,
            ScrollViewer svgScroll, SvgViewbox svgControl)
        {
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
            var text = DecodePayloadText(payload, headers);

            switch (format)
            {
                case PayloadFormat.Json:
                    editor.Text = TryPrettyPrintJson(text, out var pretty) ? pretty : text;
                    editor.SyntaxHighlighting =
                        HighlightingManager.Instance.GetDefinitionByExtension(".json")
                        ?? HighlightingManager.Instance.GetDefinition("Json");
                    break;
                case PayloadFormat.Xml:
                    editor.Text = TryPrettyPrintXml(text, out var xml) ? xml : text;
                    editor.SyntaxHighlighting =
                        HighlightingManager.Instance.GetDefinitionByExtension(".xml")
                        ?? HighlightingManager.Instance.GetDefinition("XML");
                    break;
                case PayloadFormat.Html:
                    editor.Text = text;
                    editor.SyntaxHighlighting =
                        HighlightingManager.Instance.GetDefinitionByExtension(".html")
                        ?? HighlightingManager.Instance.GetDefinitionByExtension(".htm")
                        ?? HighlightingManager.Instance.GetDefinition("HTML");
                    break;
                case PayloadFormat.JavaScript:
                    editor.Text = text;
                    editor.SyntaxHighlighting =
                        HighlightingManager.Instance.GetDefinitionByExtension(".js")
                        ?? HighlightingManager.Instance.GetDefinition("JavaScript");
                    break;
                default:
                    editor.Text = text;
                    editor.SyntaxHighlighting = null;
                    break;
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
                AddLine(doc, "Status", GetResponseStatus(response));
                AddLine(doc, "Timestamp", FormatTimestamp(response.Timestamp));
                AddLine(doc, "Payload size", response.Payload.Length + " byte(s)");
            }

            return doc;
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

            }
        }