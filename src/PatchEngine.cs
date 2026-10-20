using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Mono.Cecil;

namespace PclDownloadPatcher
{
    internal enum LauncherState { Original, Patched, DownloadsEnabled, Unsupported, Missing }

    internal sealed class Inspection
    {
        internal string Path;
        internal string Hash;
        internal string BackupPath;
        internal LauncherState State;
        internal bool BackupExists;
        internal bool BackupValid;
        internal string Version;
        internal string Reason;
        internal string BackupHash;
        internal string ExpectedPatchedHash;
        internal string RecordPath;
        internal bool RecordExists;
        internal bool RecordValid;
    }

    internal sealed class OperationResult
    {
        internal Inspection Inspection;
        internal string Message;
        internal bool Changed;
    }

    internal static class PatchEngine
    {
        internal static string Hash(byte[] bytes)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static string FileHash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请先选择 PCL 的 EXE 文件。");
            var full = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
            if (!string.Equals(System.IO.Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择启动器的 .exe 文件。");
            return full;
        }

        internal static Inspection Inspect(string path)
        {
            path = NormalizePath(path);
            var result = new Inspection { Path = path, BackupPath = path + PatchProfile.BackupSuffix,
                RecordPath = path + PatchProfile.RecordSuffix, State = LauncherState.Missing, Hash = "", Version = "未知", Reason = "未找到启动器文件。" };
            if (File.Exists(path))
            {
                var image = File.ReadAllBytes(path);
                result.Hash = Hash(image);
                var analysis = DownloadCode.Analyze(image);
                result.Version = analysis.Version;
                result.Reason = analysis.Reason;
                result.State = result.Hash == PatchProfile.PatchedSha256 ? LauncherState.Patched :
                    analysis.CanPatch ? LauncherState.Original : analysis.EntryEnabled ? LauncherState.DownloadsEnabled : LauncherState.Unsupported;
            }
            string recordedOriginal = null;
            result.RecordExists = File.Exists(result.RecordPath);
            if (result.RecordExists)
            {
                try
                {
                    using (var reader = XmlReader.Create(result.RecordPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
                    {
                        var record = XElement.Load(reader);
                        recordedOriginal = (string)record.Element("original");
                        result.ExpectedPatchedHash = (string)record.Element("patched");
                        result.RecordValid = record.Name == "patch" && (string)record.Attribute("version") == "1" &&
                            ValidHash(recordedOriginal) && ValidHash(result.ExpectedPatchedHash);
                    }
                }
                catch (Exception) { result.RecordValid = false; }
            }
            result.BackupExists = File.Exists(result.BackupPath);
            if (result.BackupExists)
            {
                var backup = File.ReadAllBytes(result.BackupPath);
                result.BackupHash = Hash(backup);
                result.BackupValid = result.RecordExists ? result.RecordValid && result.BackupHash == recordedOriginal && DownloadCode.Analyze(backup).CanPatch :
                    result.BackupHash == PatchProfile.OriginalSha256 || (result.State == LauncherState.Original && result.BackupHash == result.Hash);
                if (!result.RecordExists && result.BackupHash == PatchProfile.OriginalSha256) result.ExpectedPatchedHash = PatchProfile.PatchedSha256;
                if (result.BackupValid && result.ExpectedPatchedHash == result.Hash) result.State = LauncherState.Patched;
                if (result.State == LauncherState.Original && result.BackupHash != result.Hash) result.BackupValid = false;
            }
            return result;
        }

        private static bool ValidHash(string value)
        {
            return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        internal static byte[] Transform(byte[] original, Stream payloadStream)
        {
            using (var resolver = new EmbeddedDependencyResolver())
            using (var input = new MemoryStream(original, false))
            using (var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
            using (var payload = AssemblyDefinition.ReadAssembly(payloadStream, new ReaderParameters { InMemory = true }))
            using (var output = new MemoryStream())
            {
                resolver.Target = assembly;
                var analysis = DownloadCode.Analyze(assembly.MainModule);
                if (!analysis.CanPatch) throw new InvalidOperationException(analysis.Reason);
                var targets = MethodTransplant.AllTypes(assembly.MainModule).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
                var sources = MethodTransplant.AllTypes(payload.MainModule).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
                var downloadFlag = assembly.MainModule.GetType("PCL.ModSecret").Fields.Single(f => f.Name == "RestrictMcDownload");
                if (!downloadFlag.IsLiteral || !object.Equals(downloadFlag.Constant, true)) throw new InvalidOperationException("下载限制标志与已验证版本不符。");
                downloadFlag.Constant = false;
                var transplant = new MethodTransplant(assembly.MainModule);
                foreach (var name in PatchProfile.Methods)
                {
                    if (!targets.ContainsKey(name) || !sources.ContainsKey(name)) throw new InvalidOperationException("目标下载方法与补丁不匹配。");
                    transplant.Copy(sources[name], targets[name]);
                }
                assembly.Write(output, new WriterParameters { Timestamp = 0 });
                var patched = output.ToArray();
                using (var beforeStream = new MemoryStream(original, false))
                using (var afterStream = new MemoryStream(patched, false))
                using (var before = AssemblyDefinition.ReadAssembly(beforeStream))
                using (var after = AssemblyDefinition.ReadAssembly(afterStream))
                    DownloadCode.VerifyPreserved(before.MainModule, after.MainModule, payload.MainModule);
                if (Hash(original) == PatchProfile.OriginalSha256 && Hash(patched) != PatchProfile.PatchedSha256)
                    throw new InvalidOperationException("官方样本的补丁输出校验失败，未替换文件。");
                return patched;
            }
        }

        private static void CheckWritableAndClosed(string path)
        {
            foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(path)))
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("请先完全关闭 PCL，再进行修复或还原。");
                    }
                    catch (System.ComponentModel.Win32Exception) { /* The exclusive file check below is also required. */ }
                }
            }
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new InvalidOperationException("启动器文件是只读的，请先取消只读属性。");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }

        private static string WriteTemporary(string target, byte[] data)
        {
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                if (FileHash(temp) != Hash(data)) throw new IOException("临时文件校验失败，原文件未被替换。");
                return temp;
            }
            catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        }

        private static void EnsureBackup(Inspection inspection, byte[] original)
        {
            if (inspection.BackupExists)
            {
                if (!inspection.BackupValid) throw new InvalidOperationException("已有备份未通过校验。为保护原备份，已停止修复；请先妥善保存并移走该备份。");
                return;
            }
            var temporary = WriteTemporary(inspection.BackupPath, original);
            try { File.Move(temporary, inspection.BackupPath); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            if (FileHash(inspection.BackupPath) != Hash(original)) throw new IOException("原文件备份校验失败，未替换启动器。");
        }

        private static void EnsureRecord(Inspection inspection, string originalHash, string patchedHash)
        {
            if (inspection.RecordExists)
            {
                if (!inspection.RecordValid || inspection.BackupHash != originalHash || inspection.ExpectedPatchedHash != patchedHash)
                    throw new InvalidOperationException("已有修复记录与此文件不匹配，已停止操作以保留原记录和备份。");
                return;
            }
            byte[] record;
            using (var memory = new MemoryStream())
            {
                new XDocument(new XElement("patch", new XAttribute("version", "1"), new XElement("original", originalHash), new XElement("patched", patchedHash))).Save(memory);
                record = memory.ToArray();
            }
            var temporary = WriteTemporary(inspection.RecordPath, record);
            try { File.Move(temporary, inspection.RecordPath); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static byte[] CreatePatch(byte[] original)
        {
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("DownloadMethods")) return Transform(original, payload);
        }

        private static void ReplaceVerified(string target, byte[] data, string expectedCurrentHash)
        {
            var temporary = WriteTemporary(target, data);
            try
            {
                if (expectedCurrentHash == "")
                {
                    if (File.Exists(target)) throw new IOException("目标文件已发生变化，请重新检查后再试。");
                    File.Move(temporary, target);
                }
                else
                {
                    if (!File.Exists(target) || FileHash(target) != expectedCurrentHash)
                        throw new IOException("启动器文件已发生变化，请重新检查后再试。");
                    ReplaceWithRetry(temporary, target, expectedCurrentHash);
                }
                if (FileHash(target) != Hash(data)) throw new IOException("替换后校验失败。原文件备份仍保留，请使用还原功能。");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void ReplaceWithRetry(string temporary, string target, string expectedHash)
        {
            // Windows scanners can briefly hold a newly rewritten EXE. Retry
            // only transient access errors, and only while the old target and
            // the prepared replacement are still present and unchanged.
            var replacementHash = FileHash(temporary);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(temporary, target, null); return; }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xffff;
                    if (attempt >= 3 || (code != 5 && code != 32 && code != 33 && code != 1175) ||
                        !File.Exists(target) || !File.Exists(temporary) || FileHash(target) != expectedHash || FileHash(temporary) != replacementHash) throw;
                    Thread.Sleep(150 * (attempt + 1));
                }
            }
        }

        internal static OperationResult Repair(string path)
        {
            path = NormalizePath(path);
            return WithLock(path, delegate
            {
                var inspection = Inspect(path);
                if (inspection.State == LauncherState.Patched)
                    return new OperationResult { Inspection = inspection, Changed = false, Message = "这个文件已经修复，无需重复操作。" };
                if (inspection.State == LauncherState.DownloadsEnabled)
                    return new OperationResult { Inspection = inspection, Changed = false, Message = inspection.Reason };
                if (inspection.State != LauncherState.Original)
                    throw new InvalidOperationException(inspection.Reason);
                CheckWritableAndClosed(path);
                var original = File.ReadAllBytes(path);
                if (Hash(original) != inspection.Hash) throw new IOException("目标文件已发生变化，请重新检查后再试。");
                var patched = CreatePatch(original);
                EnsureBackup(inspection, original);
                EnsureRecord(inspection, inspection.Hash, Hash(patched));
                // Deny writes/deletion to the verified backup until replacement finishes.
                using (var backupGuard = new FileStream(inspection.BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var algorithm = SHA256.Create())
                        if (BitConverter.ToString(algorithm.ComputeHash(backupGuard)).Replace("-", "").ToLowerInvariant() != inspection.Hash)
                            throw new IOException("替换前的备份校验失败，原文件未被替换。");
                    ReplaceVerified(path, patched, inspection.Hash);
                }
                return new OperationResult { Inspection = Inspect(path), Changed = true, Message = "修复完成！修改前的完整文件已备份，其他方法和内嵌资源校验通过。" };
            });
        }

        internal static OperationResult Restore(string path)
        {
            path = NormalizePath(path);
            return WithLock(path, delegate
            {
                var inspection = Inspect(path);
                if (inspection.State == LauncherState.Original)
                    return new OperationResult { Inspection = inspection, Changed = false, Message = "当前文件尚未应用本补丁，无需还原。" };
                if (!inspection.BackupValid) throw new InvalidOperationException("未找到与修复记录匹配的完整备份，无法自动还原。");
                if (inspection.State != LauncherState.Patched && inspection.State != LauncherState.Missing)
                    throw new InvalidOperationException("当前文件已被其他程序修改或更新。为避免覆盖新版本，已停止还原；原版备份仍保留。");
                if (File.Exists(path)) CheckWritableAndClosed(path);
                using (var backupGuard = new FileStream(inspection.BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var memory = new MemoryStream())
                {
                    backupGuard.CopyTo(memory);
                    var backup = memory.ToArray();
                    if (Hash(backup) != inspection.BackupHash) throw new InvalidOperationException("还原前的备份校验失败，目标文件未被替换。");
                    var expectedPatched = inspection.BackupHash == PatchProfile.OriginalSha256 ? PatchProfile.PatchedSha256 : Hash(CreatePatch(backup));
                    if (expectedPatched != inspection.ExpectedPatchedHash) throw new InvalidOperationException("备份与修复输出不对应，目标文件未被替换。");
                    ReplaceVerified(path, backup, inspection.Hash);
                }
                return new OperationResult { Inspection = Inspect(path), Changed = true, Message = "已还原为修改前的完整文件，备份和修复记录继续保留。" };
            });
        }

        private static OperationResult WithLock(string path, Func<OperationResult> action)
        {
            var key = Hash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
            using (var mutex = new Mutex(false, "Local\\PCLDownloadPatcher_" + key))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("另一个修复工具正在操作此文件，请等待它完成。");
                try { return action(); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
