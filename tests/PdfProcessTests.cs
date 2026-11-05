using System;
using System.IO;
using System.Threading;
using AutoCAD.AI.Assistant.Services;

internal static class PdfProcessTests
{
    private static int Main(string[] args)
    {
        try
        {
            var source = PdfSourceService.LoadAsync(args[0], Int32.Parse(args[1]), CancellationToken.None).GetAwaiter().GetResult();
            if (source.kind != args[2] || !File.Exists(source.preview) || source.pages < source.page) throw new Exception("PDF process result mismatch");
            if (source.CanImport) source.VerifySnapshot();
            source.VerifyPreview();
            Console.WriteLine("PASS local PDF process: " + source.Description);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
