using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private static void Render(FrameworkElement panel, string file)
    {
        panel.Measure(new Size(450, 720)); panel.Arrange(new Rect(0, 0, 450, 720)); panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(450, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
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
            Render(panel, Path.Combine(folder, "empty.png"));
            var json = File.ReadAllText(Environment.GetEnvironmentVariable("CADAI_UI_METADATA"));
            var source = new JavaScriptSerializer().Deserialize(json, assembly.GetType("AutoCAD.AI.Assistant.Services.PdfPageSource", true));
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
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS WPF panel initializes and renders empty, native PDF and AI PDF states.");
        }
        catch (System.Exception ex) { File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString()); }
    }
}
