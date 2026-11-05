using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using AutoCAD.AI.Assistant.Models;
using AutoCAD.AI.Assistant.Services;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCAD.AI.Assistant.Views
{
    public partial class AiPanel : UserControl
    {
        private const string DefaultStatus = "Enter 发送 · Ctrl+V 粘贴图纸图片可自动复刻 · 绘完输入 U 撤销。";
        // 该模型服务的 SSE 实测比非流式慢，且偶发 60 秒超时；保留流式能力，默认走非流式。
        private static readonly bool StreamingEnabled = false;
        private readonly ModelSettingsStore _store = new ModelSettingsStore();
        private readonly OpenAiCompatibleClient _client = new OpenAiCompatibleClient();
        private readonly ObservableCollection<ModelConfiguration> _models = new ObservableCollection<ModelConfiguration>();
        private readonly ObservableCollection<ChatItem> _messages = new ObservableCollection<ChatItem>();
        private readonly ObservableCollection<CadShortcut> _shortcuts = new ObservableCollection<CadShortcut>();
        private readonly List<ChatItem> _conversation = new List<ChatItem>();
        private bool _isSending;
        private CancellationTokenSource _requestCancellation;
        private bool _sortReady;
        private string _pendingImagePath;

        public AiPanel()
        {
            InitializeComponent();
            ModelCombo.ItemsSource = _models;
            MessageList.ItemsSource = _messages;
            ShortcutList.ItemsSource = _shortcuts;
            RefreshShortcuts();
            SortByCount.IsChecked = ShortcutCatalog.SortMode != "name";
            SortByName.IsChecked = ShortcutCatalog.SortMode == "name";
            _sortReady = true;
            ShortcutCatalog.Changed += OnShortcutsChanged;

            try
            {
                foreach (var model in _store.Load()) _models.Add(model);
                ModelCombo.SelectedIndex = _models.Count > 0 ? 0 : -1;
            }
            catch (Exception ex)
            {
                StatusText.Text = "模型配置读取失败：" + ex.Message;
            }
        }

        private void OnShortcutsChanged()
        {
            if (!ShortcutPopup.IsOpen) return;
            Dispatcher.BeginInvoke(new Action(RefreshShortcuts));
        }

        private void RefreshShortcuts()
        {
            var items = ShortcutCatalog.GetSorted();
            _shortcuts.Clear();
            foreach (var item in items) _shortcuts.Add(item);
        }

        private void SortMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_sortReady) return;
            ShortcutCatalog.SetSortMode(SortByName.IsChecked == true ? "name" : "count");
            RefreshShortcuts();
        }

        private void AddModel_Click(object sender, RoutedEventArgs e)
        {
            var window = new ModelSettingsWindow();
            if (window.ShowDialog() != true || window.Model == null) return;

            try
            {
                _models.Add(window.Model);
                _store.Save(_models);
                ModelCombo.SelectedItem = window.Model;
                StatusText.Text = "模型已添加：" + window.Model.Title;
            }
            catch (Exception ex)
            {
                _models.Remove(window.Model);
                MessageBox.Show("模型配置保存失败：" + ex.Message, "CAD AI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending) { if (_requestCancellation != null) _requestCancellation.Cancel(); return; }
            await SendAsync();
        }

        private async void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (TryAttachImage()) e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            await SendAsync();
        }

        private bool TryAttachImage()
        {
            try
            {
                var files = System.Windows.Clipboard.ContainsFileDropList()
                    ? System.Windows.Clipboard.GetFileDropList()
                    : null;
                if (files != null && files.Count > 0)
                {
                    var path = files[0];
                    var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                    if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".bmp")
                    {
                        SetPendingImage(path);
                        return true;
                    }
                }

                var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AutoCAD.AI.Assistant");
                if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
                var file = System.IO.Path.Combine(folder, "paste_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".png");

                var bitmap = System.Windows.Forms.Clipboard.GetImage();
                if (bitmap != null)
                {
                    bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    bitmap.Dispose();
                    SetPendingImage(file);
                    return true;
                }

                var source = System.Windows.Clipboard.GetImage();
                if (source == null) return false;
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                using (var stream = System.IO.File.Create(file)) encoder.Save(stream);
                SetPendingImage(file);
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "读取剪贴板图片失败：" + ex.Message;
                return false;
            }
        }

        private void AttachImage_Click(object sender, RoutedEventArgs e)
        {
            if (TryAttachImage()) return;
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "选择图纸图片";
            dialog.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp";
            if (dialog.ShowDialog() == true) SetPendingImage(dialog.FileName);
        }

        /// <summary>激活承载面板的窗口并把焦点放到输入框（AutoCAD 浮动面板默认不抢焦点）。</summary>
        public void FocusInput()
        {
            ActivateHost();
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }

        private void ActivateHost()
        {
            try
            {
                var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                if (source == null) return;
                var root = NativeMethods.GetAncestor(source.Handle, 2);
                if (root != IntPtr.Zero) NativeMethods.SetForegroundWindow(root);
            }
            catch
            {
            }
        }

        private void Panel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (source == null || InsideButton(source)) return;
            ActivateHost();
            if (!InputBox.IsKeyboardFocusWithin) InputBox.Focus();
        }

        private static bool InsideButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hwnd);
        }

        private void SetPendingImage(string path)
        {
            _pendingImagePath = path;
            PendingImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(path));
            PendingBox.Visibility = Visibility.Visible;
            StatusText.Text = "图片已附加，直接发送即可复刻；可再输入一句要求。";
            InputBox.Focus();
        }

        private void ClearPendingImage()
        {
            _pendingImagePath = null;
            PendingImage.Source = null;
            PendingBox.Visibility = Visibility.Collapsed;
        }

        private void RemovePending_Click(object sender, RoutedEventArgs e)
        {
            ClearPendingImage();
            StatusText.Text = DefaultStatus;
        }

        private void CropPending_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_pendingImagePath)) return;
            var window = new ImageCropWindow(_pendingImagePath);
            var owner = Window.GetWindow(this);
            if (owner != null) window.Owner = owner;
            if (window.ShowDialog() != true || string.IsNullOrEmpty(window.ResultPath)) return;
            SetPendingImage(window.ResultPath);
            StatusText.Text = "已框选复刻区域，发送后只复刻该区域。";
        }

        private void Diag_Click(object sender, MouseButtonEventArgs e)
        {
            var path = DiagnosticsLog.Export();
            if (path == null)
            {
                StatusText.Text = "诊断导出失败：" + DiagnosticsLog.Folder;
                return;
            }
            StatusText.Text = "诊断已导出：" + path;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch
            {
            }
        }

        private async Task SendAsync()
        {
            if (_isSending) return;
            var model = ModelCombo.SelectedItem as ModelConfiguration;
            if (model == null)
            {
                AddModel_Click(this, new RoutedEventArgs());
                return;
            }

            var text = InputBox.Text.Trim();
            var imagePath = _pendingImagePath;
            var detailedImages = DetailedImageCheck.IsChecked == true;
            var reuseCachedPlan = ReusePlanCheck.IsChecked == true;
            var targetDocument = AcApplication.DocumentManager.MdiActiveDocument;
            if (targetDocument == null) { StatusText.Text = "请先新建或打开图形。"; return; }
            if (string.IsNullOrWhiteSpace(text) && imagePath == null) return;
            if (string.IsNullOrWhiteSpace(text)) text = "复刻这张图纸：只绘制可见视图，先识别全部尺寸与比例，再按 1:1 输出绘图指令，不补充图片外的内容。";

            var userItem = new ChatItem { IsUser = true, Content = text, ImagePath = imagePath };
            _conversation.Add(userItem);
            AddMessage(userItem);
            InputBox.Clear();
            ClearPendingImage();
            SetSending(true);
            _requestCancellation = new CancellationTokenSource();
            var cancellation = _requestCancellation.Token;

            var history = _conversation.ToList();
            var assistantItem = new ChatItem { Role = model.Title, Content = string.Empty };
            AddMessage(assistantItem);
            var buffer = new System.Text.StringBuilder();
            var gate = new object();
            var streaming = true;
            var update = new Action(delegate
            {
                if (!streaming) return;
                string raw;
                lock (gate) raw = buffer.ToString();
                var fence = raw.IndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) raw = raw.Substring(0, fence).TrimEnd() + "\n（正在生成绘图指令…）";
                assistantItem.Content = raw;
            });

            try
            {
                var context = string.Empty;
                try
                {
                    context = CadDrawingExecutor.BuildContext();
                }
                catch
                {
                }

                Action<string> onDelta = null;
                if (StreamingEnabled)
                {
                    onDelta = delegate(string delta)
                    {
                        lock (gate) buffer.Append(delta);
                        Dispatcher.BeginInvoke(update);
                    };
                }
                if (imagePath != null) StatusText.Text = "正在增强图纸清晰度并识别，请稍候…";
                string cacheKey = null;
                string cachedPlan = null;
                var cache = new DrawingPlanCache();
                if (imagePath != null)
                {
                    cacheKey = await Task.Run(() => DrawingPlanCache.Key(imagePath, text, model, detailedImages), cancellation);
                    if (reuseCachedPlan) cachedPlan = cache.TryRead(cacheKey);
                }
                var answer = cachedPlan == null ? await _client.CompleteAsync(model, history, context, imagePath, onDelta, cancellation,
                    status => Dispatcher.BeginInvoke(new Action(() => StatusText.Text = status)), detailedImages)
                    : "已复用相同图纸、提示和模型的本地已执行计划；源图识别准确性仍需核对。\n```cad\n" + cachedPlan + "\n```";

                var plan = CadDrawingExecutor.ExtractPlan(answer);
                var display = CadDrawingExecutor.RemovePlan(answer);
                if (string.IsNullOrWhiteSpace(display)) display = plan == null ? answer : "已生成绘图指令。";

                string validationError = null;
                if (plan != null)
                {
                    validationError = CadPlanValidator.Validate(plan);
                    if (validationError != null)
                    {
                        plan = null;
                        display = display + "\n\n绘图指令未执行：" + validationError;
                    }
                }

                streaming = false;
                assistantItem.Content = display;
                _conversation.Add(new ChatItem { IsUser = false, Role = model.Title, Content = answer });

                if (plan == null)
                {
                    StatusText.Text = validationError == null ? DefaultStatus : "绘图指令校验未通过，已跳过执行。";
                }
                else
                {
                    StatusText.Text = "正在绘制…";
                    try
                    {
                        DiagnosticsLog.SavePlan(plan);
                        DiagnosticsLog.Write("绘图计划：" + CadPlanValidator.Summarize(plan));
                        cancellation.ThrowIfCancellationRequested();
                        if (AcApplication.DocumentManager.MdiActiveDocument != targetDocument)
                            throw new InvalidOperationException("活动图形已切换。计划已保存在本地，可切回原图后用 AIPLAN 执行。");
                        var message = await CadExecutionQueue.Enqueue(targetDocument, plan, cancellation);
                        if (imagePath != null) await Task.Run(() => cache.SaveExecuted(cacheKey, plan));
                        if (imagePath != null) message += " " + CadPlanQuality.Describe(plan);
                        DiagnosticsLog.Write("绘图完成：" + message);
                        StatusText.Text = message;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error("绘图", ex);
                        StatusText.Text = "绘图失败：" + ex.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                if (ex is OperationCanceledException)
                {
                    message = cancellation.IsCancellationRequested ? "已取消，未继续执行绘图。" : "请求超时（文本总计 120 秒 / 图片总计 240 秒，含重试与修正）。";
                }
                DiagnosticsLog.Error("对话", ex);
                streaming = false;
                assistantItem.Role = "请求失败";
                assistantItem.Content = message;
                StatusText.Text = "请检查模型地址、密钥和网络连接。";
            }
            finally
            {
                streaming = false;
                _requestCancellation.Dispose();
                _requestCancellation = null;
                SetSending(false);
                InputBox.Focus();
            }
        }

        private void SetSending(bool sending)
        {
            _isSending = sending;
            SendButton.IsEnabled = true;
            SendButton.Content = sending ? "取消" : "发送";
            ModelCombo.IsEnabled = !sending;
        }

        private void AddMessage(ChatItem item)
        {
            _messages.Add(item);
            EmptyText.Visibility = Visibility.Collapsed;
            MessageScroll.Dispatcher.BeginInvoke(new Action(() => MessageScroll.ScrollToEnd()));
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending) { StatusText.Text = "请先取消当前请求再清空。"; return; }
            _messages.Clear();
            _conversation.Clear();
            ClearPendingImage();
            EmptyText.Visibility = Visibility.Visible;
            StatusText.Text = DefaultStatus;
        }

        private void ShortcutToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!ShortcutPopup.IsOpen)
            {
                ShortcutCatalog.Flush();
                RefreshShortcuts();
            }
            ShortcutPopup.IsOpen = !ShortcutPopup.IsOpen;
        }

        private void Shortcut_Click(object sender, RoutedEventArgs e)
        {
            ShortcutPopup.IsOpen = false;
            var button = sender as Button;
            var shortcut = button == null ? null : button.DataContext as CadShortcut;
            if (shortcut == null) return;

            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                StatusText.Text = "没有活动图形，无法执行命令。";
                return;
            }

            try
            {
                document.SendStringToExecute(shortcut.Command, true, false, true);
                StatusText.Text = "已发送 " + shortcut.Description + "（" + shortcut.Name + "），请在绘图区按命令提示继续。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "CAD 命令发送失败：" + ex.Message;
            }
        }
    }
}
