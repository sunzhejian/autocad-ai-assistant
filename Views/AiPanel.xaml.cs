using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
        private const string DefaultStatus = "Enter 发送 · Shift+Enter 换行；AI 回复不会自动执行 CAD 命令。";
        private readonly ModelSettingsStore _store = new ModelSettingsStore();
        private readonly OpenAiCompatibleClient _client = new OpenAiCompatibleClient();
        private readonly ObservableCollection<ModelConfiguration> _models = new ObservableCollection<ModelConfiguration>();
        private readonly ObservableCollection<ChatItem> _messages = new ObservableCollection<ChatItem>();
        private readonly List<ChatItem> _conversation = new List<ChatItem>();
        private bool _isSending;

        public AiPanel()
        {
            InitializeComponent();
            ModelCombo.ItemsSource = _models;
            MessageList.ItemsSource = _messages;
            ShortcutList.ItemsSource = CreateShortcuts();

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

        private static List<CadShortcut> CreateShortcuts()
        {
            return new List<CadShortcut>
            {
                new CadShortcut { Name = "L", Description = "直线", Command = "_.LINE " },
                new CadShortcut { Name = "PL", Description = "多段线", Command = "_.PLINE " },
                new CadShortcut { Name = "C", Description = "圆", Command = "_.CIRCLE " },
                new CadShortcut { Name = "REC", Description = "矩形", Command = "_.RECTANG " },
                new CadShortcut { Name = "A", Description = "圆弧", Command = "_.ARC " },
                new CadShortcut { Name = "M", Description = "移动", Command = "_.MOVE " },
                new CadShortcut { Name = "CO", Description = "复制", Command = "_.COPY " },
                new CadShortcut { Name = "O", Description = "偏移", Command = "_.OFFSET " },
                new CadShortcut { Name = "TR", Description = "修剪", Command = "_.TRIM " },
                new CadShortcut { Name = "EX", Description = "延伸", Command = "_.EXTEND " },
                new CadShortcut { Name = "E", Description = "删除", Command = "_.ERASE " },
                new CadShortcut { Name = "MA", Description = "特性匹配", Command = "_.MATCHPROP " },
                new CadShortcut { Name = "Z", Description = "缩放", Command = "_.ZOOM " },
                new CadShortcut { Name = "P", Description = "平移", Command = "_.PAN " },
                new CadShortcut { Name = "U", Description = "放弃", Command = "_.U " }
            };
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
            await SendAsync();
        }

        private async void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            await SendAsync();
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
            if (string.IsNullOrWhiteSpace(text)) return;

            var userItem = new ChatItem { IsUser = true, Content = text };
            _conversation.Add(userItem);
            AddMessage(userItem);
            InputBox.Clear();
            SetSending(true);

            try
            {
                var answer = await _client.CompleteAsync(model, _conversation.ToList());
                var assistantItem = new ChatItem { Role = model.Title, Content = answer };
                _conversation.Add(assistantItem);
                AddMessage(assistantItem);
                StatusText.Text = DefaultStatus;
            }
            catch (Exception ex)
            {
                AddMessage(new ChatItem { Role = "请求失败", Content = ex.Message });
                StatusText.Text = "请检查模型地址、密钥和网络连接。";
            }
            finally
            {
                SetSending(false);
                InputBox.Focus();
            }
        }

        private void SetSending(bool sending)
        {
            _isSending = sending;
            SendButton.IsEnabled = !sending;
            SendButton.Content = sending ? "请求中…" : "发送";
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
            _messages.Clear();
            _conversation.Clear();
            EmptyText.Visibility = Visibility.Visible;
            StatusText.Text = DefaultStatus;
        }

        private void Shortcut_Click(object sender, RoutedEventArgs e)
        {
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
