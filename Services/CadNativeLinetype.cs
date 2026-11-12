using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class CadNativeLinetype
    {
        internal static double LongestBaseDash(Transaction transaction, Entity entity, out string name)
        {
            var record = (LinetypeTableRecord)transaction.GetObject(entity.LinetypeId, OpenMode.ForRead);
            if (record.Name.Equals("BYLAYER", StringComparison.OrdinalIgnoreCase))
            {
                var layer = (LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead);
                record = (LinetypeTableRecord)transaction.GetObject(layer.LinetypeObjectId, OpenMode.ForRead);
            }
            name = record.Name;
            if (name.Equals("BYBLOCK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("dashLength不能在尚未插入的BYBLOCK线型上猜测，请指定实际图层线型。");
            var longest = 0.0;
            for (var i = 0; i < record.NumDashes; i++) longest = Math.Max(longest, record.DashLengthAt(i));
            return longest;
        }

        internal static void Apply(Transaction transaction, Entity entity, Dictionary<string, object> command)
        {
            if (!command.ContainsKey("dashLength")) return;
            string name;
            var longest = LongestBaseDash(transaction, entity, out name);
            var global = entity.Database.Ltscale;
            if (longest <= 1e-12 || global <= 0 || Double.IsNaN(longest) || Double.IsInfinity(longest))
                throw new InvalidOperationException("dashLength需要有非零实段的真实线型，不能把连续线/纯点线当作中心线。");
            var scale = CadPlanQuality.Number(command["dashLength"]) / longest / global;
            if (Double.IsNaN(scale) || Double.IsInfinity(scale) || scale <= 0)
                throw new InvalidOperationException("dashLength无法得到有限的实体线型比例。");
            entity.LinetypeScale = scale;
        }
    }
}
