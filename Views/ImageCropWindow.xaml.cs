using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoCAD.AI.Assistant.Views
{
    public partial class ImageCropWindow : Window
    {
        private readonly string _sourcePath;
        private readonly BitmapImage _source;
        private readonly double _imageWidth;
        private readonly double _imageHeight;
        private double _scale = 1.0;
        private double _offsetX;
        private double _offsetY;
        private Point _start;
        private bool _dragging;
        private Int32Rect _selection;

        public string ResultPath { get; private set; }

        public ImageCropWindow(string imagePath)
        {
            InitializeComponent();
            _sourcePath = imagePath;
            _source = new BitmapImage();
            _source.BeginInit();
            _source.CacheOption = BitmapCacheOption.OnLoad;
            _source.UriSource = new Uri(imagePath);
            _source.EndInit();
            _source.Freeze();
            _imageWidth = _source.PixelWidth;
            _imageHeight = _source.PixelHeight;
            Preview.Source = _source;
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LayoutImage();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            LayoutImage();
        }

        private void LayoutImage()
        {
            var hostWidth = Host.ActualWidth;
            var hostHeight = Host.ActualHeight;
            if (hostWidth <= 0 || hostHeight <= 0 || _imageWidth <= 0 || _imageHeight <= 0) return;
            _scale = Math.Min(hostWidth / _imageWidth, hostHeight / _imageHeight);
            Preview.Width = _imageWidth * _scale;
            Preview.Height = _imageHeight * _scale;
            _offsetX = (hostWidth - Preview.Width) / 2;
            _offsetY = (hostHeight - Preview.Height) / 2;
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _dragging = true;
            _start = ClampToImage(e.GetPosition(Overlay));
            Canvas.SetLeft(Selection, _start.X);
            Canvas.SetTop(Selection, _start.Y);
            Selection.Width = 0;
            Selection.Height = 0;
            Selection.Visibility = Visibility.Visible;
            Overlay.CaptureMouse();
            e.Handled = true;
        }

        private Point ClampToImage(Point point)
        {
            return new Point(
                Math.Max(_offsetX, Math.Min(_offsetX + _imageWidth * _scale, point.X)),
                Math.Max(_offsetY, Math.Min(_offsetY + _imageHeight * _scale, point.Y)));
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var point = ClampToImage(e.GetPosition(Overlay));
            var x = Math.Min(_start.X, point.X);
            var y = Math.Min(_start.Y, point.Y);
            var width = Math.Abs(point.X - _start.X);
            var height = Math.Abs(point.Y - _start.Y);
            Canvas.SetLeft(Selection, x);
            Canvas.SetTop(Selection, y);
            Selection.Width = width;
            Selection.Height = height;
        }

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Overlay.ReleaseMouseCapture();
            CaptureSelection();
            if (_selection.IsEmpty)
            {
                Selection.Visibility = Visibility.Collapsed;
                Hint.Text = "框选太小，请重新拖动；不框选则使用整张图";
            }
            else
            {
                Hint.Text = "已选 " + _selection.Width + " × " + _selection.Height + " 像素，确定后仅复刻此区域";
            }
        }

        private void CaptureSelection()
        {
            var left = Canvas.GetLeft(Selection);
            var top = Canvas.GetTop(Selection);
            var width = Selection.Width;
            var height = Selection.Height;
            if (width < 8 || height < 8)
            {
                _selection = Int32Rect.Empty;
                return;
            }
            var x = (int)Math.Round((left - _offsetX) / _scale);
            var y = (int)Math.Round((top - _offsetY) / _scale);
            var w = (int)Math.Round(width / _scale);
            var h = (int)Math.Round(height / _scale);
            x = Math.Max(0, Math.Min(x, (int)_imageWidth - 1));
            y = Math.Max(0, Math.Min(y, (int)_imageHeight - 1));
            w = Math.Max(1, Math.Min(w, (int)_imageWidth - x));
            h = Math.Max(1, Math.Min(h, (int)_imageHeight - y));
            _selection = new Int32Rect(x, y, w, h);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _selection = Int32Rect.Empty;
            Selection.Visibility = Visibility.Collapsed;
            ResultPath = _sourcePath;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            ResultPath = null;
            DialogResult = false;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            CaptureSelection();
            if (_selection.Width == 0 || _selection.Height == 0)
            {
                ResultPath = _sourcePath;
                DialogResult = true;
                return;
            }
            try
            {
                var cropped = new CroppedBitmap(_source, _selection);
                var folder = Path.Combine(Path.GetTempPath(), "AutoCAD.AI.Assistant");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "crop_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                using (var stream = File.Create(file)) encoder.Save(stream);
                ResultPath = file;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Hint.Text = "裁剪失败：" + ex.Message;
            }
        }
    }
}
