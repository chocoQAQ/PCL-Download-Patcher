using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using Mono.Cecil;

namespace PclDownloadPatcher
{
    // PCL embeds its dependencies. Read their metadata in memory; never load or
    // run launcher code and never extract account credentials or dependencies.
    internal sealed class EmbeddedDependencyResolver : IAssemblyResolver
    {
        internal AssemblyDefinition Target;
        private readonly DefaultAssemblyResolver fallback = new DefaultAssemblyResolver();
        private readonly Dictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>();

        public AssemblyDefinition Resolve(AssemblyNameReference name) { return Resolve(name, new ReaderParameters()); }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (Target != null && name.FullName == Target.Name.FullName) return Target;
            AssemblyDefinition existing;
            if (cache.TryGetValue(name.FullName, out existing)) return existing;
            if (Target != null)
            {
                foreach (var resource in Target.MainModule.Resources.OfType<EmbeddedResource>().Where(r => r.Name.EndsWith(".resources", StringComparison.Ordinal)))
                using (var stream = resource.GetResourceStream())
                using (var reader = new ResourceReader(stream))
                {
                    var enumerator = reader.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        if (!string.Equals(enumerator.Key as string, name.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        // GetResourceData does not deserialize arbitrary objects in a
                        // modified launcher's resources. Only accept a raw byte array.
                        string resourceType;
                        byte[] resourceData;
                        reader.GetResourceData((string)enumerator.Key, out resourceType, out resourceData);
                        if (resourceType != "ResourceTypeCode.ByteArray" || resourceData.Length < 4) continue;
                        byte[] bytes;
                        using (var raw = new BinaryReader(new MemoryStream(resourceData, false)))
                        {
                            var length = raw.ReadInt32();
                            if (length < 0 || length != resourceData.Length - 4) continue;
                            bytes = raw.ReadBytes(length);
                        }
                        using (var image = new MemoryStream(bytes, false))
                            existing = AssemblyDefinition.ReadAssembly(image, new ReaderParameters { InMemory = true, AssemblyResolver = this });
                        if (existing.Name.FullName != name.FullName) { existing.Dispose(); continue; }
                        cache.Add(name.FullName, existing);
                        return existing;
                    }
                }
            }
            return fallback.Resolve(name, parameters);
        }

        public void Dispose()
        {
            foreach (var assembly in cache.Values) assembly.Dispose();
            fallback.Dispose();
        }
    }
}
