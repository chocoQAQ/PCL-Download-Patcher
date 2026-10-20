using System;
using System.IO;
using System.Linq;
using System.Resources;
using Mono.Cecil;

internal static class ExtractTestDependencies
{
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(args[1]);
        using (var assembly = AssemblyDefinition.ReadAssembly(args[0]))
        using (var stream = assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "PCL.Resources.resources").GetResourceStream())
        using (var resources = new ResourceReader(stream))
        {
            foreach (var name in new[] { "PCLCS", "MeloongCore", "MeloongCore.Wpf", "Newtonsoft.Json" })
            {
                string type;
                byte[] raw;
                resources.GetResourceData(name, out type, out raw);
                if (type != "ResourceTypeCode.ByteArray") throw new Exception("Unexpected resource type.");
                using (var reader = new BinaryReader(new MemoryStream(raw, false)))
                {
                    int length = reader.ReadInt32();
                    if (length != raw.Length - 4) throw new Exception("Unexpected resource length.");
                    File.WriteAllBytes(Path.Combine(args[1], name + ".dll"), reader.ReadBytes(length));
                }
            }
        }
    }
}
