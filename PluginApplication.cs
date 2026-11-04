using System;
using System.Drawing;
using System.Windows.Forms.Integration;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AutoCAD.AI.Assistant.Views;

[assembly: CommandClass(typeof(AutoCAD.AI.Assistant.PluginApplication))]

namespace AutoCAD.AI.Assistant
{
    public sealed class PluginApplication
    {
        private static PaletteSet _palette;
        private static AiPanel _panel;

        [CommandMethod("AIPANEL", CommandFlags.Session)]
        public void ShowAiPanel()
        {
            if (_palette == null)
            {
                _panel = new AiPanel();
                var host = new ElementHost
                {
                    Child = _panel,
                    Dock = System.Windows.Forms.DockStyle.Fill
                };

                _palette = new PaletteSet("CAD AI", new Guid("AF50251F-E562-4D21-933C-963738B5C775"))
                {
                    Size = new Size(450, 720),
                    MinimumSize = new Size(360, 480)
                };
                _palette.Add("AI 助手", host);
            }

            _palette.Visible = true;
            _palette.Activate(0);
        }
    }
}
