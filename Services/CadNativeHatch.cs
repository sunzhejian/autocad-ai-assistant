using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class CadNativeHatch
    {
        internal static double PointDensity(Hatch hatch)
        {
            if (!String.Equals(hatch.PatternName, "DOTS", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(hatch.PatternName, "AR-SAND", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("pointSpacing目前只支持DOTS或AR-SAND纯点图案。");
            var density = 0.0;
            for (var i = 0; i < hatch.NumberOfPatternDefinitions; i++)
            {
                var definition = hatch.GetPatternDefinitionAt(i);
                var dashes = new List<double>();
                foreach (double dash in definition.GetDashes()) dashes.Add(dash);
                density += HatchMaterialPolicy.PointFamilyDensity(definition.Angle,
                    definition.OffsetX, definition.OffsetY, dashes);
            }
            if (density <= 0 || Double.IsInfinity(density) || Double.IsNaN(density))
                throw new InvalidOperationException("无法量测原生点图案密度。");
            return density;
        }
    }
}
