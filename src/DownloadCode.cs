using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PclDownloadPatcher
{
    internal sealed class DownloadAnalysis
    {
        internal bool CanPatch;
        internal bool EntryEnabled;
        internal string Version = "未知";
        internal string Reason = "无法识别兼容的 PCL 下载代码。";
    }

    internal static class DownloadCode
    {
        internal static string Signature(MethodDefinition method)
        {
            if (!method.HasBody) return "NO_BODY";
            var text = new StringBuilder();
            var code = method.Body.Instructions;
            text.Append(method.Body.InitLocals).Append('|');
            foreach (var variable in method.Body.Variables) text.Append(variable.VariableType.FullName).Append(';');
            foreach (var instruction in code)
            {
                text.Append(instruction.OpCode.Name).Append(':');
                var operand = instruction.Operand;
                var branch = operand as Instruction;
                var branches = operand as Instruction[];
                var member = operand as MemberReference;
                var local = operand as VariableDefinition;
                var parameter = operand as ParameterDefinition;
                if (branch != null) text.Append(code.IndexOf(branch));
                else if (branches != null) text.Append(string.Join(",", branches.Select(b => code.IndexOf(b))));
                else if (member != null) text.Append(member.FullName);
                else if (local != null) text.Append(local.Index);
                else if (parameter != null) text.Append(parameter.Index);
                else text.Append(Convert.ToString(operand, CultureInfo.InvariantCulture));
                text.Append('\n');
            }
            foreach (var handler in method.Body.ExceptionHandlers)
                text.Append(handler.HandlerType).Append('|').Append(code.IndexOf(handler.TryStart)).Append('|')
                    .Append(handler.TryEnd == null ? -1 : code.IndexOf(handler.TryEnd)).Append('|')
                    .Append(code.IndexOf(handler.HandlerStart)).Append('|')
                    .Append(handler.HandlerEnd == null ? -1 : code.IndexOf(handler.HandlerEnd)).Append('|')
                    .Append(handler.FilterStart == null ? -1 : code.IndexOf(handler.FilterStart)).Append('|')
                    .Append(handler.CatchType == null ? "" : handler.CatchType.FullName).Append('\n');
            return PatchEngine.Hash(Encoding.UTF8.GetBytes(text.ToString()));
        }

        internal static DownloadAnalysis Analyze(byte[] image)
        {
            try
            {
                using (var stream = new MemoryStream(image, false))
                using (var assembly = AssemblyDefinition.ReadAssembly(stream)) return Analyze(assembly.MainModule);
            }
            catch (BadImageFormatException) { return new DownloadAnalysis { Reason = "此文件不是可识别的 PCL .NET 程序。" }; }
            catch (Exception) { return new DownloadAnalysis { Reason = "读取下载代码失败，文件可能经过加壳或结构不兼容。" }; }
        }

        internal static DownloadAnalysis Analyze(ModuleDefinition module)
        {
            var analysis = new DownloadAnalysis { Version = module.Assembly.Name.Version.ToString() };
            if (module.Assembly.Name.Name != "Plain Craft Launcher 2") return analysis;
            var install = module.GetType("PCL.PageDownloadInstall");
            var loader = install == null ? null : install.Methods.SingleOrDefault(m => m.Name == "LoaderInit" && !m.HasParameters);
            var secret = module.GetType("PCL.ModSecret");
            var flag = secret == null ? null : secret.Fields.SingleOrDefault(f => f.Name == "RestrictMcDownload");
            bool restricted = flag != null && flag.IsLiteral && object.Equals(flag.Constant, true);
            var selected = new Dictionary<string, MethodDefinition>();
            foreach (var type in MethodTransplant.AllTypes(module))
                foreach (var method in type.Methods)
                    if (PatchProfile.Methods.Contains(method.FullName)) selected[method.FullName] = method;
            bool hasRestrictionMessage = selected.Values.Any(m => m.HasBody && m.Body.Instructions.Any(i =>
                i.OpCode.Code == Code.Ldstr && ((string)i.Operand).Contains("暂时不再提供 MC 下载")));
            if (loader != null && loader.HasBody && !restricted && !hasRestrictionMessage)
            {
                bool normalList = loader.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldsfld &&
                    i.Operand is FieldReference && ((FieldReference)i.Operand).DeclaringType.FullName == "PCL.ModDownload" && ((FieldReference)i.Operand).Name == "DlClientListLoader");
                bool initialized = loader.Body.Instructions.Any(i => i.Operand is MethodReference &&
                    ((MethodReference)i.Operand).DeclaringType.FullName == "PCL.MyPageRight" && ((MethodReference)i.Operand).Name == "PageLoaderInit");
                if (normalList && initialized)
                {
                    analysis.EntryEnabled = true;
                    analysis.Reason = "下载入口已启用，未发现本补丁针对的关闭下载限制；无需修复。联网下载尚未验证。";
                    return analysis;
                }
            }
            if (analysis.Version != PatchProfile.Version)
            {
                analysis.Reason = "检测到 PCL " + analysis.Version + "，未匹配已验证的下载修复规则。";
                return analysis;
            }
            if (module.Assembly.Name.HasPublicKey || module.Assembly.Modules.Count != 1 || (module.Attributes & ModuleAttributes.ILOnly) == 0)
            {
                analysis.Reason = "此构建的签名或模块结构不支持定向修复。";
                return analysis;
            }
            if (!restricted || selected.Count != PatchProfile.Methods.Length)
            {
                analysis.Reason = "下载相关结构与已验证规则不一致，未进行修改。";
                return analysis;
            }
            for (int index = 0; index < PatchProfile.Methods.Length; index++)
                if (Signature(selected[PatchProfile.Methods[index]]) != PatchProfile.OriginalMethodSignatures[index])
                {
                    analysis.Reason = "下载方法本身已改变，无法自动确定补丁位置；文件未被修改。";
                    return analysis;
                }
            analysis.CanPatch = true;
            analysis.Reason = "下载限制结构匹配，可定向修复并保留其他改动。";
            return analysis;
        }

        internal static void VerifyPreserved(ModuleDefinition before, ModuleDefinition after, ModuleDefinition payload)
        {
            var originalMethods = MethodTransplant.AllTypes(before).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            var patchedMethods = MethodTransplant.AllTypes(after).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            var expected = MethodTransplant.AllTypes(payload).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            if (originalMethods.Count != patchedMethods.Count) throw new InvalidOperationException("输出中的方法列表发生变化，已停止替换。");
            foreach (var pair in originalMethods)
            {
                MethodDefinition changed;
                if (!patchedMethods.TryGetValue(pair.Key, out changed)) throw new InvalidOperationException("输出中缺少原有方法，已停止替换。");
                var expectedMethod = PatchProfile.Methods.Contains(pair.Key) ? expected[pair.Key] : pair.Value;
                if (Signature(changed) != Signature(expectedMethod)) throw new InvalidOperationException("输出方法与预期不符，已停止替换：" + pair.Key);
            }
            var originalFields = MethodTransplant.AllTypes(before).SelectMany(t => t.Fields).ToDictionary(f => f.FullName);
            var patchedFields = MethodTransplant.AllTypes(after).SelectMany(t => t.Fields).ToDictionary(f => f.FullName);
            if (originalFields.Count != patchedFields.Count) throw new InvalidOperationException("输出中的字段列表发生变化，已停止替换。");
            foreach (var pair in originalFields)
            {
                FieldDefinition changed;
                if (!patchedFields.TryGetValue(pair.Key, out changed)) throw new InvalidOperationException("输出中缺少原有字段，已停止替换。");
                if (pair.Value.DeclaringType.FullName == "PCL.ModSecret" && pair.Value.Name == "RestrictMcDownload")
                {
                    if (!object.Equals(changed.Constant, false)) throw new InvalidOperationException("下载标志未被正确修复。");
                }
                else if (pair.Value.HasConstant != changed.HasConstant || (pair.Value.HasConstant && !object.Equals(pair.Value.Constant, changed.Constant)) || !pair.Value.InitialValue.SequenceEqual(changed.InitialValue))
                    throw new InvalidOperationException("非下载字段发生变化，已停止替换。");
            }
            var originalResources = before.Resources.OfType<EmbeddedResource>().ToDictionary(r => r.Name);
            var patchedResources = after.Resources.OfType<EmbeddedResource>().ToDictionary(r => r.Name);
            if (before.Resources.Count != after.Resources.Count || originalResources.Count != patchedResources.Count) throw new InvalidOperationException("输出资源列表发生变化，已停止替换。");
            foreach (var pair in originalResources)
                if (!patchedResources.ContainsKey(pair.Key) || !pair.Value.GetResourceData().SequenceEqual(patchedResources[pair.Key].GetResourceData())) throw new InvalidOperationException("非下载资源发生变化，已停止替换。");
            if (after.AssemblyReferences.Any(r => (r.Name == "Mono.Cecil" || r.Name == "PCL.DownloadPatch.Methods") && !before.AssemblyReferences.Any(b => b.FullName == r.FullName))) throw new InvalidOperationException("输出包含意外的运行时依赖。");
        }
    }
}
