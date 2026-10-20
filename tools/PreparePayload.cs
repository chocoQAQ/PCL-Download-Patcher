using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using PclDownloadPatcher;

internal static class PreparePayload
{
    private static void Main(string[] args)
    {
        using (var donor = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true }))
        using (var payload = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("PCL.DownloadPatch.Methods", new Version(1, 0)), "DownloadMethods", ModuleKind.Dll))
        {
            var sourceMethods = MethodTransplant.AllTypes(donor.MainModule).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            var selected = new List<MethodDefinition>();
            foreach (var name in PatchProfile.Methods)
            {
                MethodDefinition source;
                if (!sourceMethods.TryGetValue(name, out source)) throw new Exception("Missing donor method: " + name);
                var type = payload.MainModule.Types.FirstOrDefault(t => t.FullName == source.DeclaringType.FullName);
                if (type == null)
                {
                    type = new TypeDefinition(source.DeclaringType.Namespace, source.DeclaringType.Name, TypeAttributes.Public | TypeAttributes.Class, payload.MainModule.TypeSystem.Object);
                    payload.MainModule.Types.Add(type);
                }
                var method = new MethodDefinition(source.Name, source.Attributes, payload.MainModule.ImportReference(source.ReturnType));
                foreach (var parameter in source.Parameters)
                    method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, payload.MainModule.ImportReference(parameter.ParameterType)));
                type.Methods.Add(method);
                selected.Add(method);
            }
            var transplant = new MethodTransplant(payload.MainModule);
            foreach (var method in selected) transplant.Copy(sourceMethods[method.FullName], method);
            payload.MainModule.Mvid = new Guid("28f887ed-f880-4bdc-b7d3-5bfec4b59240");
            payload.Write(args[1], new WriterParameters { Timestamp = 0 });
            Console.WriteLine("Prepared " + selected.Count + " download methods (" + new FileInfo(args[1]).Length + " bytes).");
        }
    }
}
