using System;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;

namespace AutoCAD.AI.Assistant.Services
{
    /// <summary>Pure-data preflight, safe to run off the CAD thread. One execution only.</summary>
    internal sealed class PreparedCadPlan
    {
        public string Json { get; private set; }
        public Dictionary<string, double> FinalModelDimensionSizes { get; private set; }
        private Dictionary<string, object> _root;
        private int _claimed;
        private PreparedCadPlan() { }
        public static PreparedCadPlan Create(string json)
        {
            var error = CadPlanValidator.Validate(json);
            if (error != null) throw new InvalidOperationException("计划校验失败：" + error);
            bool modelDimensionLengths;
            json = CadPlanNormalizer.Normalize(json, out modelDimensionLengths);
            error = CadPlanValidator.Validate(json);
            if (error != null) throw new InvalidOperationException("单位换算后校验失败：" + error);
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json);
            var sizes = CadDimensionSizing.Prepare((object[])root["commands"], modelDimensionLengths);
            return new PreparedCadPlan { Json = json, _root = root, FinalModelDimensionSizes = sizes };
        }
        public Dictionary<string, object> Claim()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("该预检计划已经执行，请重新预检。");
            var root = _root; _root = null; return root;
        }
    }
}
