// Runs against the built executable and its embedded DLLs, without opening windows
// or modifying the user's launcher settings. Keep compatible with Framework csc.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using PCL;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class LauncherSmoke
{
    private static Assembly launcher;
    private static readonly Dictionary<string, Assembly> dependencies = new Dictionary<string, Assembly>();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            launcher = Assembly.LoadFrom(Path.Combine(args[0], "Plain Craft Launcher 2.exe"));
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            // PCL's download manager owns long-lived threads; end this test process
            // after the checks instead of leaving those manager threads running.
            Environment.Exit(Run(args[0], args[1], bool.Parse(args[2])));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
            return 1;
        }
    }

    private static Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        var names = new[] { "NAudio", "Newtonsoft.Json", "Ookii.Dialogs.Wpf", "Imazen.WebP", "CacheCow.Common", "CacheCow.Client.FileStore", "CacheCow.Client", "ThrottleDebounce", "System.Net.Http.Formatting", "Microsoft.Win32.Registry", "PCLCS", "MeloongCore.Wpf", "MeloongCore" };
        if (!names.Contains(name)) return null;
        lock (dependencies)
        {
            Assembly loaded;
            if (dependencies.TryGetValue(name, out loaded)) return loaded;
            var bytes = new ResourceManager("PCL.Resources", launcher).GetObject(name) as byte[];
            if (bytes == null) return null;
            loaded = Assembly.Load(bytes);
            dependencies[name] = loaded;
            return loaded;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string work, string version, bool fullInstall)
    {
        var app = new System.Windows.Application();
        MeloongCore.Main.Init("PCLFixDownloadTest");
        // Populate the in-memory defaults before any launcher code reads the registry.
        foreach (var setting in Settings.Entries.Values)
            setting.ValueCache[""] = setting.DefaultValue;
        Settings.Entries["ToolDownloadSource"].ValueCache[""] = 2;
        Settings.Entries["ToolDownloadVersion"].ValueCache[""] = 2;
        var mcRoot = Path.Combine(work, ".minecraft") + Path.DirectorySeparatorChar;
        Settings.Entries["LaunchFolderSelect"].ValueCache[""] = mcRoot;
        ModBase.PathTemp = Path.Combine(work, "cache") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(ModBase.PathTemp);
        Directory.CreateDirectory(Path.Combine(ModBase.PathTemp, "Cache"));
        Directory.CreateDirectory(Path.Combine(mcRoot, "versions", version));

        var secret = launcher.GetType("PCL.ModSecret", true);
        if ((bool)secret.GetField("RestrictMcDownload").GetRawConstantValue())
            throw new Exception("The compiled launcher still restricts Minecraft downloads.");
        var versionName = (string)typeof(ModBase).GetField("VersionBaseName").GetRawConstantValue();
        if (versionName != "2.99.0.0") throw new Exception("Unexpected launcher version.");

        // JIT the five transplanted methods to catch invalid IL, member bindings
        // and generic/local signatures without opening or starting PCL itself.
        var binding = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var methodList = new[] {
            typeof(PageDownloadInstall).GetMethod("LoaderInit", binding),
            typeof(PageDownloadModify).GetMethod("LoadInstanceFinished", binding),
            typeof(PageDownloadResourceDetail).GetMethod("InitializeStackResourceVersions", binding)
        }.Concat(typeof(ModModpack).GetMethods(binding).Where(m => m.Name == "ModpackInstall"));
        foreach (var method in methodList) RuntimeHelpers.PrepareMethod(method.MethodHandle);
        Console.WriteLine("PASS: all five patched methods JIT-compiled successfully.");
        Exception failure = null;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var thread = new Thread(delegate()
        {
            try { DownloadAndVerify(work, mcRoot, version, fullInstall); }
            catch (Exception ex) { failure = ex; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        thread.IsBackground = true;
        thread.Start();
        Dispatcher.Run();
        thread.Join();
        if (failure != null) throw new Exception("Download verification failed.", failure);
        Console.WriteLine("PASS: compiled policy, manifest, client JAR, asset index, library and asset verified.");
        return 0;
    }

    private static void DownloadAndVerify(string work, string mcRoot, string version, bool fullInstall)
    {
        ModDownload.DlClientListMojangLoader.WaitForExitTime(90000);
        var manifest = ModDownload.DlClientListMojangLoader.Output.Value;
        var entry = manifest["versions"].Single(v => (string)v["id"] == version);
        var instancePath = Path.Combine(mcRoot, "versions", version) + Path.DirectorySeparatorChar;
        var jsonPath = Path.Combine(instancePath, version + ".json");
        var jsonFile = new ModNet.NetFile(new[] { (string)entry["url"] }, jsonPath,
            new MeloongCore.FileChecker { Hash = (string)entry["sha1"], IsJson = true });
        Download("Version JSON", jsonFile);
        var json = JObject.Parse(File.ReadAllText(jsonPath));
        var instance = new ModMinecraft.McInstance(instancePath);
        var jar = ModDownload.DlClientJarGet(instance, false);
        Download("Client JAR", jar);
        VerifyHash(jar.LocalPath, (string)json["downloads"]["client"]["sha1"]);
        var index = ModDownload.DlClientAssetIndexGet(instance);
        Download("Asset index", index);
        VerifyHash(index.LocalPath, (string)json["assetIndex"]["sha1"]);

        var libraries = ModMinecraft.McLibNetFilesFromInstance(instance);
        var library = libraries.First(f => f.LocalPath.IndexOf("libraries", StringComparison.OrdinalIgnoreCase) >= 0);
        Download("Library", library);
        var assets = JObject.Parse(File.ReadAllText(index.LocalPath));
        var asset = ((JObject)assets["objects"]).Properties().First().Value;
        var hash = (string)asset["hash"];
        var assetPath = Path.Combine(mcRoot, "assets", "objects", hash.Substring(0, 2), hash);
        Download("Asset", new ModNet.NetFile(
            new[] { "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash },
            assetPath, new MeloongCore.FileChecker { Hash = hash, ActualSize = (long)asset["size"] }));
        VerifyHash(assetPath, hash);

        if (fullInstall)
        {
            var method = typeof(ModDownloadLib).GetMethod("McDownloadClientLoader", BindingFlags.NonPublic | BindingFlags.Static);
            var loaders = (List<ModLoader.LoaderBase>)method.Invoke(null, new object[] { version, (string)entry["url"], instancePath });
            var install = new ModLoader.LoaderCombo<string>("Full vanilla installation", loaders);
            install.WaitForExitTime(1200000);
        }

        var result = new JObject();
        result["version"] = version;
        result["manifest_versions"] = manifest["versions"].Count();
        result["client_sha1"] = (string)json["downloads"]["client"]["sha1"];
        result["client_bytes"] = new FileInfo(jar.LocalPath).Length;
        result["asset_index_sha1"] = (string)json["assetIndex"]["sha1"];
        result["sample_asset_sha1"] = hash;
        result["full_install"] = fullInstall;
        result["tested_at_utc"] = DateTime.UtcNow.ToString("o");
        File.WriteAllText(Path.Combine(work, "result.json"), result.ToString());
    }

    private static void Download(string name, ModNet.NetFile file)
    {
        Console.WriteLine("Downloading " + name + "...");
        var loader = new ModNet.LoaderDownload(name, new[] { file });
        loader.WaitForExitTime(180000);
        if (!File.Exists(file.LocalPath)) throw new Exception("Missing downloaded file: " + file.LocalPath);
        var error = file.Check == null ? null : file.Check.Check(file.LocalPath);
        if (error != null) throw new Exception(error);
    }

    private static void VerifyHash(string path, string expected)
    {
        using (var stream = File.OpenRead(path))
        using (var sha1 = SHA1.Create())
        {
            var actual = BitConverter.ToString(sha1.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new Exception("SHA-1 mismatch: " + path);
        }
    }
}
