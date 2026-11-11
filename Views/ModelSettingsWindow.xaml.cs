using System;
using System.Windows;
using AutoCAD.AI.Assistant.Models;
using AutoCAD.AI.Assistant.Services;

namespace AutoCAD.AI.Assistant.Views
{
    public partial class ModelSettingsWindow : Window
    {
        public ModelConfiguration Model { get; private set; }
        private readonly ModelConfiguration _editing;

        public ModelSettingsWindow() : this(null) { }

        public ModelSettingsWindow(ModelConfiguration existing)
        {
            InitializeComponent();
            _editing = existing;
            if (existing == null) return;
            Title = "编辑模型"; SaveModelButton.Content = "保存修改";
            UrlBox.Text = existing.RequestUrl; FullUrlCheck.IsChecked = existing.UseFullUrl;
            ModelIdBox.Text = existing.ModelId; DisplayNameBox.Text = existing.DisplayName ?? "";
            SystemPromptBox.Text = existing.SystemPrompt ?? "";
            KeyHint.Text = "留空保留现有加密密钥；输入新密钥才会替换。保存不会发起请求。";
            var effort = ModelConfiguration.NormalizeReasoningEffort(existing.ReasoningEffort) ?? "";
            foreach (System.Windows.Controls.ComboBoxItem option in ReasoningEffortBox.Items)
                if (Convert.ToString(option.Tag) == effort) { ReasoningEffortBox.SelectedItem = option; break; }
        }

        private void DisplayNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            NameCount.Text = DisplayNameBox.Text.Length + "/64";
        }

        private void FullUrl_Checked(object sender, RoutedEventArgs e)
        {
            if (UrlHint == null) return;
            UrlHint.Text = FullUrlCheck.IsChecked == true
                ? "将直接使用完整请求地址，不再追加路径。"
                : "填写服务基础地址，/chat/completions 会自动追加到末尾。";
        }

        private void ShowKey_Checked(object sender, RoutedEventArgs e)
        {
            if (ApiKeyTextBox == null || ApiKeyBox == null) return;
            if (ApiKeyTextBox.Visibility == Visibility.Visible)
            {
                ApiKeyBox.Password = ApiKeyTextBox.Text;
                ApiKeyTextBox.Visibility = Visibility.Collapsed;
                ApiKeyBox.Visibility = Visibility.Visible;
            }
            else
            {
                ApiKeyTextBox.Text = ApiKeyBox.Password;
                ApiKeyBox.Visibility = Visibility.Collapsed;
                ApiKeyTextBox.Visibility = Visibility.Visible;
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            UrlBox.Clear();
            ModelIdBox.Clear();
            DisplayNameBox.Clear();
            ApiKeyBox.Clear();
            ApiKeyTextBox.Clear();
            SystemPromptBox.Clear();
            ReasoningEffortBox.SelectedIndex = 0;
            FullUrlCheck.IsChecked = false;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var url = UrlBox.Text.Trim();
            var modelId = ModelIdBox.Text.Trim();
            var apiKey = ApiKeyTextBox.Visibility == Visibility.Visible ? ApiKeyTextBox.Text.Trim() : ApiKeyBox.Password.Trim();

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show("请输入有效的 HTTP 或 HTTPS 请求地址。", "自定义模型", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(modelId))
            {
                MessageBox.Show("请输入模型 ID。", "自定义模型", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(apiKey) && (_editing == null || String.IsNullOrWhiteSpace(_editing.ApiKeyProtected)))
            {
                MessageBox.Show("请输入 API 密钥。", "自定义模型", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Model = BuildModel(apiKey);
            DialogResult = true;
        }

        private ModelConfiguration BuildModel(string apiKey)
        {
            return new ModelConfiguration
            {
                Id = _editing == null ? Guid.NewGuid().ToString("N") : _editing.Id,
                RequestUrl = UrlBox.Text.Trim(),
                UseFullUrl = FullUrlCheck.IsChecked == true,
                ModelId = ModelIdBox.Text.Trim(),
                DisplayName = DisplayNameBox.Text.Trim(),
                ApiKeyProtected = String.IsNullOrWhiteSpace(apiKey) ? _editing.ApiKeyProtected : SecretProtector.Protect(apiKey),
                SystemPrompt = SystemPromptBox.Text.Trim(),
                ReasoningEffort = ModelConfiguration.NormalizeReasoningEffort(Convert.ToString(((System.Windows.Controls.ComboBoxItem)ReasoningEffortBox.SelectedItem).Tag))
            };
        }
    }
}
