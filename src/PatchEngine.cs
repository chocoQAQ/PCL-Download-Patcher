using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Mono.Cecil;

namespace PclDownloadPatcher
{
    internal enum LauncherState { Original, Patched, Unsupported, Missing }

    internal sealed class Inspection
    {
        internal string Path;
        internal string Hash;
        internal string BackupPath;
        internal LauncherState State;
        internal bool BackupExists;
        internal bool BackupValid;
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
            var result = new Inspection { Path = path, BackupPath = path + PatchProfile.BackupSuffix, State = LauncherState.Missing, Hash = "" };
            if (File.Exists(path))
            {
                result.Hash = FileHash(path);
                result.State = result.Hash == PatchProfile.OriginalSha256 ? LauncherState.Original :
                    result.Hash == PatchProfile.PatchedSha256 ? LauncherState.Patched : LauncherState.Unsupported;
            }
            result.BackupExists = File.Exists(result.BackupPath);
            result.BackupValid = result.BackupExists && new FileInfo(result.BackupPath).Length == 5980160 &&
                FileHash(result.BackupPath) == PatchProfile.OriginalSha256;
            return result;
        }

        internal static byte[] Transform(byte[] original, Stream payloadStream)
        {
            if (Hash(original) != PatchProfile.OriginalSha256) throw new InvalidOperationException("该文件不在已验证的支持列表中，未进行修改。");
            using (var resolver = new EmbeddedDependencyResolver())
            using (var input = new MemoryStream(original, false))
            using (var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
            using (var payload = AssemblyDefinition.ReadAssembly(payloadStream, new ReaderParameters { InMemory = true }))
            using (var output = new MemoryStream())
            {
                resolver.Target = assembly;
                if (assembly.Name.Version.ToString() != PatchProfile.Version || assembly.Name.HasPublicKey)
                    throw new InvalidOperationException("目标程序集与已验证版本不符。");
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
                return output.ToArray();
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
            if (FileHash(inspection.BackupPath) != PatchProfile.OriginalSha256) throw new IOException("原版备份校验失败，未替换启动器。");
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
                    File.Replace(temporary, target, null);
                }
                if (FileHash(target) != Hash(data)) throw new IOException("替换后校验失败。原版备份仍保留，请使用还原功能。");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static OperationResult Repair(string path)
        {
            path = NormalizePath(path);
            return WithLock(path, delegate
            {
                var inspection = Inspect(path);
                if (inspection.State == LauncherState.Patched)
                    return new OperationResult { Inspection = inspection, Changed = false, Message = "这个文件已经修复，无需重复操作。" };
                if (inspection.State != LauncherState.Original)
                    throw new InvalidOperationException("仅支持已核验的官方 PCL 正式版 2.99.0.0；未知版本或被修改的文件不会被覆盖。");
                CheckWritableAndClosed(path);
                var original = File.ReadAllBytes(path);
                byte[] patched;
                using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("DownloadMethods"))
                    patched = Transform(original, payload);
                if (Hash(patched) != PatchProfile.PatchedSha256) throw new InvalidOperationException("补丁输出校验失败，原文件未被替换。");
                EnsureBackup(inspection, original);
                // Deny writes/deletion to the verified backup until replacement finishes.
                using (var backupGuard = new FileStream(inspection.BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var algorithm = SHA256.Create())
                        if (BitConverter.ToString(algorithm.ComputeHash(backupGuard)).Replace("-", "").ToLowerInvariant() != PatchProfile.OriginalSha256)
                            throw new IOException("替换前的备份校验失败，原文件未被替换。");
                    ReplaceVerified(path, patched, inspection.Hash);
                }
                return new OperationResult { Inspection = Inspect(path), Changed = true, Message = "修复完成！原版备份已保存，现在可以重新打开 PCL 下载游戏。" };
            });
        }

        internal static OperationResult Restore(string path)
        {
            path = NormalizePath(path);
            return WithLock(path, delegate
            {
                var inspection = Inspect(path);
                if (inspection.State == LauncherState.Original)
                    return new OperationResult { Inspection = inspection, Changed = false, Message = "当前文件已经是受支持的原版，无需还原。" };
                if (!inspection.BackupValid) throw new InvalidOperationException("未找到通过校验的原版备份，无法自动还原。");
                if (inspection.State != LauncherState.Patched && inspection.State != LauncherState.Missing)
                    throw new InvalidOperationException("当前文件已被其他程序修改或更新。为避免覆盖新版本，已停止还原；原版备份仍保留。");
                if (File.Exists(path)) CheckWritableAndClosed(path);
                using (var backupGuard = new FileStream(inspection.BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var memory = new MemoryStream())
                {
                    backupGuard.CopyTo(memory);
                    var backup = memory.ToArray();
                    if (Hash(backup) != PatchProfile.OriginalSha256) throw new InvalidOperationException("还原前的备份校验失败，目标文件未被替换。");
                    ReplaceVerified(path, backup, inspection.Hash);
                }
                return new OperationResult { Inspection = Inspect(path), Changed = true, Message = "已还原为修改前的官方原版，备份文件继续保留。" };
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
