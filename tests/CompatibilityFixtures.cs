using System;
using System.IO;
using System.Linq;
using System.Resources;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PclDownloadPatcher;

internal static class CompatibilityFixtures
{
    private static void Main(string[] args)
    {
        using (var resolver = new EmbeddedDependencyResolver())
        using (var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
        {
            resolver.Target = assembly;
            var module = assembly.MainModule;
            if (args[2] == "variant")
            {
                // Simulate someone's unrelated customization without executing it.
                var method = module.GetType("PCL.ModSecret").Methods.First(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr));
                method.Body.Instructions.First(i => i.OpCode.Code == Code.Ldstr).Operand = "Patcher preservation fixture";
                module.Resources.Add(new EmbeddedResource("Custom.Theme.Test", ManifestResourceAttributes.Private, new byte[] { 7, 3, 9, 1 }));
                // A resource that must never be deserialized during dependency lookup.
                using (var memory = new MemoryStream())
                {
                    using (var writer = new ResourceWriter(memory)) { writer.AddResource("UnusedCustomData", new byte[] { 1, 2, 3 }); writer.Generate(); module.Resources.Add(new EmbeddedResource("Custom.Test.resources", ManifestResourceAttributes.Private, memory.ToArray())); }
                }
            }
            else if (args[2] == "incompatible")
            {
                var loader = module.GetType("PCL.PageDownloadInstall").Methods.Single(m => m.Name == "LoaderInit");
                loader.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
            }
            else if (args[2] == "legacy-enabled") assembly.Name.Version = new Version(2, 12, 8, 2);
            else throw new ArgumentException("Unknown fixture mode.");
            assembly.Write(args[1], new WriterParameters { Timestamp = 0 });
        }
    }
}
