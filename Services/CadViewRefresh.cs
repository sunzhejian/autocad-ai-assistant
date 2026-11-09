using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class CadViewRefresh
    {
        public static bool Refresh(Document document, Autodesk.AutoCAD.DatabaseServices.Extents3d? newBounds, bool regenerate, bool frameSmall = false)
        {
            if (regenerate) document.Editor.Regen();
            else document.Editor.UpdateScreen();
            if (!newBounds.HasValue || !document.Database.TileMode) return false;
            using (var view = document.Editor.GetCurrentView())
            {
                if (view.PerspectiveEnabled || view.Height <= 0 || view.Width <= 0) return false;
                var worldFromView = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
                    * Matrix3d.Displacement(view.Target - Point3d.Origin) * Matrix3d.PlaneToWorld(view.ViewDirection);
                var box = newBounds.Value; box.TransformBy(worldFromView.Inverse());
                var min = box.MinPoint; var max = box.MaxPoint;
                if (!Finite(min.X) || !Finite(min.Y) || !Finite(max.X) || !Finite(max.Y)) return false;
                var inside = min.X >= view.CenterPoint.X - view.Width * .48 && max.X <= view.CenterPoint.X + view.Width * .48
                    && min.Y >= view.CenterPoint.Y - view.Height * .48 && max.Y <= view.CenterPoint.Y + view.Height * .48;
                var occupancy = Math.Max((max.X - min.X) / view.Width, (max.Y - min.Y) / view.Height);
                var tinyFirstDrawing = frameSmall && occupancy > 1e-9 && occupancy < .25;
                if (inside && !tinyFirstDrawing) return false;
                var aspect = view.Width / view.Height;
                view.Height = Math.Max(.001, Math.Max(max.Y - min.Y, (max.X - min.X) / aspect) * 1.15);
                view.Width = view.Height * aspect;
                view.CenterPoint = new Point2d((min.X + max.X) / 2, (min.Y + max.Y) / 2);
                document.Editor.SetCurrentView(view);
                return true;
            }
        }
        private static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
    }
}
