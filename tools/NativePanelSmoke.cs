using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(NativePanelSmoke))]
public sealed class NativePanelSmoke
{
    private static object Field(object instance, string name)
    { return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(instance); }
    private static void Set(object instance, string name, object value)
    { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(instance, value); }
    private static void Call(object instance, string name, params object[] args)
    { instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, args); }
    private static void Render(FrameworkElement panel, string file, int width = 450, int height = 720)
    {
        panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(0,0,width,height));
        bitmap.Render(background); bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file)) encoder.Save(stream);
    }
    [CommandMethod("AIPANELSMOKE")]
    public void Run()
    {
        var folder = Environment.GetEnvironmentVariable("CADAI_UI_OUTPUT");
        try
        {
            var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("CADAI_PLUGIN_DLL"));
            var panel = (FrameworkElement)Activator.CreateInstance(assembly.GetType("AutoCAD.AI.Assistant.Views.AiPanel", true));
            if (((CheckBox)Field(panel, "RecordUsageCheck")).IsChecked == true) throw new InvalidOperationException("Usage tracking must default to off");
            Render(panel, Path.Combine(folder, "empty.png"));
            if (((ComboBox)Field(panel,"ModelCombo")).ContextMenu == null)
                throw new InvalidOperationException("Current-model editor entry is missing");
            var modelType = assembly.GetType("AutoCAD.AI.Assistant.Models.ModelConfiguration", true);
            var profile = Activator.CreateInstance(modelType);
            modelType.GetProperty("Id").SetValue(profile, "stable-profile-id", null);
            modelType.GetProperty("ModelId").SetValue(profile, "auto", null);
            modelType.GetProperty("RequestUrl").SetValue(profile, "https://example.invalid/v1", null);
            modelType.GetProperty("ApiKeyProtected").SetValue(profile, "already-protected-placeholder", null);
            modelType.GetProperty("ReasoningEffort").SetValue(profile, "high", null);
            var settingsType = assembly.GetType("AutoCAD.AI.Assistant.Views.ModelSettingsWindow", true);
            var editor = (Window)Activator.CreateInstance(settingsType, new[] { profile });
            var budget = (ComboBox)Field(editor, "ReasoningEffortBox");
            if (Convert.ToString(((ComboBoxItem)budget.SelectedItem).Tag) != "high"
                || ((PasswordBox)Field(editor,"ApiKeyBox")).Password != ""
                || ((TextBox)Field(editor,"ApiKeyTextBox")).Text != "")
                throw new InvalidOperationException("Model editor must restore budget without revealing the stored key");
            foreach (ComboBoxItem option in budget.Items)
                if (Convert.ToString(option.Tag)=="low") { budget.SelectedItem=option; break; }
            var updated = settingsType.GetMethod("BuildModel",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,new object[]{""});
            if (Convert.ToString(modelType.GetProperty("Id").GetValue(updated,null))!="stable-profile-id"
                || Convert.ToString(modelType.GetProperty("ApiKeyProtected").GetValue(updated,null))!="already-protected-placeholder"
                || Convert.ToString(modelType.GetProperty("ReasoningEffort").GetValue(updated,null))!="low"
                || Convert.ToString(modelType.GetProperty("ReasoningEffort").GetValue(profile,null))!="high")
                throw new InvalidOperationException("Model edit changed identity, key, or the original unsaved profile");
            var editorContent=(FrameworkElement)editor.Content;
            Render(editorContent,Path.Combine(folder,"model-settings-collapsed.png"),540,790);
            var expanders=FindExpanders(editorContent);
            foreach(var expander in expanders) expander.IsExpanded=true;
            Render(editorContent,Path.Combine(folder,"model-reasoning-edit.png"),540,790);
            editor.Close();
            var json = File.ReadAllText(Environment.GetEnvironmentVariable("CADAI_UI_METADATA"));
            var source = new JavaScriptSerializer().Deserialize(json, assembly.GetType("AutoCAD.AI.Assistant.Services.PdfPageSource", true));
            var previewPath = Convert.ToString(source.GetType().GetProperty("preview").GetValue(source, null));
            var decode = assembly.GetType("AutoCAD.AI.Assistant.Views.ChatPreviewImage").GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic);
            var preview = Task.Run(() => (BitmapSource)decode.Invoke(null, new object[] { previewPath, 480 })).GetAwaiter().GetResult();
            if (!preview.IsFrozen || Math.Max(preview.PixelWidth, preview.PixelHeight) > 480) throw new InvalidOperationException("Preview is not bounded or transferable");
            Set(panel, "_pendingPdf", source);
            ((FrameworkElement)Field(panel, "PendingBox")).Visibility = Visibility.Visible;
            ((FrameworkElement)Field(panel, "PdfOptions")).Visibility = Visibility.Visible;
            ((TextBox)Field(panel, "PdfPageBox")).Text = "3";
            ((TextBlock)Field(panel, "PendingTitle")).Text = "河北样题 · 第 3/8 页 · 矢量 · 路径 2126";
            Call(panel, "LoadPreview", Convert.ToString(source.GetType().GetProperty("preview").GetValue(source, null)));
            ((ComboBox)Field(panel, "PdfRoute")).SelectedIndex = 0; Call(panel, "UpdatePdfRoute");
            Render(panel, Path.Combine(folder, "pdf-native.png"));
            ((ComboBox)Field(panel, "PdfRoute")).SelectedIndex = 1; Call(panel, "UpdatePdfRoute");
            Render(panel, Path.Combine(folder, "pdf-ai.png"));
            var messageType = assembly.GetType("AutoCAD.AI.Assistant.Models.ChatItem");
            for (var i = 0; i < 100; i++)
            {
                var item = Activator.CreateInstance(messageType); messageType.GetProperty("Content").SetValue(item, "message " + i, null);
                Call(panel, "AddMessage", item);
            }
            var messages = (System.Collections.ICollection)Field(panel, "_messages");
            if (messages.Count != 40) throw new InvalidOperationException("Panel message budget failed");
            Render(panel, Path.Combine(folder, "bounded-chat.png"));
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS WPF states, model reasoning editor preserves identity/encrypted key without revealing it, default tracking off, 40-message budget and background frozen preview " + preview.PixelWidth + "x" + preview.PixelHeight + ".");
        }
        catch (System.Exception ex) { File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString()); }
    }
    private static System.Collections.Generic.List<Expander> FindExpanders(DependencyObject root)
    {
        var result=new System.Collections.Generic.List<Expander>();
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            var expander=child as Expander;if(expander!=null)result.Add(expander);
            result.AddRange(FindExpanders(child));
        }
        return result;
    }
}
