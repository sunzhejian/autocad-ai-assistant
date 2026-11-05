using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>发送前增强图纸可读性，不修改用户原图。</summary>
    internal static class ImagePreprocessor
    {
        private const int VisionLimit = 1600;

        internal sealed class VisionImage
        {
            public string Label { get; set; }
            public string DataUrl { get; set; }
        }

        public static List<VisionImage> PrepareViews(string path, System.Threading.CancellationToken cancellation, bool includeDetails = true)
        {
            cancellation.ThrowIfCancellationRequested();
            var images = new List<VisionImage>();
            using (var source = new Bitmap(path))
            using (var cropped = CropUniformBorders(source))
            {
                using (var overview = Resize(cropped, false, VisionLimit))
                {
                    EnhanceGrayLevels(overview);
                    images.Add(new VisionImage { Label = "整图：用于判断单位、比例和可见视图位置。", DataUrl = EncodeBest(overview) });
                }
                // Overview and overlapping details share one request; small screenshots remain single-image.
                if (includeDetails && Math.Max(cropped.Width, cropped.Height) > 2000)
                {
                    var width = (int)Math.Ceiling(cropped.Width * 0.56);
                    var height = (int)Math.Ceiling(cropped.Height * 0.56);
                    for (var row = 0; row < 2; row++)
                    for (var col = 0; col < 2; col++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var area = new Rectangle(col == 0 ? 0 : cropped.Width - width, row == 0 ? 0 : cropped.Height - height, width, height);
                        using (var tile = cropped.Clone(area, PixelFormat.Format24bppRgb))
                        using (var detail = Resize(tile, false, 1280))
                        {
                            EnhanceGrayLevels(detail);
                            images.Add(new VisionImage { Label = "局部高清 " + (row + 1) + "," + (col + 1)
                                + "（与邻块重叠；属同一张图，不重复绘制）。", DataUrl = EncodeBest(detail) });
                        }
                    }
                }
                DiagnosticsLog.Write("多尺度图像 原图=" + source.Width + "x" + source.Height + " 裁边="
                    + cropped.Width + "x" + cropped.Height + " 图片数=" + images.Count);
            }
            return images;
        }

        private static string EncodeBest(Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                var jpeg = EncodeJpeg(bitmap, 90L);
                var lossless = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
                return lossless.Length <= jpeg.Length * 1.25 ? lossless : jpeg;
            }
        }

        public static string EncodeForVision(string path)
        {
            try
            {
                using (var source = new Bitmap(path))
                using (var cropped = CropUniformBorders(source))
                using (var enhanced = ResizeAndEnhance(cropped))
                {
                    DiagnosticsLog.Write("图像预处理 原图=" + source.Width + "x" + source.Height
                        + " 裁边=" + cropped.Width + "x" + cropped.Height
                        + " 输出=" + enhanced.Width + "x" + enhanced.Height);
                    return EncodeJpeg(enhanced, 82L);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("图像预处理", ex);
                using (var source = new Bitmap(path))
                using (var resized = Resize(source, false))
                {
                    return EncodeJpeg(resized, 78L);
                }
            }
        }

        private static Bitmap CropUniformBorders(Bitmap source)
        {
            var bounds = FindContentBounds(source);
            if (bounds.Width * bounds.Height < source.Width * source.Height * 0.05)
            {
                bounds = new Rectangle(0, 0, source.Width, source.Height);
            }

            var padding = Math.Max(4, Math.Min(source.Width, source.Height) / 200);
            var area = Rectangle.Intersect(
                new Rectangle(0, 0, source.Width, source.Height),
                Rectangle.Inflate(bounds, padding, padding));

            // 外扩区统一填白，避免把黑底重新带回边缘。
            var result = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.Clear(Color.White);
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source,
                    new Rectangle(bounds.X - area.X, bounds.Y - area.Y, bounds.Width, bounds.Height),
                    bounds, GraphicsUnit.Pixel);
            }
            return result;
        }

        /// <summary>
        /// 全分辨率求内容包围盒：以四角底色为背景，取与背景差异明显的像素范围。
        /// 白纸扫描件可去掉白边，黑底照片可去掉黑框，且不会因稀疏线条误裁图形。
        /// </summary>
        private static Rectangle FindContentBounds(Bitmap image)
        {
            var data = image.LockBits(
                new Rectangle(0, 0, image.Width, image.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * image.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

                var background = (CornerLuminance(bytes, stride, 0, 0)
                    + CornerLuminance(bytes, stride, image.Width - 1, 0)
                    + CornerLuminance(bytes, stride, 0, image.Height - 1)
                    + CornerLuminance(bytes, stride, image.Width - 1, image.Height - 1)) / 4;

                var left = image.Width;
                var top = image.Height;
                var right = -1;
                var bottom = -1;
                for (var y = 0; y < image.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < image.Width; x++)
                    {
                        var index = row + x * 3;
                        var luminance = (bytes[index + 2] * 77 + bytes[index + 1] * 150 + bytes[index] * 29) >> 8;
                        if (Math.Abs(luminance - background) <= 24) continue;
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
                if (right < 0) return new Rectangle(0, 0, image.Width, image.Height);
                return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        private static int CornerLuminance(byte[] bytes, int stride, int x, int y)
        {
            var index = y * stride + x * 3;
            return (bytes[index + 2] * 77 + bytes[index + 1] * 150 + bytes[index] * 29) >> 8;
        }

        private static Bitmap ResizeAndEnhance(Bitmap source)
        {
            var bitmap = Resize(source, false, VisionLimit);
            EnhanceGrayLevels(bitmap);
            return bitmap;
        }

        private static Bitmap Resize(Bitmap source, bool allowUpscale)
        {
            return Resize(source, allowUpscale, VisionLimit);
        }

        private static Bitmap Resize(Bitmap source, bool allowUpscale, int limit)
        {
            var maximum = Math.Max(source.Width, source.Height);
            var scale = Math.Min(1.0, (double)limit / maximum);
            if (allowUpscale && maximum < limit) scale = Math.Min(2.0, (double)limit / maximum);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, 0, 0, width, height);
            }
            return bitmap;
        }

        /// <summary>拉伸灰度层次但保留抗锯齿，避免硬二值化丢失细线和小字。</summary>
        private static void EnhanceGrayLevels(Bitmap bitmap)
        {
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * bitmap.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var histogram = new int[256];
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var index = row + x * 3;
                        histogram[(bytes[index + 2] * 77 + bytes[index + 1] * 150 + bytes[index] * 29) >> 8]++;
                    }
                }

                var low = Percentile(histogram, bitmap.Width * bitmap.Height, 0.005);
                var high = Percentile(histogram, bitmap.Width * bitmap.Height, 0.995);
                if (low > 180 || high < 70 || high - low < 60) return;
                var lookup = new byte[256];
                for (var value = 0; value < lookup.Length; value++)
                {
                    lookup[value] = (byte)Math.Max(0, Math.Min(255,
                        (int)Math.Round((value - low) * 255.0 / (high - low))));
                }
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var index = row + x * 3;
                        var gray = lookup[(bytes[index + 2] * 77 + bytes[index + 1] * 150 + bytes[index] * 29) >> 8];
                        bytes[index] = gray;
                        bytes[index + 1] = gray;
                        bytes[index + 2] = gray;
                    }
                }
                Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static int Percentile(int[] histogram, int count, double fraction)
        {
            var target = (int)(count * fraction);
            var cumulative = 0;
            for (var value = 0; value < histogram.Length; value++)
            {
                cumulative += histogram[value];
                if (cumulative >= target) return value;
            }
            return 255;
        }

        private static string EncodeJpeg(Bitmap bitmap, long quality)
        {
            ImageCodecInfo codec = null;
            foreach (var info in ImageCodecInfo.GetImageEncoders())
            {
                if (info.MimeType == "image/jpeg") codec = info;
            }
            using (var stream = new MemoryStream())
            {
                if (codec == null)
                {
                    bitmap.Save(stream, ImageFormat.Jpeg);
                }
                else
                {
                    using (var parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                        bitmap.Save(stream, codec, parameters);
                    }
                }
                return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
            }
        }
    }
}
