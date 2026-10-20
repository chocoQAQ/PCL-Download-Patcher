using System;
using System.IO;
using PclDownloadPatcher;

internal static class BuildCandidate
{
    private static void Main(string[] args)
    {
        try
        {
            using (var payload = File.OpenRead(args[1]))
            {
                var bytes = PatchEngine.Transform(File.ReadAllBytes(args[0]), payload);
                File.WriteAllBytes(args[2], bytes);
                Console.WriteLine("Candidate SHA256: " + PatchEngine.Hash(bytes));
            }
        }
        catch (Exception ex) { Console.WriteLine(ex.GetType().FullName); Console.WriteLine(ex.Message); Console.WriteLine(ex.StackTrace); Environment.Exit(1); }
    }
}
