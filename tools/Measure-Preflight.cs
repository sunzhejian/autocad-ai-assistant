using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

// Read-only validator benchmark: does not load AutoCAD or execute any plan.
internal static class MeasurePreflight
{
    private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("plugin.dll plan-directory report.json"); return 2; }
        var pluginBytes = File.ReadAllBytes(args[0]);
        var assembly = Assembly.Load(pluginBytes);
        var method = assembly.GetType("AutoCAD.AI.Assistant.Services.CadPlanValidator", true).GetMethod("Validate");
        var records = new List<object>();
        foreach (var path in Directory.GetFiles(args[1], "*.json"))
        {
            var bytes = File.ReadAllBytes(path);
            var plan = Encoding.UTF8.GetString(bytes).TrimStart('\ufeff');
            method.Invoke(null, new object[] { plan }); // Warm JIT separately.
            var samples = new List<double>();
            string error = null;
            for (var i = 0; i < 5; i++)
            {
                var timer = Stopwatch.StartNew();
                error = method.Invoke(null, new object[] { plan }) as string;
                samples.Add(timer.Elapsed.TotalMilliseconds);
            }
            records.Add(new { plan = Path.GetFileName(path), bytes = bytes.Length, plan_sha256 = Hash(bytes),
                valid = error == null, error = error, milliseconds = samples });
        }
        File.WriteAllText(args[2], new JavaScriptSerializer().Serialize(new {
            version = assembly.GetName().Version.ToString(), plugin_sha256 = Hash(pluginBytes), samples = records,
            scope = "Pure preflight only. Not mouse latency, provider latency or native execution."
        }));
        return 0;
    }

    private static string Hash(byte[] bytes)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
