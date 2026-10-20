using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PclDownloadPatcher;

internal static class VerifyPreservation
{
    // Compare symbolic IL. Metadata token numbers can change during serialization.
    private static string Body(MethodDefinition method)
    {
        if (!method.HasBody) return "NO_BODY";
        var builder = new StringBuilder();
        var code = method.Body.Instructions;
        builder.Append(method.Body.InitLocals).Append('|');
        foreach (var variable in method.Body.Variables) builder.Append(variable.VariableType.FullName).Append(';');
        foreach (var instruction in code)
        {
            builder.Append(instruction.OpCode.Name).Append(':');
            var operand = instruction.Operand;
            var branch = operand as Instruction;
            var branches = operand as Instruction[];
            var member = operand as MemberReference;
            var local = operand as VariableDefinition;
            var parameter = operand as ParameterDefinition;
            if (branch != null) builder.Append(code.IndexOf(branch));
            else if (branches != null) builder.Append(string.Join(",", branches.Select(b => code.IndexOf(b))));
            else if (member != null) builder.Append(member.FullName);
            else if (local != null) builder.Append(local.Index);
            else if (parameter != null) builder.Append(parameter.Index);
            else builder.Append(Convert.ToString(operand, CultureInfo.InvariantCulture));
            builder.Append('\n');
        }
        foreach (var handler in method.Body.ExceptionHandlers)
            builder.Append(handler.HandlerType).Append('|').Append(code.IndexOf(handler.TryStart)).Append('|')
                .Append(handler.TryEnd == null ? -1 : code.IndexOf(handler.TryEnd)).Append('|')
                .Append(code.IndexOf(handler.HandlerStart)).Append('|')
                .Append(handler.HandlerEnd == null ? -1 : code.IndexOf(handler.HandlerEnd)).Append('|')
                .Append(handler.FilterStart == null ? -1 : code.IndexOf(handler.FilterStart)).Append('|')
                .Append(handler.CatchType == null ? "" : handler.CatchType.FullName).Append('\n');
        return builder.ToString();
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static void Main(string[] args)
    {
        using (var original = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true }))
        using (var patched = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { InMemory = true }))
        using (var payload = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { InMemory = true }))
        {
            var before = MethodTransplant.AllTypes(original.MainModule).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            var after = MethodTransplant.AllTypes(patched.MainModule).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            var expected = MethodTransplant.AllTypes(payload.MainModule).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            Check(before.Count == after.Count, "Methods were added or removed.");
            int unchanged = 0;
            foreach (var pair in before)
            {
                Check(after.ContainsKey(pair.Key), "Method disappeared.");
                if (PatchProfile.Methods.Contains(pair.Key))
                {
                    Check(Body(after[pair.Key]) == Body(expected[pair.Key]), "Patched method differs from reviewed payload: " + pair.Key);
                    Check(Body(pair.Value) != Body(after[pair.Key]), "Patch had no effect: " + pair.Key);
                }
                else { Check(Body(pair.Value) == Body(after[pair.Key]), "Unrelated method changed: " + pair.Key); unchanged++; }
            }
            var beforeFields = MethodTransplant.AllTypes(original.MainModule).SelectMany(t => t.Fields).ToDictionary(f => f.FullName);
            var afterFields = MethodTransplant.AllTypes(patched.MainModule).SelectMany(t => t.Fields).ToDictionary(f => f.FullName);
            Check(beforeFields.Count == afterFields.Count, "Field definitions changed.");
            foreach (var pair in beforeFields)
            {
                if (pair.Value.DeclaringType.FullName == "PCL.ModSecret" && pair.Value.Name == "RestrictMcDownload")
                    Check(object.Equals(afterFields[pair.Key].Constant, false), "Download flag was not cleared.");
                else
                {
                    Check(pair.Value.HasConstant == afterFields[pair.Key].HasConstant, "Unrelated constant changed.");
                    if (pair.Value.HasConstant) Check(object.Equals(pair.Value.Constant, afterFields[pair.Key].Constant), "Unrelated constant value changed.");
                    Check(pair.Value.InitialValue.SequenceEqual(afterFields[pair.Key].InitialValue), "Field initializer changed.");
                }
            }
            var beforeResources = original.MainModule.Resources.OfType<EmbeddedResource>().ToDictionary(r => r.Name);
            var afterResources = patched.MainModule.Resources.OfType<EmbeddedResource>().ToDictionary(r => r.Name);
            Check(beforeResources.Count == afterResources.Count, "Resource list changed.");
            foreach (var pair in beforeResources)
                Check(pair.Value.GetResourceData().SequenceEqual(afterResources[pair.Key].GetResourceData()), "Embedded resource changed: " + pair.Key);
            Check(!patched.MainModule.AssemblyReferences.Any(r => r.Name == "PCL.DownloadPatch.Methods" || r.Name == "Mono.Cecil"), "Patched PCL acquired a patcher runtime dependency.");
            Check(original.MainModule.Mvid == patched.MainModule.Mvid, "Module identity changed.");
            Console.WriteLine("PASS: exactly five download methods and one flag changed; " + unchanged + " other methods, field data and all " + beforeResources.Count + " embedded resources preserved.");
        }
    }
}
