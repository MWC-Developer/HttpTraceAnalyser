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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

        /// <summary>Currently loaded trace file, if any. Exposed for external automation (e.g. the in-process MCP server).</summary>
        public HttpTraceFile? Trace => _trace;

        // Cached request/response payload + headers for the currently selected row,
        // so switching the payload format doesn't require re-fetching from the DataTable.
        private byte[]? _requestPayload;
        private IReadOnlyList<KeyValuePair<string, string>>? _requestHeaders;
        private byte[]? _responsePayload;
        private IReadOnlyList<KeyValuePair<string, string>>? _responseHeaders;

        private enum PayloadFormat { PlainText = 0, Json = 1, Xml = 2, Html = 3, JavaScript = 4, Image = 5, Svg = 6 }
        private enum FindScope { AllSessions = 0, RequestHeaders = 1, RequestBody = 2, ResponseHeaders = 3, ResponseBody = 4 }

        // Word-wrap state for the RichTextBox viewers (Summary, Mapi). RichTextBox has
        // no built-in wrap toggle; we simulate it by pinning Document.PageWidth. State
        // is tracked here so that rebuilding the FlowDocument preserves the user's choice.
        // Default (checked) = wrap, so no unnecessary horizontal scroll bar is shown.
        private bool _summaryWrap = true;
        private bool _mapiWrap = true;
        private bool _restWrap = true;
        private bool _soapWrap = true;

        // Cached loader-level summary (e.g. ETL provider event counts). Shown in the
        // Summary viewer whenever no row is selected, so it stays visible even when
        // the loader extracted no HTTP messages.
        private FlowDocument? _loaderSummary;

        // Column-sort state. Cycle per column: none -> ascending -> descending -> none.
        private GridViewColumn? _sortColumn;
        private ListSortDirection? _sortDirection;
        private readonly Dictionary<GridViewColumn, string> _originalHeaders = new();
        private readonly Dictionary<string, GridViewColumn> _userDefinedGridColumns =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MenuItem> _userDefinedColumnMenuItems = new();

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

        // Large payloads skip pretty-printing/word-wrap/highlighting for performance (see
        // LargePayloadThreshold). These track actual size and whether the user chose to
        // force full formatting anyway (which also disables Find for that payload, since
        // formatting+highlighting a huge body reintroduces the same slowness Find just fixed).
        private bool _requestPayloadIsLarge;
        private bool _responsePayloadIsLarge;
        private bool _requestPayloadFormatOverride;
        private bool _responsePayloadFormatOverride;

        // Track if we're currently switching tabs to prevent re-entrancy
        private bool _isHandlingTabSwitch;

        // Track if we're populating viewers to suppress format change events
        private bool _isPopulatingViewers;

        private bool _isSplitView;
        private GridLength _sessionsLeftWidth = new(900);
        private GridLength _sessionsTopHeight = new(2, GridUnitType.Star);

        private FrameworkElement[] _middleMouseScrollTargets = [];
        private FrameworkElement? _middleMouseScrollTarget;
        private ScrollViewer? _middleMouseScrollViewer;
        private DispatcherTimer? _middleMouseScrollTimer;
        private bool _middleMouseAutoScrollActive;
        private Point _middleMouseScrollAnchor;
        private Point _middleMouseScrollPosition;
        private Cursor? _previousOverrideCursor;
        private HwndSource? _windowSource;
        private const int WmMouseHorizontalWheel = 0x020E;

        // Above this size, pretty-printing, word-wrap, line numbers and syntax highlighting are
        // skipped: colorizing (and word-wrapping) a huge single-line JSON/XML body is what makes
        // scrolling/selecting (e.g. via Find) feel terrible, since it re-runs regex highlighting
        // rules over the whole line on every redraw.
        private const int LargePayloadThreshold = 100_000; // 100KB

        public MainWindow()
        {
            InitializeComponent();

            _middleMouseScrollTargets =
            [
                RequestList,
                RequestHeadersText,
                RequestPayloadEditor,
                RequestPayloadImageScroll,
                RequestPayloadSvgScroll,
                ResponseHeadersText,
                ResponsePayloadEditor,
                ResponsePayloadImageScroll,
                ResponsePayloadSvgScroll,
            ];
            foreach (var target in _middleMouseScrollTargets)
            {
                target.AddHandler(Mouse.PreviewMouseDownEvent,
                    new MouseButtonEventHandler(ScrollableViewer_PreviewMouseDown), handledEventsToo: true);
                target.AddHandler(Mouse.PreviewMouseMoveEvent,
                    new MouseEventHandler(ScrollableViewer_PreviewMouseMove), handledEventsToo: true);
                target.AddHandler(Mouse.LostMouseCaptureEvent,
                    new MouseEventHandler(ScrollableViewer_LostMouseCapture), handledEventsToo: true);
            }
            AddHandler(Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(ScrollableViewer_PreviewKeyDown), handledEventsToo: true);

            // Add grid columns + column-chooser entries for any fields contributed by
            // plugins loaded during App.OnStartup (see Model/Extensibility/PluginManager).
            AddExtendedFieldColumns();
            RebuildUserDefinedGridColumns();

            // Show which extended (plugin) parsers were loaded/failed on the Summary tab
            // as soon as the window opens, before any trace file is loaded.
            _loaderSummary = BuildPluginSummary();
            SummaryViewer.Document = _loaderSummary ?? new FlowDocument();
            ApplyRichTextBoxWrap(SummaryViewer, _summaryWrap);

            // Disable link detection after controls are loaded to prevent regex performance issues.
            // This is a redundant safety measure in addition to the global handler in App.OnStartup.
            Loaded += (_, _) => DisableLinkDetection();
            Loaded += async (_, _) =>
            {
                if (AppSettings.UseSplitView)
                    await SetViewLayoutAsync(useSplitView: true);
            };
            SourceInitialized += MainWindow_SourceInitialized;

            // FilterRuleSet/HighlightRuleSet forward to whichever session is currently active, which
            // changes over time (session switch), so subscribe to the active session's collections
            // directly and resubscribe whenever the active session changes, rather than relying on
            // the static façade's add/remove accessors (those only bind to whatever is "current" at
            // subscribe time).
            TraceSessionManager.ActiveSessionChanged += OnActiveSessionChanged;
            SubscribeToSessionRuleEvents(TraceSessionManager.GetActive());
            TraceSessionManager.SessionsChanged += OnSessionsChanged;
            TraceSessionManager.ActiveSessionChanged += OnActiveSessionChangedRefreshList;
            CustomColumnSet.ColumnsChanged += OnCustomColumnsChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (_, _) =>
            {
                TraceSessionManager.ActiveSessionChanged -= OnActiveSessionChanged;
                UnsubscribeFromSessionRuleEvents(TraceSessionManager.GetActive());
                TraceSessionManager.SessionsChanged -= OnSessionsChanged;
                TraceSessionManager.ActiveSessionChanged -= OnActiveSessionChangedRefreshList;
                CustomColumnSet.ColumnsChanged -= OnCustomColumnsChanged;
                ThemeManager.ThemeChanged -= OnThemeChanged;
                StopMiddleMouseAutoScroll();
                _windowSource?.RemoveHook(WindowMessageHook);
            };

            RefreshSessionsList();
        }

        private void OnSessionsChanged(object? sender, EventArgs e) => RefreshSessionsList();

        private void OnActiveSessionChangedRefreshList(object? sender, TraceSession? newSession) => RefreshSessionsList();

        private TraceSession? _ruleEventSubscribedSession;

        private void SubscribeToSessionRuleEvents(TraceSession? session)
        {
            if (session is null)
                return;
            session.Highlights.RulesChanged += OnHighlightRulesChanged;
            session.Filters.FiltersChanged += OnFilterRulesChanged;
            _ruleEventSubscribedSession = session;
        }

        private void UnsubscribeFromSessionRuleEvents(TraceSession? session)
        {
            if (session is null)
                return;
            session.Highlights.RulesChanged -= OnHighlightRulesChanged;
            session.Filters.FiltersChanged -= OnFilterRulesChanged;
        }

        private void OnActiveSessionChanged(object? sender, TraceSession? newSession)
        {
            UnsubscribeFromSessionRuleEvents(_ruleEventSubscribedSession);
            _ruleEventSubscribedSession = null;
            SubscribeToSessionRuleEvents(newSession);
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

        private void OnThemeChanged(object? sender, EventArgs e)
        {
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

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new FilterWindow { Owner = this };
            window.ShowDialog();
        }

        private void FindCommand_Executed(object sender, ExecutedRoutedEventArgs e)
            => OpenFindForCurrentFocus();

        private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenFindForCurrentFocus();
                e.Handled = true;
            }
            else if (TryGetFocusedFindScope(out var scope)
                     && GetLocalFindControls(scope).Bar.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Enter)
                {
                    await FindLocalAsync(scope, forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    CloseLocalFind(scope);
                    e.Handled = true;
                }
            }
        }

        private void OpenFindForCurrentFocus()
        {
            if (TryGetFocusedFindScope(out var scope))
            {
                OpenLocalFind(scope);
                return;
            }

            FindSessionsBar.Visibility = Visibility.Visible;
            FindSessionsText.Focus();
            FindSessionsText.SelectAll();
        }

        private bool TryGetFocusedFindScope(out FindScope scope)
        {
            if (RequestHeadersPanel.IsKeyboardFocusWithin)
                scope = FindScope.RequestHeaders;
            else if (RequestBodyPanel.IsKeyboardFocusWithin)
                scope = FindScope.RequestBody;
            else if (ResponseHeadersPanel.IsKeyboardFocusWithin)
                scope = FindScope.ResponseHeaders;
            else if (ResponseBodyPanel.IsKeyboardFocusWithin)
                scope = FindScope.ResponseBody;
            else
            {
                scope = FindScope.AllSessions;
                return false;
            }

            return true;
        }

        private void OpenLocalFind(FindScope scope)
        {
            // Formatting a large payload anyway disables Find for it (see IsFindBlocked) -
            // don't even show the find bar in that case.
            if (IsFindBlocked(scope))
                return;

            var (bar, searchBox, _) = GetLocalFindControls(scope);
            bar.Visibility = Visibility.Visible;
            searchBox.IsEnabled = true;
            searchBox.Focus();
            searchBox.SelectAll();
        }

        private async void LocalFindNextButton_Click(object sender, RoutedEventArgs e)
        {
            if (TryGetFindScope(sender, out var scope))
                await FindLocalAsync(scope, forward: true);
        }

        private async void LocalFindPreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (TryGetFindScope(sender, out var scope))
                await FindLocalAsync(scope, forward: false);
        }

        private void LocalFindCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (TryGetFindScope(sender, out var scope))
                CloseLocalFind(scope);
        }

        private async void LocalFindText_KeyDown(object sender, KeyEventArgs e)
        {
            if (!TryGetFindScope(sender, out var scope))
                return;

            if (e.Key == Key.Escape)
            {
                CloseLocalFind(scope);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                await FindLocalAsync(scope, forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
            }
        }

        private void LocalFindText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryGetFindScope(sender, out var scope))
                GetLocalFindControls(scope).Status.Text = string.Empty;
        }

        private async Task FindLocalAsync(FindScope scope, bool forward)
        {
            var (_, searchBox, status) = GetLocalFindControls(scope);
            if (IsFindBlocked(scope))
            {
                status.Text = "Find disabled for this large, fully formatted payload";
                return;
            }

            var searchText = searchBox.Text;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                status.Text = string.Empty;
                return;
            }

            switch (scope)
            {
                case FindScope.RequestHeaders:
                    SelectTextMatch(RequestHeadersText, searchText, forward, status);
                    break;
                case FindScope.RequestBody:
                    await PreparePayloadForSearchAsync(request: true);
                    SelectTextMatch(RequestPayloadEditor, searchText, forward, status);
                    break;
                case FindScope.ResponseHeaders:
                    SelectTextMatch(ResponseHeadersText, searchText, forward, status);
                    break;
                case FindScope.ResponseBody:
                    await PreparePayloadForSearchAsync(request: false);
                    SelectTextMatch(ResponsePayloadEditor, searchText, forward, status);
                    break;
            }

            if (status.Text == "Match found")
                FocusFindTarget(scope);
            else
                searchBox.Focus();
        }

        private void CloseLocalFind(FindScope scope)
        {
            var (bar, _, status) = GetLocalFindControls(scope);
            bar.Visibility = Visibility.Collapsed;
            status.Text = string.Empty;
            FocusFindTarget(scope);
        }

        private (Border Bar, TextBox SearchBox, TextBlock Status) GetLocalFindControls(FindScope scope)
            => scope switch
            {
                FindScope.RequestHeaders => (RequestHeadersFindBar, RequestHeadersFindText, RequestHeadersFindStatus),
                FindScope.RequestBody => (RequestBodyFindBar, RequestBodyFindText, RequestBodyFindStatus),
                FindScope.ResponseHeaders => (ResponseHeadersFindBar, ResponseHeadersFindText, ResponseHeadersFindStatus),
                FindScope.ResponseBody => (ResponseBodyFindBar, ResponseBodyFindText, ResponseBodyFindStatus),
                _ => throw new ArgumentOutOfRangeException(nameof(scope)),
            };

        private static bool TryGetFindScope(object sender, out FindScope scope)
            => Enum.TryParse((sender as FrameworkElement)?.Tag as string, out scope)
               && scope != FindScope.AllSessions;

        // Formatting a large payload anyway (see "Format anyway") re-enables word wrap and
        // syntax highlighting, which reintroduces the slow scroll/select behavior Find relies
        // on - so Find is disabled for that payload in exchange for full formatting.
        private bool IsFindBlocked(FindScope scope) => scope switch
        {
            FindScope.RequestBody => _requestPayloadIsLarge && _requestPayloadFormatOverride,
            FindScope.ResponseBody => _responsePayloadIsLarge && _responsePayloadFormatOverride,
            _ => false,
        };

        private void FocusFindTarget(FindScope scope)
        {
            if (scope == FindScope.RequestHeaders)
                RequestHeadersText.Focus();
            else if (scope == FindScope.RequestBody)
                RequestPayloadEditor.Focus();
            else if (scope == FindScope.ResponseHeaders)
                ResponseHeadersText.Focus();
            else if (scope == FindScope.ResponseBody)
                ResponsePayloadEditor.Focus();
        }

        private async void FindNextButton_Click(object sender, RoutedEventArgs e)
            => await FindAsync(forward: true);

        private async void FindPreviousButton_Click(object sender, RoutedEventArgs e)
            => await FindAsync(forward: false);

        private void CloseFindButton_Click(object sender, RoutedEventArgs e)
            => CloseFindSessionsBar();

        private void FindSessionsText_TextChanged(object sender, TextChangedEventArgs e)
        {
            FindSessionsStatus.Text = string.Empty;
        }

        private void FindScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FindSessionsStatus is not null)
                FindSessionsStatus.Text = string.Empty;
        }

        private async void FindSessionsText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseFindSessionsBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                await FindAsync(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
            }
        }

        private void CloseFindSessionsBar()
        {
            FindSessionsBar.Visibility = Visibility.Collapsed;
            FindSessionsStatus.Text = string.Empty;
            RequestList.Focus();
        }

        private async Task FindAsync(bool forward)
        {
            var scope = (FindScope)FindScopeCombo.SelectedIndex;
            if (scope == FindScope.AllSessions)
            {
                FindSession(forward);
                return;
            }

            await FindInSelectedSessionAsync(scope, forward);
            FindSessionsText.Focus();
        }

        private void FindSession(bool forward)
        {
            var searchText = FindSessionsText.Text;
            var count = RequestList.Items.Count;
            if (string.IsNullOrWhiteSpace(searchText) || count == 0)
            {
                FindSessionsStatus.Text = count == 0 ? "No sessions" : string.Empty;
                return;
            }

            var step = forward ? 1 : -1;
            var selectedIndex = RequestList.SelectedIndex;
            var startIndex = selectedIndex < 0
                ? (forward ? 0 : count - 1)
                : ((selectedIndex + step) % count + count) % count;

            for (var offset = 0; offset < count; offset++)
            {
                var index = ((startIndex + step * offset) % count + count) % count;
                if (RequestList.Items[index] is DataRowView row && RowContainsText(row.Row, searchText))
                {
                    RequestList.SelectedItems.Clear();
                    RequestList.SelectedIndex = index;
                    RequestList.ScrollIntoView(RequestList.Items[index]);
                    FindSessionsStatus.Text = $"Session {index + 1} of {count}";
                    return;
                }
            }

            FindSessionsStatus.Text = "No matches";
        }

        private async Task FindInSelectedSessionAsync(FindScope scope, bool forward)
        {
            if (RequestList.SelectedItem is not DataRowView)
            {
                FindSessionsStatus.Text = "Select a session";
                return;
            }

            if (IsFindBlocked(scope))
            {
                FindSessionsStatus.Text = "Find disabled for this large, fully formatted payload";
                return;
            }

            var searchText = FindSessionsText.Text;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                FindSessionsStatus.Text = string.Empty;
                return;
            }

            switch (scope)
            {
                case FindScope.RequestHeaders:
                    MainTabControl.SelectedIndex = 1;
                    SelectTextMatch(RequestHeadersText, searchText, forward, FindSessionsStatus);
                    break;
                case FindScope.RequestBody:
                    await PreparePayloadForSearchAsync(request: true);
                    SelectTextMatch(RequestPayloadEditor, searchText, forward, FindSessionsStatus);
                    break;
                case FindScope.ResponseHeaders:
                    MainTabControl.SelectedIndex = 2;
                    SelectTextMatch(ResponseHeadersText, searchText, forward, FindSessionsStatus);
                    break;
                case FindScope.ResponseBody:
                    await PreparePayloadForSearchAsync(request: false);
                    SelectTextMatch(ResponsePayloadEditor, searchText, forward, FindSessionsStatus);
                    break;
            }
        }

        private async Task PreparePayloadForSearchAsync(bool request)
        {
            var payload = request ? _requestPayload : _responsePayload;
            if (payload is not { Length: > 0 })
                return;

            var editor = request ? RequestPayloadEditor : ResponsePayloadEditor;
            var formatCombo = request ? RequestPayloadFormatCombo : ResponsePayloadFormatCombo;
            var format = (PayloadFormat)formatCombo.SelectedIndex;
            if (format is PayloadFormat.Image or PayloadFormat.Svg)
            {
                format = PayloadFormat.PlainText;
                _isPopulatingViewers = true;
                try
                {
                    formatCombo.SelectedIndex = (int)format;
                }
                finally
                {
                    _isPopulatingViewers = false;
                }
            }

            if (request)
            {
                RequestViewerGrid.Visibility = Visibility.Visible;
                _requestTabEverActivated = true;
                MainTabControl.SelectedIndex = 1;
                if (_requestPayloadNeedsRender || string.IsNullOrEmpty(editor.Text))
                {
                    _requestPayloadNeedsRender = false;
                    await RenderRequestPayload(format);
                }
            }
            else
            {
                ResponseViewerGrid.Visibility = Visibility.Visible;
                _responseTabEverActivated = true;
                MainTabControl.SelectedIndex = 2;
                if (_responsePayloadNeedsRender || string.IsNullOrEmpty(editor.Text))
                {
                    _responsePayloadNeedsRender = false;
                    await RenderResponsePayload(format);
                }
            }
        }

        private static void SelectTextMatch(TextBox textBox, string searchText, bool forward, TextBlock status)
        {
            var index = FindTextIndex(textBox.Text, searchText, textBox.SelectionStart, textBox.SelectionLength, forward);
            if (index < 0)
            {
                status.Text = "No matches";
                return;
            }

            textBox.Focus();
            textBox.Select(index, searchText.Length);
            status.Text = "Match found";

            // Deferred: WPF scrolls the caret into view (edge, not centered) in response to
            // Select()/Focus(), which would otherwise run after and undo an immediate centering call.
            textBox.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => CenterTextMatch(textBox, index, searchText.Length)));
        }

        private static void SelectTextMatch(ICSharpCode.AvalonEdit.TextEditor editor, string searchText, bool forward, TextBlock status)
        {
            var index = FindTextIndex(editor.Text, searchText, editor.SelectionStart, editor.SelectionLength, forward);
            if (index < 0)
            {
                status.Text = "No matches";
                return;
            }

            editor.Focus();
            editor.Select(index, searchText.Length);
            status.Text = "Match found";

            // Deferred: AvalonEdit scrolls the caret into view (edge, not centered) in response to
            // Select()/Focus(), which would otherwise run after and undo an immediate centering call.
            editor.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => CenterTextMatch(editor, index, searchText.Length)));
        }

        private static void CenterTextMatch(TextBox textBox, int index, int length)
        {
            textBox.ScrollToLine(textBox.GetLineIndexFromCharacterIndex(index));
            textBox.UpdateLayout();

            var start = textBox.GetRectFromCharacterIndex(index);
            if (start.IsEmpty)
                return;

            var endIndex = Math.Min(index + length, textBox.Text.Length);
            var end = textBox.GetRectFromCharacterIndex(endIndex);
            var centerX = !end.IsEmpty && Math.Abs(end.Y - start.Y) < start.Height
                ? (start.X + end.X) / 2
                : start.X;
            var centerY = start.Y + start.Height / 2;

            textBox.ScrollToHorizontalOffset(Math.Max(0,
                textBox.HorizontalOffset + centerX - textBox.ViewportWidth / 2));
            textBox.ScrollToVerticalOffset(Math.Max(0,
                textBox.VerticalOffset + centerY - textBox.ViewportHeight / 2));
        }

        private static void CenterTextMatch(ICSharpCode.AvalonEdit.TextEditor editor, int index, int length)
        {
            var startLocation = editor.Document.GetLocation(index);
            editor.ScrollTo(startLocation.Line, startLocation.Column);
            editor.UpdateLayout();

            var textView = editor.TextArea.TextView;
            var scrollInfo = (System.Windows.Controls.Primitives.IScrollInfo)textView;
            var start = textView.GetVisualPosition(
                new ICSharpCode.AvalonEdit.TextViewPosition(startLocation),
                VisualYPosition.LineMiddle);
            var endLocation = editor.Document.GetLocation(Math.Min(index + length, editor.Document.TextLength));
            var end = textView.GetVisualPosition(
                new ICSharpCode.AvalonEdit.TextViewPosition(endLocation),
                VisualYPosition.LineMiddle);
            var centerX = endLocation.Line == startLocation.Line
                ? (start.X + end.X) / 2
                : start.X;

            // GetVisualPosition returns document-absolute coordinates (already independent of the
            // current scroll position), so the new offset must not add the existing offset on top.
            scrollInfo.SetHorizontalOffset(Math.Max(0, centerX - scrollInfo.ViewportWidth / 2));
            scrollInfo.SetVerticalOffset(Math.Max(0, start.Y - scrollInfo.ViewportHeight / 2));
        }

        private static int FindTextIndex(string text, string searchText, int selectionStart, int selectionLength, bool forward)
        {
            if (string.IsNullOrEmpty(text))
                return -1;

            if (forward)
            {
                var start = Math.Min(selectionStart + selectionLength, text.Length);
                var index = text.IndexOf(searchText, start, StringComparison.OrdinalIgnoreCase);
                return index >= 0 || start == 0
                    ? index
                    : text.IndexOf(searchText, 0, StringComparison.OrdinalIgnoreCase);
            }

            var previousStart = Math.Min(selectionStart - 1, text.Length - 1);
            var previous = previousStart >= 0
                ? text.LastIndexOf(searchText, previousStart, StringComparison.OrdinalIgnoreCase)
                : -1;
            return previous >= 0
                ? previous
                : text.LastIndexOf(searchText, StringComparison.OrdinalIgnoreCase);
        }

        private static bool RowContainsText(DataRow row, string searchText)
        {
            foreach (DataColumn column in row.Table.Columns)
            {
                if (column.ColumnName is TraceDataSchema.RowBackground or TraceDataSchema.RowForeground)
                    continue;

                var value = row[column];
                if (value is byte[] payload)
                {
                    if (payload.Length > 0 && Encoding.UTF8.GetString(payload)
                        .Contains(searchText, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (value is not DBNull && Convert.ToString(value)?.Contains(
                    searchText, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnHighlightRulesChanged(object? sender, EventArgs e)
        {
            var highlights = TraceSessionManager.GetActive()?.Highlights;
            if (highlights is not null)
                _trace?.RecomputeHighlights(highlights);
            // The Brush columns changed in-place; nudge the view to redraw.
            RequestList.Items.Refresh();
        }

        private void HighlightsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new HighlightsWindow { Owner = this };
            window.ShowDialog();
        }

        private void CustomColumnsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new CustomColumnsWindow { Owner = this };
            window.ShowDialog();
        }

        private void OnCustomColumnsChanged(object? sender, EventArgs e)
        {
            foreach (var rule in FilterRuleSet.Rules
                .Where(rule => !TraceColumnCatalog.Names.Contains(
                    rule.ColumnName,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray())
            {
                FilterRuleSet.Rules.Remove(rule);
            }

            foreach (var rule in HighlightRuleSet.Rules
                .Where(rule => !TraceColumnCatalog.Names.Contains(
                    rule.ColumnName,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray())
            {
                HighlightRuleSet.Rules.Remove(rule);
            }

            var highlights = TraceSessionManager.GetActive()?.Highlights;
            if (highlights is not null)
                _trace?.RefreshUserDefinedColumns(highlights);
            RebuildUserDefinedGridColumns();
            ApplyFilter();
            RequestList.Items.Refresh();
        }

        private async void AppSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new AppSettingsWindow(_isSplitView) { Owner = this };
            if (window.ShowDialog() != true)
                return;

            AppSettingsButton.IsEnabled = false;
            try
            {
                var theme = window.SelectedThemePreference switch
                {
                    ThemePreference.Light => AppTheme.Light,
                    ThemePreference.Dark => AppTheme.Dark,
                    _ => ThemeManager.GetSystemTheme(),
                };
                if (ThemeManager.Current != theme)
                    ThemeManager.Apply(theme);

                await SetViewLayoutAsync(window.UseSplitView);
                await ApplyMcpSettingsAsync(window.HostMcpServer, window.McpPort);

                McpServerButton.Checked -= McpServerButton_Checked;
                McpServerButton.Unchecked -= McpServerButton_Unchecked;
                McpServerButton.IsChecked = McpHostManager.IsRunning;
                McpServerButton.Content = McpHostManager.IsRunning ? "Disable" : "Enable";
                McpServerButton.Tag = McpHostManager.IsRunning ? "\uE8CE" : "\uE8CD";
                McpServerButton.Checked += McpServerButton_Checked;
                McpServerButton.Unchecked += McpServerButton_Unchecked;

                AppSettings.ThemePreference = window.SelectedThemePreference;
                AppSettings.UseSplitView = window.UseSplitView;
                AppSettings.McpPort = window.McpPort;
                AppSettings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to apply app settings:\n{ex.Message}",
                    "App Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                AppSettingsButton.IsEnabled = true;
            }
        }

        private static async Task ApplyMcpSettingsAsync(bool shouldRun, int port)
        {
            bool portChanged = McpHostManager.Port != port;
            if (McpHostManager.IsRunning && (!shouldRun || portChanged))
                await McpHostManager.StopAsync();

            McpHostManager.Port = port;

            if (shouldRun && !McpHostManager.IsRunning)
                await McpHostManager.StartAsync();
        }

        private async void McpServerButton_Checked(object sender, RoutedEventArgs e)
        {
            McpServerButton.IsEnabled = false;
            try
            {
                await McpHostManager.StartAsync();
                McpServerButton.Content = "Disable";
                McpServerButton.Tag = "\uE8CE";
            }
            catch (Exception ex)
            {
                McpServerButton.IsChecked = false;
                MessageBox.Show(this, $"Failed to start the MCP server:\n{ex.Message}",
                    "MCP Server", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                McpServerButton.IsEnabled = true;
            }
        }

        private async void McpServerButton_Unchecked(object sender, RoutedEventArgs e)
        {
            McpServerButton.IsEnabled = false;
            try
            {
                await McpHostManager.StopAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to stop the MCP server cleanly:\n{ex.Message}",
                    "MCP Server", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                McpServerButton.Content = "Enable";
                McpServerButton.Tag = "\uE8CD";
                McpServerButton.IsEnabled = true;
            }
        }

        private async Task SetViewLayoutAsync(bool useSplitView)
        {
            if (_isSplitView == useSplitView)
                return;

            if (useSplitView)
            {
                _sessionsLeftWidth = SessionsColumn.Width;

                RequestTab.Content = null;
                ResponseTab.Content = null;
                SplitRequestHost.Content = RequestViewerGrid;
                SplitResponseHost.Content = ResponseViewerGrid;
                MoveTabContent(SummaryTab, TopSummaryTab);
                MoveTabContent(RestTab, TopRestTab);
                MoveTabContent(SoapTab, TopSoapTab);
                MoveTabContent(MapiTab, TopMapiTab);

                SessionsColumn.MinWidth = 0;
                SessionsColumn.Width = new GridLength(1, GridUnitType.Star);
                VerticalSplitterColumn.Width = new GridLength(0);
                TabbedDetailsColumn.MinWidth = 0;
                TabbedDetailsColumn.Width = new GridLength(0);

                SessionsPane.SetValue(Grid.ColumnSpanProperty, 3);
                SessionsRow.Height = _sessionsTopHeight;
                HorizontalSplitterRow.Height = new GridLength(4);
                SplitDetailsRow.Height = new GridLength(3, GridUnitType.Star);

                VerticalWorkspaceSplitter.Visibility = Visibility.Collapsed;
                MainTabControl.Visibility = Visibility.Collapsed;
                HorizontalWorkspaceSplitter.Visibility = Visibility.Visible;
                TopLayoutTabControl.Visibility = Visibility.Visible;
                RequestViewerGrid.Visibility = Visibility.Visible;
                ResponseViewerGrid.Visibility = Visibility.Visible;
                _requestTabEverActivated = true;
                _responseTabEverActivated = true;
                _isSplitView = true;

                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                AutoSizeGridViewColumns();
                await RenderVisibleSplitPayloads();
            }
            else
            {
                _sessionsTopHeight = SessionsRow.Height;

                SplitRequestHost.Content = null;
                SplitResponseHost.Content = null;
                RequestTab.Content = RequestViewerGrid;
                ResponseTab.Content = ResponseViewerGrid;
                MoveTabContent(TopSummaryTab, SummaryTab);
                MoveTabContent(TopRestTab, RestTab);
                MoveTabContent(TopSoapTab, SoapTab);
                MoveTabContent(TopMapiTab, MapiTab);

                SessionsPane.ClearValue(Grid.ColumnSpanProperty);
                SessionsColumn.MinWidth = 400;
                SessionsColumn.Width = _sessionsLeftWidth;
                VerticalSplitterColumn.Width = GridLength.Auto;
                TabbedDetailsColumn.MinWidth = 300;
                TabbedDetailsColumn.Width = new GridLength(1, GridUnitType.Star);

                SessionsRow.Height = new GridLength(1, GridUnitType.Star);
                HorizontalSplitterRow.Height = new GridLength(0);
                SplitDetailsRow.Height = new GridLength(0);

                TopLayoutTabControl.Visibility = Visibility.Collapsed;
                HorizontalWorkspaceSplitter.Visibility = Visibility.Collapsed;
                VerticalWorkspaceSplitter.Visibility = Visibility.Visible;
                MainTabControl.Visibility = Visibility.Visible;
                _isSplitView = false;
            }
        }

        private static void MoveTabContent(TabItem source, TabItem destination)
        {
            var content = source.Content;
            source.Content = null;
            destination.Content = content;
        }

        private async Task RenderVisibleSplitPayloads()
        {
            if (_requestPayloadNeedsRender)
            {
                _requestPayloadNeedsRender = false;
                await RenderRequestPayload(_pendingRequestFormat, showBusyIndicator: true);
            }

            if (_responsePayloadNeedsRender)
            {
                _responsePayloadNeedsRender = false;
                await RenderResponsePayload(_pendingResponseFormat, showBusyIndicator: true);
            }
        }

        /// <summary>
        /// Selects the row with the given <see cref="TraceDataSchema.Index"/> value in the trace grid,
        /// scrolls it into view, and brings the window to the foreground. Intended for external
        /// automation (e.g. the in-process MCP server). Returns a human-readable result message.
        /// </summary>
        public string SelectTraceRow(int index)
        {
            if (_trace is null)
                return "No trace file is currently loaded.";

            DataRowView? target = null;
            foreach (var item in RequestList.Items)
            {
                if (item is DataRowView drv && drv.Row[TraceDataSchema.Index] is int idx && idx == index)
                {
                    target = drv;
                    break;
                }
            }

            if (target is null)
                return $"Row #{index} was not found. It may not exist, or it may be hidden by the active filter (try clearing filters).";

            RequestList.SelectedItems.Clear();
            RequestList.SelectedItem = target;
            RequestList.ScrollIntoView(target);
            BringToFront();
            return $"Selected row #{index}.";
        }

        /// <summary>
        /// Finds the first row (in <see cref="TraceDataSchema.Index"/> order, across the full trace,
        /// ignoring the active filter) matching the given field/comparator/value, and selects it.
        /// Returns a human-readable result message. Intended for external automation.
        /// </summary>
        public string FindAndSelectTraceRow(FilterField field, FilterComparator comparator, string value)
        {
            if (_trace is null)
                return "No trace file is currently loaded.";

            var rule = new FilterRule { Field = field, Comparator = comparator, Value = value };
            var expr = rule.BuildExpression();
            if (string.IsNullOrEmpty(expr))
                return "Could not build a query for the given criteria (check the value format, e.g. 'min-max' for Range).";

            DataRow[] rows;
            try
            {
                rows = _trace.Messages.Select(expr, $"[{TraceDataSchema.Index}] ASC");
            }
            catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or InvalidExpressionException)
            {
                return $"Query failed: {ex.Message}";
            }

            if (rows.Length == 0)
                return $"No row matched {field} {comparator} '{value}'.";

            var targetIndex = (int)rows[0][TraceDataSchema.Index];
            return SelectTraceRow(targetIndex);
        }

        private void BringToFront()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
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

        private void ScrollableViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_middleMouseAutoScrollActive)
            {
                StopMiddleMouseAutoScroll();
                if (e.ChangedButton == MouseButton.Middle)
                    e.Handled = true;
                return;
            }

            if (e.ChangedButton != MouseButton.Middle)
                return;

            if (sender is not FrameworkElement target)
                return;

            var scrollViewer = GetScrollViewer(target);
            if (scrollViewer is null)
                return;

            _middleMouseScrollTarget = target;
            _middleMouseScrollViewer = scrollViewer;
            _middleMouseScrollAnchor = e.GetPosition(target);
            _middleMouseScrollPosition = _middleMouseScrollAnchor;
            _previousOverrideCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.ScrollAll;
            _middleMouseAutoScrollActive = true;

            _middleMouseScrollTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Input,
                MiddleMouseScrollTimer_Tick,
                Dispatcher);
            _middleMouseScrollTimer.Start();
            Mouse.Capture(target, CaptureMode.SubTree);
            e.Handled = true;
        }

        private void ScrollableViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_middleMouseAutoScrollActive && _middleMouseScrollTarget is not null)
                _middleMouseScrollPosition = e.GetPosition(_middleMouseScrollTarget);
        }

        private void ScrollableViewer_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_middleMouseAutoScrollActive)
                StopMiddleMouseAutoScroll();
        }

        private void ScrollableViewer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_middleMouseAutoScrollActive && e.Key == Key.Escape)
            {
                StopMiddleMouseAutoScroll();
                e.Handled = true;
            }
        }

        private void MiddleMouseScrollTimer_Tick(object? sender, EventArgs e)
        {
            if (_middleMouseScrollViewer is null)
                return;

            var horizontalDelta = GetAutoScrollDelta(_middleMouseScrollPosition.X - _middleMouseScrollAnchor.X);
            var verticalDelta = GetAutoScrollDelta(_middleMouseScrollPosition.Y - _middleMouseScrollAnchor.Y);

            if (horizontalDelta != 0)
                _middleMouseScrollViewer.ScrollToHorizontalOffset(_middleMouseScrollViewer.HorizontalOffset + horizontalDelta);
            if (verticalDelta != 0)
                _middleMouseScrollViewer.ScrollToVerticalOffset(_middleMouseScrollViewer.VerticalOffset + verticalDelta);
        }

        private static double GetAutoScrollDelta(double distance)
        {
            const double DeadZone = 12;
            const double SpeedFactor = 0.15;
            const double MaximumDelta = 48;

            var magnitude = Math.Abs(distance);
            if (magnitude <= DeadZone)
                return 0;

            return Math.Sign(distance) * Math.Min(MaximumDelta, (magnitude - DeadZone) * SpeedFactor);
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _windowSource = PresentationSource.FromVisual(this) as HwndSource;
            _windowSource?.AddHook(WindowMessageHook);
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmMouseHorizontalWheel)
                return IntPtr.Zero;

            var hoveredTarget = _middleMouseScrollTargets.FirstOrDefault(target => target.IsVisible && target.IsMouseOver);
            if (hoveredTarget is null)
                return IntPtr.Zero;

            var scrollViewer = GetScrollViewer(hoveredTarget);
            if (scrollViewer is null)
                return IntPtr.Zero;

            int wheelDelta = (short)((wParam.ToInt64() >> 16) & 0xffff);
            bool canScroll = wheelDelta > 0
                ? scrollViewer.HorizontalOffset < scrollViewer.ScrollableWidth
                : scrollViewer.HorizontalOffset > 0;
            if (!canScroll)
                return IntPtr.Zero;

            const double PixelsPerWheelDetent = 48;
            scrollViewer.ScrollToHorizontalOffset(
                scrollViewer.HorizontalOffset + wheelDelta / 120.0 * PixelsPerWheelDetent);
            handled = true;
            return IntPtr.Zero;
        }

        private void StopMiddleMouseAutoScroll()
        {
            _middleMouseAutoScrollActive = false;
            _middleMouseScrollTimer?.Stop();
            Mouse.OverrideCursor = _previousOverrideCursor;
            _previousOverrideCursor = null;
            if (Mouse.Captured == _middleMouseScrollTarget)
                Mouse.Capture(null);
            _middleMouseScrollTarget = null;
            _middleMouseScrollViewer = null;
        }

        private static ScrollViewer? GetScrollViewer(FrameworkElement target)
            => target as ScrollViewer ?? FindVisualChild<ScrollViewer>(target);

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;

                var descendant = FindVisualChild<T>(child);
                if (descendant is not null)
                    return descendant;
            }

            return null;
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

        private (DataRowView Row, string FieldName, string Value)? _contextCell;

        private void RequestList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _contextCell = null;

            var source = e.OriginalSource as DependencyObject;
            var item = FindVisualAncestor<ListViewItem>(source);
            var presenter = FindVisualAncestor<GridViewRowPresenter>(source);
            if (item?.DataContext is not DataRowView row || presenter is null || RequestList.View is not GridView gridView)
                return;

            if (!item.IsSelected)
            {
                RequestList.SelectedItems.Clear();
                item.IsSelected = true;
            }
            item.Focus();

            double x = e.GetPosition(presenter).X;
            double rightEdge = 0;
            foreach (var column in gridView.Columns)
            {
                rightEdge += column.ActualWidth;
                if (x > rightEdge)
                    continue;

                var fieldName = GetSortMemberPath(column);
                if (string.IsNullOrEmpty(fieldName) || !row.Row.Table.Columns.Contains(fieldName))
                    return;

                var rawValue = row.Row[fieldName];
                var value = rawValue is DBNull
                    ? string.Empty
                    : Convert.ToString(rawValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                _contextCell = (row, fieldName, value);
                return;
            }
        }

        private void RequestList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var hasRow = _contextCell is not null;
            FilterMenuItem.IsEnabled = hasRow;
            HighlightMenuItem.IsEnabled = hasRow;

            if (_contextCell is { } cell)
            {
                var displayValue = cell.Value.Length <= 80
                    ? cell.Value
                    : cell.Value[..77] + "...";
                FilterCurrentCellMenuItem.Header = $"Current cell value equals {displayValue}";
                HighlightCurrentCellMenuItem.Header = $"Current cell value equals {displayValue}";
            }

            if (_contextCell is { Row: var drv })
            {
                var host = drv.Row[TraceDataSchema.Host] as string ?? string.Empty;
                var method = drv.Row[TraceDataSchema.Method] as string ?? string.Empty;
                var path = drv.Row[TraceDataSchema.Path] as string ?? string.Empty;

                SetFilterMenuItem(FilterHostMenuItem, "only host", FilterField.Host, FilterComparator.Equals, host);
                SetFilterMenuItem(ExcludeHostMenuItem, "exclude host", FilterField.Host, FilterComparator.NotEquals, host);

                SetFilterMenuItem(FilterMethodMenuItem, "only METHOD", FilterField.Method, FilterComparator.Equals, method);
                SetFilterMenuItem(ExcludeMethodMenuItem, "exclude METHOD", FilterField.Method, FilterComparator.NotEquals, method);

                SetFilterMenuItem(FilterPathMenuItem, "only path", FilterField.Path, FilterComparator.Equals, path);
                SetFilterMenuItem(ExcludePathMenuItem, "exclude path", FilterField.Path, FilterComparator.NotEquals, path);
            }
        }

        private void RequestList_ContextMenuClosed(object sender, RoutedEventArgs e)
        {
            _contextCell = null;
        }

        private static void SetFilterMenuItem(MenuItem item, string headerPrefix, FilterField field, FilterComparator comparator, string value)
        {
            item.Header = $"{headerPrefix} {value}";
            item.Tag = (field, comparator, value);
            item.IsEnabled = !string.IsNullOrEmpty(value);
        }

        private void FilterMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: (FilterField field, FilterComparator comparator, string value) } || string.IsNullOrEmpty(value))
                return;

            FilterRuleSet.Rules.Add(new FilterRule
            {
                Combinator = FilterCombinator.And,
                Field = field,
                Comparator = comparator,
                Value = value,
            });
        }

        private void FilterCurrentCellMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_contextCell is not { } cell)
                return;

            FilterRuleSet.Rules.Add(new FilterRule
            {
                Combinator = FilterCombinator.And,
                ColumnName = cell.FieldName,
                Comparator = FilterComparator.Equals,
                Value = cell.Value,
            });
        }

        private void HighlightCurrentCellMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_contextCell is not { } cell)
                return;

            HighlightRuleSet.Rules.Insert(0, new HighlightRule
            {
                ColumnName = cell.FieldName,
                Operator = HighlightOperator.Equals,
                Value = cell.Value,
                BackgroundColor = Colors.LightYellow,
            });
        }

        private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source is not null)
            {
                if (source is T match)
                    return match;
                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }
            return null;
        }

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
            if (RequestList.View is not GridView gridView)
                return;

            const double DefaultWidth = 200;

            if (item.IsChecked)
            {
                double width = _savedColumnWidths.TryGetValue(column, out var saved) && saved > 0
                    ? saved
                    : DefaultWidth;
                column.Width = width;
                if (!gridView.Columns.Contains(column))
                    gridView.Columns.Insert(GetColumnInsertionIndex(gridView, column), column);
            }
            else
            {
                if (column.Width > 0)
                    _savedColumnWidths[column] = column.Width;
                gridView.Columns.Remove(column);
            }
        }

        private static int GetColumnInsertionIndex(GridView gridView, GridViewColumn targetColumn)
        {
            if (gridView.ColumnHeaderContextMenu is not ContextMenu contextMenu)
                return gridView.Columns.Count;

            int insertionIndex = 0;
            foreach (var menuItem in contextMenu.Items.OfType<MenuItem>())
            {
                if (menuItem.Tag is not GridViewColumn menuColumn)
                    continue;
                if (menuColumn == targetColumn)
                    return insertionIndex;
                if (gridView.Columns.Contains(menuColumn))
                    insertionIndex++;
            }

            return gridView.Columns.Count;
        }

        /// <summary>
        /// Adds a hidden <see cref="GridViewColumn"/> and matching column-chooser
        /// <see cref="MenuItem"/> for every extended field registered by a plugin (see
        /// <see cref="HttpTraceFile.ExtendedFieldNames"/>). Called once from the constructor,
        /// after plugins have already been loaded during App startup.
        /// </summary>
        private void AddExtendedFieldColumns()
        {
            var names = HttpTraceFile.ExtendedFieldNames;
            if (names.Count == 0)
                return;

            if (RequestList.View is not GridView gridView)
                return;

            var contextMenu = gridView.ColumnHeaderContextMenu;

            foreach (var name in names)
            {
                var displayName = HttpTraceFile.GetExtendedFieldDisplayName(name);

                var gridColumn = new GridViewColumn
                {
                    Header = displayName,
                    Width = 200,
                    DisplayMemberBinding = new System.Windows.Data.Binding(name),
                };

                if (contextMenu is not null)
                {
                    var menuItem = new MenuItem
                    {
                        Header = displayName,
                        IsCheckable = true,
                        IsChecked = false,
                        Tag = gridColumn,
                    };
                    menuItem.Checked += ColumnVisibility_Changed;
                    menuItem.Unchecked += ColumnVisibility_Changed;

                    int insertIndex = contextMenu.Items.IndexOf(ColumnActionsSeparator);
                    if (insertIndex < 0)
                        insertIndex = contextMenu.Items.Count;
                    contextMenu.Items.Insert(insertIndex, menuItem);
                }
            }
        }

        private void RebuildUserDefinedGridColumns()
        {
            if (RequestList.View is not GridView gridView)
                return;

            foreach (var column in _userDefinedGridColumns.Values)
                gridView.Columns.Remove(column);
            _userDefinedGridColumns.Clear();

            var contextMenu = gridView.ColumnHeaderContextMenu;
            if (contextMenu is not null)
            {
                foreach (var item in _userDefinedColumnMenuItems)
                    contextMenu.Items.Remove(item);
                _userDefinedColumnMenuItems.Clear();
            }

            foreach (var definition in CustomColumnSet.Columns)
            {
                if (string.IsNullOrWhiteSpace(definition.Name) ||
                    TraceColumnCatalog.IsReservedName(definition.Name) ||
                    _userDefinedGridColumns.ContainsKey(definition.Name))
                {
                    continue;
                }

                var gridColumn = new GridViewColumn
                {
                    Header = definition.Name,
                    Width = 160,
                    DisplayMemberBinding = new Binding(definition.Name),
                };
                _userDefinedGridColumns[definition.Name] = gridColumn;

                if (contextMenu is not null)
                {
                    var menuItem = new MenuItem
                    {
                        Header = definition.Name,
                        IsCheckable = true,
                        IsChecked = true,
                        Tag = gridColumn,
                    };
                    menuItem.Checked += ColumnVisibility_Changed;
                    menuItem.Unchecked += ColumnVisibility_Changed;

                    int insertIndex = contextMenu.Items.IndexOf(ColumnActionsSeparator);
                    if (insertIndex < 0)
                        insertIndex = contextMenu.Items.Count;
                    contextMenu.Items.Insert(insertIndex, menuItem);
                    _userDefinedColumnMenuItems.Add(menuItem);
                }

                gridView.Columns.Insert(GetColumnInsertionIndex(gridView, gridColumn), gridColumn);
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

            double availableWidth = RequestList.ActualWidth - ScrollbarWidth - ArrowIndicatorWidth;

            // Ensure we have a reasonable available width
            if (availableWidth < 300)
                availableWidth = 800; // Fallback if ListView hasn't been sized yet

            // Keep compact fields at their measured width. If the content is wider than
            // the viewport, distribute space above each header's minimum in proportion to demand.
            const double MinWidth = 50;
            const double Padding = 8;

            var desiredMeasurements = columnMeasurements
                .Select(cm =>
                {
                    double minimumWidth = Math.Max(MinWidth, MeasureColumnHeaderWidth(cm.Column));
                    double desiredWidth = Math.Max(minimumWidth, cm.MeasuredWidth + Padding);
                    return (cm.Column, MinimumWidth: minimumWidth, DesiredWidth: desiredWidth);
                })
                .ToList();

            double totalDesiredWidth = desiredMeasurements.Sum(cm => cm.DesiredWidth);

            if (totalDesiredWidth <= availableWidth)
            {
                foreach (var (column, _, desiredWidth) in desiredMeasurements)
                    column.Width = desiredWidth;

                // Keep compact columns content-sized and let the final visible column
                // consume the remaining viewport width.
                var lastColumn = desiredMeasurements[^1].Column;
                lastColumn.Width += availableWidth - totalDesiredWidth;
            }
            else
            {
                double minimumTotal = desiredMeasurements.Sum(cm => cm.MinimumWidth);
                if (minimumTotal >= availableWidth)
                {
                    foreach (var (column, minimumWidth, _) in desiredMeasurements)
                        column.Width = minimumWidth;
                    return;
                }

                double remainingWidth = availableWidth;
                double remainingMinimum = minimumTotal;
                foreach (var (column, minimumWidth, desiredWidth) in desiredMeasurements)
                {
                    remainingMinimum -= minimumWidth;
                    double availableForColumn = remainingWidth - remainingMinimum;
                    column.Width = Math.Min(desiredWidth, Math.Max(minimumWidth, availableForColumn));
                    remainingWidth -= column.Width;
                }
            }
        }

        private double MeasureColumnHeaderWidth(GridViewColumn column)
        {
            var header = new GridViewColumnHeader
            {
                Content = column.Header,
                FontFamily = RequestList.FontFamily,
                FontSize = RequestList.FontSize,
                FontStretch = RequestList.FontStretch,
                FontStyle = RequestList.FontStyle,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(6, 2, 6, 2),
            };
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(header.DesiredSize.Width);
        }

        private async void AddSessionButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open HTTP trace file",
                Filter = "HTTP trace files (*.saz;*.har;*.etl;*.trace)|*.saz;*.har;*.etl;*.trace|" +
                         "Fiddler session archive (*.saz)|*.saz|" +
                         "HTTP archive (*.har)|*.har|" +
                         "Event Trace for Windows (*.etl)|*.etl|" +
                         "EWS API trace (*.trace;*.log;*.txt)|*.trace;*.log;*.txt|" +
                         "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            await LoadTraceFileAsync(dialog.FileName).ConfigureAwait(true);
        }

        /// <summary>Display row for the session explorer's <see cref="SessionsListBox"/>.</summary>
        private sealed class SessionListItem
        {
            public required string Id { get; init; }
            public required string Label { get; init; }
            public required string Path { get; init; }
            public required bool IsActive { get; init; }
            public FontWeight IsActiveFontWeight => IsActive ? FontWeights.Bold : FontWeights.Normal;
        }

        private bool _updatingSessionsList;
        private double _expandedExplorerWidth = 240;

        private void CollapseExplorerButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleSessionsExplorer();
        }

        private void ToggleSessionsExplorer()
        {
            bool isExpanded = SessionsExplorerPane.Visibility == Visibility.Visible;
            if (isExpanded)
            {
                _expandedExplorerWidth = ExplorerColumn.Width.Value;
                SessionsExplorerPane.Visibility = Visibility.Collapsed;
                ExplorerColumn.Width = new GridLength(0);
                ExplorerSplitterColumn.Width = new GridLength(0);
                ExplorerRail.Visibility = Visibility.Visible;
            }
            else
            {
                ExplorerColumn.Width = new GridLength(_expandedExplorerWidth);
                ExplorerSplitterColumn.Width = GridLength.Auto;
                SessionsExplorerPane.Visibility = Visibility.Visible;
                ExplorerRail.Visibility = Visibility.Collapsed;
            }
        }

        private void RefreshSessionsList()
        {
            var activeId = TraceSessionManager.ActiveSessionId;
            var items = TraceSessionManager.List()
                .Select(s => new SessionListItem
                {
                    Id = s.Id,
                    Label = s.Label,
                    Path = s.Trace.FilePath,
                    IsActive = string.Equals(s.Id, activeId, StringComparison.OrdinalIgnoreCase),
                })
                .ToList();

            _updatingSessionsList = true;
            try
            {
                SessionsListBox.ItemsSource = items;
                SessionsListBox.SelectedItem = items.FirstOrDefault(i => i.IsActive);
            }
            finally
            {
                _updatingSessionsList = false;
            }
        }

        private void SessionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingSessionsList)
                return;
            if (SessionsListBox.SelectedItem is not SessionListItem item)
                return;
            if (string.Equals(item.Id, TraceSessionManager.ActiveSessionId, StringComparison.OrdinalIgnoreCase))
                return;

            SwitchToSession(item.Id);
        }

        private void CloseSessionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string sessionId })
                return;

            CloseSession(sessionId);
        }

        /// <summary>
        /// Loads the trace file at <paramref name="path"/> from disk, registers it as a new
        /// session, and shows it in the window (replacing any currently displayed trace).
        /// Shared by the Open File dialog and external automation (e.g. the in-process MCP
        /// server). Returns an error message on failure, or <c>null</c> on success.
        /// </summary>
        public Task<string?> LoadTraceFileAsync(string path) => LoadTraceFileAsync(path, sessionId: null, label: null, activate: true);

        /// <summary>
        /// Loads the trace file at <paramref name="path"/> from disk and registers it as a new
        /// session under <paramref name="sessionId"/> (auto-generated when null) with the given
        /// <paramref name="label"/> (defaults to the file name). When <paramref name="activate"/>
        /// is true (the default), the trace is immediately shown in the window; otherwise it is
        /// loaded into the background registry only. Returns an error message on failure, or
        /// <c>null</c> on success.
        /// </summary>
        public async Task<string?> LoadTraceFileAsync(string path, string? sessionId, string? label, bool activate)
        {
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
                MessageBox.Show(this, $"Failed to open trace file:\n{error.Message}",
                    "Open file", MessageBoxButton.OK, MessageBoxImage.Error);
                return error.Message;
            }

            TraceSession session;
            try
            {
                session = TraceSessionManager.Add(loaded!, sessionId, label);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }

            if (activate)
                ShowTrace(session.Id, loaded!, session.Label);
            return null;
        }

        /// <summary>
        /// Displays an already-loaded trace (no disk I/O), refreshing the grid, viewers, tab
        /// selection, and window title exactly as a fresh load would. Used both after a disk
        /// load completes and when external automation switches which registered session is
        /// currently shown.
        /// </summary>
        internal void ShowTrace(string sessionId, HttpTraceFile trace, string displayName)
        {
            // Activate first: PopulateList/ApplyFilter below resolve rules via the static
            // FilterRuleSet/HighlightRuleSet façades, which read whichever session is currently
            // active, so the switch must happen before anything that reads those façades.
            TraceSessionManager.SetActive(sessionId);

            var session = TraceSessionManager.Get(sessionId);
            if (session is not null)
                trace.RecomputeHighlights(session.Highlights);

            _trace = trace;
            _loaderSummary = BuildLoaderSummary(_trace);

            PopulateList();
            ClearViewers();
            if (_isSplitView)
                TopLayoutTabControl.SelectedIndex = 0;
            else
                MainTabControl.SelectedIndex = 0;
            Title = $"HTTP Trace Analyser - {displayName}";
        }

        /// <summary>Clears the window back to its no-trace-loaded state, e.g. after the active session is closed.</summary>
        internal void ClearTrace()
        {
            _trace = null;
            _loaderSummary = null;
            PopulateList();
            ClearViewers();
            Title = "HTTP Trace Analyser";
        }

        /// <summary>
        /// Switches to an already-loaded session by id (no disk I/O). Shared by the session
        /// explorer panel's session list and the MCP <c>SwitchTraceSession</c> tool so both
        /// surfaces behave identically. Returns a human-readable result message.
        /// </summary>
        public string SwitchToSession(string sessionId)
        {
            var session = TraceSessionManager.Get(sessionId);
            if (session is null)
                return $"Session '{sessionId}' was not found.";

            ShowTrace(session.Id, session.Trace, session.Label);
            return $"Now showing session '{session.Id}' - {session.Trace.FilePath} ({session.Trace.Count} messages).";
        }

        /// <summary>
        /// Closes (unloads) a session by id, freeing its memory. If it was the active session,
        /// automatically switches to another loaded session if one exists, otherwise clears the
        /// viewer. Shared by the session explorer panel's close button and the MCP
        /// <c>CloseTraceSession</c> tool. Returns a human-readable result message.
        /// </summary>
        public string CloseSession(string sessionId)
        {
            var session = TraceSessionManager.Get(sessionId);
            if (session is null)
                return $"Session '{sessionId}' was not found.";

            bool wasActive = string.Equals(TraceSessionManager.ActiveSessionId, sessionId, StringComparison.OrdinalIgnoreCase);
            TraceSessionManager.Remove(sessionId);

            if (!wasActive)
                return $"Closed session '{sessionId}'.";

            var next = TraceSessionManager.List().LastOrDefault();
            if (next is not null)
            {
                ShowTrace(next.Id, next.Trace, next.Label);
                return $"Closed session '{sessionId}' (was active). Now showing session '{next.Id}'.";
            }

            ClearTrace();
            return $"Closed session '{sessionId}' (was active). No other sessions remain; viewer cleared.";
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
            AddSessionButton.IsEnabled = !busy;
        }

        /// <summary>
        /// Builds a summary section listing any extended (plugin) trace parsers that were
        /// loaded, and any that failed to load, from the "Plugins" folder (see
        /// <see cref="Model.Extensibility.PluginManager"/>). Returns null if no plugins were
        /// discovered at all (nothing to report).
        /// </summary>
        private static FlowDocument? BuildPluginSummary()
        {
            var plugins = Model.Extensibility.PluginManager.Plugins;
            var failures = Model.Extensibility.PluginManager.FailedPlugins;

            if (plugins.Count == 0 && failures.Count == 0)
                return null;

            var doc = NewDocument();
            AddSectionHeader(doc, "Extended parsers");

            if (plugins.Count > 0)
            {
                foreach (var plugin in plugins)
                    AddLine(doc, plugin.Name, string.Join(", ", plugin.SupportedExtensions));
            }
            else
            {
                AddLine(doc, "Loaded", "(none)");
            }

            if (failures.Count > 0)
            {
                AddSectionHeader(doc, "Extended parsers - failed to load");
                foreach (var failure in failures)
                    AddLine(doc, failure.Source, failure.Error);
            }

            return doc;
        }

        /// <summary>
        /// Appends the plugin summary section (see <see cref="BuildPluginSummary"/>) to an
        /// existing document, if there is anything to report.
        /// </summary>
        private static void AppendPluginSummary(FlowDocument doc)
        {
            var pluginDoc = BuildPluginSummary();
            if (pluginDoc is null)
                return;

            var blocks = pluginDoc.Blocks.ToList();
            foreach (var block in blocks)
            {
                pluginDoc.Blocks.Remove(block);
                doc.Blocks.Add(block);
            }
        }

        private static FlowDocument? BuildLoaderSummary(HttpTraceFile trace)
        {
            var counts = trace.ProviderEventCounts;
            bool hasProviderCounts = counts is not null && counts.Count > 0;
            bool hasRows = trace.Count > 0;

            if (!hasProviderCounts && !hasRows)
                return BuildPluginSummary();

            var doc = NewDocument();
            AppendPluginSummary(doc);
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
            bool showProcess = _trace is SazTraceFile;
            ProcessColumnMenuItem.Visibility = showProcess ? Visibility.Visible : Visibility.Collapsed;
            ProcessColumnMenuItem.IsChecked = showProcess;
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
            RestViewer.Document = new FlowDocument();
            ApplyRichTextBoxWrap(RestViewer, _restWrap);
            RestJsonTree.ItemsSource = null;
            RestJsonTree.Visibility = Visibility.Collapsed;
            SoapViewer.Document = new FlowDocument();
            ApplyRichTextBoxWrap(SoapViewer, _soapWrap);

            _requestPayload = null;
            _requestHeaders = null;
            _responsePayload = null;
            _responseHeaders = null;
            _requestPayloadIsLarge = false;
            _responsePayloadIsLarge = false;
            _requestPayloadFormatOverride = false;
            _responsePayloadFormatOverride = false;
            UpdateLargePayloadBanner(request: true);
            UpdateLargePayloadBanner(request: false);

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
            return _isSplitView || MainTabControl.SelectedIndex == 1; // Request is the second tab (index 1)
        }

        private bool IsResponseTabSelected()
        {
            return _isSplitView || MainTabControl.SelectedIndex == 2; // Response is the third tab (index 2)
        }

        private const int RestTabIndex = 3;
        private const int SoapTabIndex = 4;
        private const int MapiTabIndex = 5;

        private async void RequestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_trace is null || RequestList.SelectedItem is not DataRowView drv)
            {
                ClearViewers();
                return;
            }

            HttpRequest request = _trace.GetRequest(drv.Row);
            HttpResponse? response = _trace.GetResponse(drv.Row);

            // A "Format anyway" override only applies to the payload it was requested for.
            _requestPayloadFormatOverride = false;
            _responsePayloadFormatOverride = false;

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
                var (restDoc, restJsonRoots) = BuildRestDocument(request, response);
                RestViewer.Document = restDoc;
                ApplyRichTextBoxWrap(RestViewer, _restWrap);
                if (restJsonRoots is { Count: > 0 })
                {
                    RestJsonTree.ItemsSource = restJsonRoots;
                    RestJsonTree.Visibility = Visibility.Visible;
                }
                else
                {
                    RestJsonTree.ItemsSource = null;
                    RestJsonTree.Visibility = Visibility.Collapsed;
                }
                SoapViewer.Document = BuildSoapDocument(request, response);
                ApplyRichTextBoxWrap(SoapViewer, _soapWrap);
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
            RequestContentTypeText.Text = GetContentType(request.Headers);

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
                ResponseContentTypeText.Text = string.Empty;
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
            ResponseContentTypeText.Text = GetContentType(response.Headers);

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
                    else if (rtb == RestViewer) _restWrap = wrap;
                    else if (rtb == SoapViewer) _soapWrap = wrap;
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
            _requestPayloadFormatOverride = false;
            await RenderRequestPayload((PayloadFormat)RequestPayloadFormatCombo.SelectedIndex);
        }

        private async void ResponsePayloadFormat_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Ignore during population - format is set programmatically, not by user
            if (_isPopulatingViewers || _responsePayload is null)
                return;
            _responsePayloadFormatOverride = false;
            await RenderResponsePayload((PayloadFormat)ResponsePayloadFormatCombo.SelectedIndex);
        }

        private async Task RenderRequestPayload(PayloadFormat format, bool showBusyIndicator = true, bool forceFullRender = false)
        {
            _requestPayloadIsLarge = await RenderPayload(format, _requestPayload!, _requestHeaders,
                RequestPayloadEditor, RequestPayloadImageScroll, RequestPayloadImage,
                RequestPayloadSvgScroll, RequestPayloadSvg, showBusyIndicator, forceFullRender);
            UpdateLargePayloadBanner(request: true);
        }

        private async Task RenderResponsePayload(PayloadFormat format, bool showBusyIndicator = true, bool forceFullRender = false)
        {
            _responsePayloadIsLarge = await RenderPayload(format, _responsePayload!, _responseHeaders,
                ResponsePayloadEditor, ResponsePayloadImageScroll, ResponsePayloadImage,
                ResponsePayloadSvgScroll, ResponsePayloadSvg, showBusyIndicator, forceFullRender);
            UpdateLargePayloadBanner(request: false);
        }

        private async Task<bool> RenderPayload(
            PayloadFormat format,
            byte[] payload,
            IReadOnlyList<KeyValuePair<string, string>>? headers,
            ICSharpCode.AvalonEdit.TextEditor editor,
            ScrollViewer imageScroll, Image imageControl,
            ScrollViewer svgScroll, SvgViewbox svgControl,
            bool showBusyIndicator = true,
            bool forceFullRender = false)
        {
            // Threshold for showing busy indicator (1MB)
            const int BusyIndicatorThreshold = 1_048_576;
            bool shouldShowBusy = showBusyIndicator && payload.Length >= BusyIndicatorThreshold;
            bool isLargeFile = false;

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
                    return false;
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
                        return false;

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
                        return false;
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

                // For very large files, skip pretty-printing as it's slow and disable word wrap,
                // unless the user explicitly asked to format anyway (forceFullRender).
                isLargeFile = text.Length > LargePayloadThreshold;
                bool skipLargeFileOptimizations = isLargeFile && !forceFullRender;

                // Pretty-print on background thread for large files
                string displayText;
                if (shouldShowBusy)
                {
                    displayText = await Task.Run(() =>
                    {
                        switch (format)
                        {
                            case PayloadFormat.Json:
                                return skipLargeFileOptimizations ? text : (TryPrettyPrintJson(text, out var pretty) ? pretty : text);
                            case PayloadFormat.Xml:
                                return skipLargeFileOptimizations ? text : (TryPrettyPrintXml(text, out var xml) ? xml : text);
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
                            displayText = skipLargeFileOptimizations ? text : (TryPrettyPrintJson(text, out var pretty) ? pretty : text);
                            break;
                        case PayloadFormat.Xml:
                            displayText = skipLargeFileOptimizations ? text : (TryPrettyPrintXml(text, out var xml) ? xml : text);
                            break;
                        default:
                            displayText = text;
                            break;
                    }
                }

                // For large files, disable performance-intensive features
                if (skipLargeFileOptimizations)
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

                // Apply syntax highlighting asynchronously on background thread to avoid UI freeze.
                // Skipped for large files: colorizing a huge (often single-line) body makes every
                // subsequent scroll/select - including Find navigation - extremely slow.
                if (!skipLargeFileOptimizations)
                {
                    ApplySyntaxHighlightingAsync(editor, format);
                }
            }
            finally
            {
                if (shouldShowBusy)
                {
                    SetBusy(false);
                }
            }

            return isLargeFile;
        }

        /// <summary>
        /// Shows/hides the "large payload" banner for the given side and switches its message
        /// between the "format anyway" offer and the "Find is disabled" notice once formatted.
        /// </summary>
        private void UpdateLargePayloadBanner(bool request)
        {
            var banner = request ? RequestBodyLargePayloadBanner : ResponseBodyLargePayloadBanner;
            var message = request ? RequestBodyLargePayloadMessage : ResponseBodyLargePayloadMessage;
            var button = request ? RequestBodyFormatAnywayButton : ResponseBodyFormatAnywayButton;
            var isLarge = request ? _requestPayloadIsLarge : _responsePayloadIsLarge;
            var overrideActive = request ? _requestPayloadFormatOverride : _responsePayloadFormatOverride;

            if (!isLarge)
            {
                banner.Visibility = Visibility.Collapsed;
                return;
            }

            banner.Visibility = Visibility.Visible;
            if (overrideActive)
            {
                message.Text = "This payload is fully formatted for readability. Find is disabled here - it would be slow on a payload this large.";
                button.Content = "Show unformatted";
            }
            else
            {
                message.Text = "Large payload: formatting, word wrap and syntax highlighting are disabled for performance.";
                button.Content = "Format anyway";
            }
        }

        private async void RequestFormatAnywayButton_Click(object sender, RoutedEventArgs e)
        {
            _requestPayloadFormatOverride = !_requestPayloadFormatOverride;
            CloseLocalFind(FindScope.RequestBody);
            await RenderRequestPayload((PayloadFormat)RequestPayloadFormatCombo.SelectedIndex, forceFullRender: _requestPayloadFormatOverride);
        }

        private async void ResponseFormatAnywayButton_Click(object sender, RoutedEventArgs e)
        {
            _responsePayloadFormatOverride = !_responsePayloadFormatOverride;
            CloseLocalFind(FindScope.ResponseBody);
            await RenderResponsePayload((PayloadFormat)ResponsePayloadFormatCombo.SelectedIndex, forceFullRender: _responsePayloadFormatOverride);
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
            // Reapply highlighting to request payload editor if it has text (skip large payloads - see LargePayloadThreshold)
            if (!string.IsNullOrEmpty(RequestPayloadEditor.Text) && RequestPayloadEditor.Text.Length <= LargePayloadThreshold)
            {
                var requestFormat = (PayloadFormat)RequestPayloadFormatCombo.SelectedIndex;
                if (requestFormat is PayloadFormat.Json or PayloadFormat.Xml or PayloadFormat.Html or PayloadFormat.JavaScript)
                {
                    ApplySyntaxHighlightingAsync(RequestPayloadEditor, requestFormat);
                }
            }

            // Reapply highlighting to response payload editor if it has text (skip large payloads - see LargePayloadThreshold)
            if (!string.IsNullOrEmpty(ResponsePayloadEditor.Text) && ResponsePayloadEditor.Text.Length <= LargePayloadThreshold)
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

        private static string GetContentType(IReadOnlyList<KeyValuePair<string, string>>? headers)
        {
            if (headers is null)
                return string.Empty;
            foreach (var h in headers)
            {
                if (!string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;
                return h.Value ?? string.Empty;
            }
            return string.Empty;
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

        /// <summary>Decodes a payload to text using its Content-Type charset (defaulting to UTF-8). Internal for reuse by the MCP tools.</summary>
        internal static string DecodePayloadText(byte[] payload, IReadOnlyList<KeyValuePair<string, string>>? headers)
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

        private FlowDocument BuildSummary(HttpRequest request, HttpResponse? response)
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

            AppendContentTypeSummary(doc, request, response);

            return doc;
        }

        /// <summary>
        /// Appends a "Detected Content" line to the summary identifying any recognised protocol
        /// content (REST, SOAP, MAPI) found in the request/response, each rendered as a
        /// clickable link that switches the main tab control to the corresponding viewer tab.
        /// </summary>
        private void AppendContentTypeSummary(FlowDocument doc, HttpRequest request, HttpResponse? response)
        {
            var detected = new List<(string Label, int TabIndex)>();

            if (IsRestContent(request, response, out _))
                detected.Add(("REST", RestTabIndex));

            if (SoapAnalyzer.AnalyzeRequest(request).IsSoap || SoapAnalyzer.AnalyzeResponse(response).IsSoap)
                detected.Add(("SOAP", SoapTabIndex));

            if (MapiHttpDecoder.IsMapiHttp(request) || MapiHttpDecoder.IsMapiHttp(response))
                detected.Add(("MAPI", MapiTabIndex));

            if (detected.Count == 0)
                return;

            AddSectionHeader(doc, "Detected Content");
            var para = new Paragraph { Margin = new Thickness(0) };
            for (int i = 0; i < detected.Count; i++)
            {
                if (i > 0)
                    para.Inlines.Add(new Run(", "));

                var (label, tabIndex) = detected[i];
                var link = new Hyperlink(new Run(label))
                {
                    TextDecorations = TextDecorations.Underline,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Focusable = true,
                };
                link.Click += (_, _) => SelectAnalysisTab(tabIndex);
                // RichTextBox (even IsReadOnly) intercepts mouse-up for selection handling before
                // Hyperlink.Click reliably fires, so also switch tabs on mouse-down as a fallback.
                link.PreviewMouseLeftButtonDown += (_, args) =>
                {
                    SelectAnalysisTab(tabIndex);
                    args.Handled = true;
                };
                para.Inlines.Add(link);
            }
            doc.Blocks.Add(para);
        }

        private void SelectAnalysisTab(int mainTabIndex)
        {
            if (_isSplitView)
                TopLayoutTabControl.SelectedIndex = mainTabIndex - 1;
            else
                MainTabControl.SelectedIndex = mainTabIndex;
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
                        p.Foreground = ThemeManager.GetContrastingForeground(matchedRule.BackgroundColor);
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

        private static (FlowDocument Document, List<JsonTreeNode>? JsonRoots) BuildRestDocument(HttpRequest request, HttpResponse? response)
        {
            var doc = NewDocument();

            if (!IsRestContent(request, response, out var analysis))
            {
                doc.Blocks.Add(new Paragraph(new Run("(no REST content detected)")
                { FontStyle = FontStyles.Italic }));
                return (doc, null);
            }

            AddSectionHeader(doc, "Request");
            AddLine(doc, "Method", request.Method);
            AddLine(doc, "URL", request.Url?.ToString() ?? string.Empty);
            if (!string.IsNullOrEmpty(analysis.ApiVersion))
                AddLine(doc, "API version", analysis.ApiVersion!);

            var breakdownPara = new Paragraph { Margin = new Thickness(0, 6, 0, 6) };
            breakdownPara.Inlines.Add(new Run("Resource path breakdown:") { FontWeight = FontWeights.Bold });
            doc.Blocks.Add(breakdownPara);

            var list = new List();
            foreach (var segment in analysis.Segments)
            {
                var item = new ListItem();
                var p = new Paragraph { Margin = new Thickness(0) };
                p.Inlines.Add(new Run(segment.Collection) { FontWeight = FontWeights.Bold });
                if (!string.IsNullOrEmpty(segment.Identifier))
                {
                    p.Inlines.Add(new Run(" -> "));
                    p.Inlines.Add(new Run(segment.Identifier));
                    if (segment.IdentifierIsWellKnown)
                        p.Inlines.Add(new Run(" (well-known)") { FontStyle = FontStyles.Italic });
                }
                item.Blocks.Add(p);
                list.ListItems.Add(item);
            }
            doc.Blocks.Add(list);

            if (analysis.QueryParameters.Count > 0)
            {
                var queryPara = new Paragraph { Margin = new Thickness(0, 6, 0, 2) };
                queryPara.Inlines.Add(new Run("Query parameters:") { FontWeight = FontWeights.Bold });
                doc.Blocks.Add(queryPara);
                foreach (var q in analysis.QueryParameters)
                    AddLine(doc, q.Key, q.Value);
            }

            List<JsonTreeNode>? jsonRoots = null;

            if (response is not null)
            {
                AddSectionHeader(doc, "Response");
                var status = GetResponseStatus(response);
                if (!string.IsNullOrEmpty(status))
                    AddLine(doc, "Status", status);

                var contentType = GetContentType(response.Headers);
                if (!string.IsNullOrEmpty(contentType))
                    AddLine(doc, "Content-Type", contentType);

                if (response.Payload is { Length: > 0 })
                {
                    string text = DecodePayloadText(response.Payload, response.Headers);

                    jsonRoots = TryBuildJsonTree(text);
                    if (jsonRoots is not null)
                    {
                        var fieldsPara = new Paragraph { Margin = new Thickness(0, 6, 0, 2) };
                        fieldsPara.Inlines.Add(new Run("Response fields:") { FontWeight = FontWeights.Bold });
                        doc.Blocks.Add(fieldsPara);
                        doc.Blocks.Add(new Paragraph(new Run("(see expandable tree below)") { FontStyle = FontStyles.Italic }));
                    }
                    else
                    {
                        if (TryPrettyPrintJson(text, out var pretty))
                            text = pretty;

                        var bodyPara = new Paragraph
                        {
                            FontFamily = new FontFamily("Consolas"),
                            Margin = new Thickness(0, 4, 0, 8),
                        };
                        bodyPara.Inlines.Add(new Run("Body:") { FontWeight = FontWeights.Bold });
                        bodyPara.Inlines.Add(new LineBreak());
                        bodyPara.Inlines.Add(new Run(text));
                        doc.Blocks.Add(bodyPara);
                    }
                }
                else
                {
                    doc.Blocks.Add(new Paragraph(new Run("(no response body)") { FontStyle = FontStyles.Italic }));
                }
            }

            return (doc, jsonRoots);
        }

        /// <summary>
        /// Attempts to parse the given text as JSON and build a tree of <see cref="JsonTreeNode"/>
        /// for display in the REST tab's collapsible <see cref="TreeView"/>. Returns null if the
        /// text is not valid JSON, in which case the caller should fall back to the raw body.
        /// </summary>
        private static List<JsonTreeNode>? TryBuildJsonTree(string text)
        {
            text = text.TrimStart();
            if (text.Length == 0 || (text[0] != '{' && text[0] != '['))
                return null;

            JsonDocument jsonDoc;
            try
            {
                jsonDoc = JsonDocument.Parse(text);
            }
            catch
            {
                return null;
            }

            using (jsonDoc)
            {
                var roots = new List<JsonTreeNode>();
                BuildJsonTreeNode(roots, jsonDoc.RootElement, null, isRoot: true);
                return roots;
            }
        }

        /// <summary>
        /// Recursively converts a JSON element into <see cref="JsonTreeNode"/> entries, labelling
        /// each field with its name (when part of an object) and formatting scalar values inline.
        /// Arrays are rendered as indexed nested nodes; objects as nested nodes of "name: value"
        /// entries. The top level of an array/object is expanded by default for immediate visibility.
        /// </summary>
        private static void BuildJsonTreeNode(List<JsonTreeNode> nodes, JsonElement element, string? name, bool isRoot = false)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    {
                        var properties = element.EnumerateObject().ToList();
                        if (properties.Count == 0)
                        {
                            nodes.Add(new JsonTreeNode(FormatLabel(name, "{ }")));
                            break;
                        }

                        if (name is null)
                        {
                            foreach (var prop in properties)
                                BuildJsonTreeNode(nodes, prop.Value, prop.Name, isRoot);
                        }
                        else
                        {
                            var node = new JsonTreeNode(name, isRoot);
                            foreach (var prop in properties)
                                BuildJsonTreeNode(node.Children, prop.Value, prop.Name);
                            nodes.Add(node);
                        }
                        break;
                    }

                case JsonValueKind.Array:
                    {
                        var arrayItems = element.EnumerateArray().ToList();
                        if (arrayItems.Count == 0)
                        {
                            nodes.Add(new JsonTreeNode(FormatLabel(name, "[ ]")));
                            break;
                        }

                        var node = new JsonTreeNode($"{name ?? "Array"} [{arrayItems.Count}]", isRoot);
                        for (int i = 0; i < arrayItems.Count; i++)
                            BuildJsonTreeNode(node.Children, arrayItems[i], $"[{i}]");
                        nodes.Add(node);
                        break;
                    }

                default:
                    nodes.Add(new JsonTreeNode(FormatLabel(name, FormatJsonScalar(element))));
                    break;
            }
        }

        private static string FormatLabel(string? name, string value)
            => string.IsNullOrEmpty(name) ? value : $"{name}: {value}";

        private static string FormatJsonScalar(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "(null)",
            _ => element.GetRawText(),
        };

        /// <summary>
        /// Determines whether the given request/response pair should be treated as REST content
        /// for the REST tab / summary content-type detection. Requires the URL to look
        /// resource-path-like AND the payload to actually look like JSON (or be empty), and
        /// excludes anything that parses as a SOAP envelope.
        /// </summary>
        private static bool IsRestContent(HttpRequest request, HttpResponse? response, out RestAnalysisResult analysis)
        {
            analysis = RestAnalyzer.Analyze(request.Url);
            bool looksLikeRestUrl = analysis.IsRest;
            bool isSoap = SoapAnalyzer.AnalyzeRequest(request).IsSoap || SoapAnalyzer.AnalyzeResponse(response).IsSoap;

            // The URL-based heuristic in RestAnalyzer can false-positive on non-REST endpoints
            // whose path happens to look segment-like (e.g. SOAP's /EWS/Exchange.asmx). Guard
            // against that by requiring the content to actually look like a REST (JSON) payload
            // and not a SOAP envelope.
            bool contentLooksJson = PayloadLooksLikeJson(request.Payload, request.Headers) ||
                (response is not null && PayloadLooksLikeJson(response.Payload, response.Headers));
            bool contentTypeSuggestsNonJson =
                ContentTypeSuggestsNonJson(request.Headers) ||
                (response is not null && ContentTypeSuggestsNonJson(response.Headers));

            return looksLikeRestUrl && !isSoap && !contentTypeSuggestsNonJson &&
                (contentLooksJson || (request.Payload is not { Length: > 0 } && response?.Payload is not { Length: > 0 }));
        }

        /// <summary>Returns true if the Content-Type header indicates a non-JSON, non-REST body (e.g. SOAP/XML).</summary>
        private static bool ContentTypeSuggestsNonJson(IReadOnlyList<KeyValuePair<string, string>>? headers)
        {
            var contentType = GetContentType(headers);
            if (string.IsNullOrEmpty(contentType))
                return false;
            return contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("soap", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Cheaply checks whether a payload looks like a JSON document (object or array).</summary>
        private static bool PayloadLooksLikeJson(byte[]? payload, IReadOnlyList<KeyValuePair<string, string>>? headers)
        {
            if (payload is null || payload.Length == 0)
                return false;

            var contentType = GetContentType(headers);
            if (!string.IsNullOrEmpty(contentType) && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                // An explicit non-JSON content-type (xml, soap, text, etc.) rules this out.
                if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("soap", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var text = DecodePayloadText(payload, headers).TrimStart();
            if (text.Length == 0 || (text[0] != '{' && text[0] != '['))
                return false;

            try
            {
                using var _ = JsonDocument.Parse(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static FlowDocument BuildSoapDocument(HttpRequest request, HttpResponse? response)
        {
            var doc = NewDocument();

            var requestAnalysis = SoapAnalyzer.AnalyzeRequest(request);
            var responseAnalysis = SoapAnalyzer.AnalyzeResponse(response);

            if (!requestAnalysis.IsSoap && !responseAnalysis.IsSoap)
            {
                doc.Blocks.Add(new Paragraph(new Run("(no SOAP content detected)")
                { FontStyle = FontStyles.Italic }));
                return doc;
            }

            AddSectionHeader(doc, "Request");
            AddLine(doc, "Method", string.IsNullOrEmpty(requestAnalysis.Method) ? "(unknown)" : requestAnalysis.Method);

            var anchorMailbox = requestAnalysis.AnchorMailbox;
            if (!string.IsNullOrEmpty(anchorMailbox))
                AddLine(doc, "X-AnchorMailbox", anchorMailbox);

            if (requestAnalysis.Headers.Count > 0)
            {
                var headersPara = new Paragraph { Margin = new Thickness(0, 6, 0, 2) };
                headersPara.Inlines.Add(new Run("SOAP headers:") { FontWeight = FontWeights.Bold });
                doc.Blocks.Add(headersPara);
                foreach (var h in requestAnalysis.Headers)
                    AddLine(doc, h.Name, h.Value);
            }
            else
            {
                doc.Blocks.Add(new Paragraph(new Run("(no SOAP headers)") { FontStyle = FontStyles.Italic }));
            }

            if (response is not null)
            {
                AddSectionHeader(doc, "Response");
                var status = GetResponseStatus(response);
                if (!string.IsNullOrEmpty(status))
                    AddLine(doc, "Status", status);

                if (responseAnalysis.IsSoap)
                {
                    if (responseAnalysis.IsFault)
                    {
                        // Note: SOAP services (including EWS) commonly return HTTP 200 even when
                        // the SOAP body describes an error, so surface the fault prominently here
                        // regardless of the HTTP status above.
                        var faultPara = new Paragraph { Margin = new Thickness(0, 4, 0, 4) };
                        faultPara.Inlines.Add(new Run("SOAP Fault") { FontWeight = FontWeights.Bold, Foreground = Brushes.Firebrick });
                        doc.Blocks.Add(faultPara);
                        if (!string.IsNullOrEmpty(responseAnalysis.FaultCode))
                            AddLine(doc, "Fault code", responseAnalysis.FaultCode);
                        if (!string.IsNullOrEmpty(responseAnalysis.FaultReason))
                            AddLine(doc, "Fault reason", responseAnalysis.FaultReason);
                    }
                    else
                    {
                        AppendSoapResponseOverview(doc, responseAnalysis);
                    }
                }
                else
                {
                    doc.Blocks.Add(new Paragraph(new Run("(response payload is not a SOAP envelope)")
                    { FontStyle = FontStyles.Italic }));
                }
            }

            return doc;
        }

        /// <summary>Appends a human-readable overview of the SOAP response (e.g. folder/item details returned).</summary>
        private static void AppendSoapResponseOverview(FlowDocument doc, SoapResponseAnalysis analysis)
        {
            if (analysis.Messages.Count == 0)
            {
                doc.Blocks.Add(new Paragraph(new Run("SOAP response indicates success (no Fault element).")
                { FontStyle = FontStyles.Italic }));
                return;
            }

            foreach (var message in analysis.Messages)
            {
                var summaryPara = new Paragraph { Margin = new Thickness(0, 4, 0, 4) };
                var responseClass = message.ResponseClass ?? "(unknown)";
                var isSuccess = string.Equals(message.ResponseClass, "Success", StringComparison.OrdinalIgnoreCase);
                summaryPara.Inlines.Add(new Run($"Result: {responseClass}")
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = isSuccess ? Brushes.SeaGreen : Brushes.DarkOrange,
                });
                if (!string.IsNullOrEmpty(message.ResponseCode))
                    summaryPara.Inlines.Add(new Run($"  ({message.ResponseCode})"));
                doc.Blocks.Add(summaryPara);

                if (message.Entries.Count == 0)
                {
                    doc.Blocks.Add(new Paragraph(new Run("(no items returned)") { FontStyle = FontStyles.Italic })
                    { Margin = new Thickness(0, 0, 0, 6) });
                    continue;
                }

                foreach (var entry in message.Entries)
                {
                    var titlePara = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                    titlePara.Inlines.Add(new Run(entry.Title) { FontWeight = FontWeights.Bold, TextDecorations = TextDecorations.Underline });
                    doc.Blocks.Add(titlePara);

                    foreach (var prop in entry.Properties)
                        AddLine(doc, prop.Key, prop.Value);
                }
            }
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