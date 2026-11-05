using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace AutoCAD.AI.Assistant.Views
{
    /// <summary>Decode two bounded previews at a time, away from the CAD UI thread.</summary>
    public sealed class ChatPreviewImage : Image
    {
        private static readonly SemaphoreSlim Slots = new SemaphoreSlim(2);
        private CancellationTokenSource _request;
        public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
            "SourcePath", typeof(string), typeof(ChatPreviewImage), new PropertyMetadata(null, (obj, args) => ((ChatPreviewImage)obj).Reload()));
        public string SourcePath { get { return (string)GetValue(SourcePathProperty); } set { SetValue(SourcePathProperty, value); } }
        public ChatPreviewImage()
        {
            Loaded += (sender, args) => Reload();
            Unloaded += (sender, args) => { if (_request != null) _request.Cancel(); Source = null; };
        }
        private async void Reload()
        {
            if (!IsLoaded) return;
            if (_request != null) _request.Cancel();
            Source = null;
            var path = SourcePath;
            if (String.IsNullOrWhiteSpace(path)) return;
            var request = new CancellationTokenSource(); _request = request;
            try
            {
                await Slots.WaitAsync(request.Token);
                BitmapSource bitmap;
                try { bitmap = await Task.Run(() => Decode(path, 480), request.Token); }
                finally { Slots.Release(); }
                if (!request.IsCancellationRequested && IsLoaded && SourcePath == path) Source = bitmap;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AutoCAD.AI.Assistant.Services.DiagnosticsLog.Error("聊天预览", ex); }
            finally { if (_request == request) _request = null; request.Dispose(); }
        }
        internal static BitmapSource Decode(string path, int longestSide)
        {
            using (var stream = File.OpenRead(path))
            {
                var metadata = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var factor = Math.Min(1.0, (double)longestSide / Math.Max(metadata.PixelWidth, metadata.PixelHeight));
                var width = Math.Max(1, (int)(metadata.PixelWidth * factor));
                var height = Math.Max(1, (int)(metadata.PixelHeight * factor));
                stream.Position = 0;
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = width; bitmap.DecodePixelHeight = height;
                bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }
        }
    }
}
