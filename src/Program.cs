using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using System.Xml.Linq;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyTitle("PCL 下载修复工具")]
[assembly: AssemblyDescription("第三方 PCL 2.99.0.0 下载修复工具，支持自动备份和还原")]
[assembly: AssemblyCompany("chocoQAQ")]
[assembly: AssemblyProduct("PCL Download Patcher")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PclDownloadPatcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveLibrary;
            return Run(args);
        }

        private static Assembly ResolveLibrary(object sender, ResolveEventArgs args)
        {
            if (new AssemblyName(args.Name).Name != "Mono.Cecil") return null;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mono.Cecil"))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return Assembly.Load(memory.ToArray());
            }
        }

        internal static string ResourceText(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            if (args.Length > 0 && args[0] == "--preview" && args.Length == 2)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new MainForm())
                using (var image = new Bitmap(form.Width, form.Height))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-20000, -20000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    form.Update();
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                return 0;
            }
            if (args.Length == 0 || (args.Length == 1 && !args[0].StartsWith("--", StringComparison.Ordinal)))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(args.Length == 1 ? args[0] : null));
                return 0;
            }
            string report = args.Length == 4 && args[2] == "--report" ? args[3] : null;
            try
            {
                if (args.Length != 2 && args.Length != 4) throw new ArgumentException("用法：--inspect / --repair / --restore PCL.exe [--report result.xml]");
                OperationResult result;
                switch (args[0])
                {
                    case "--inspect": result = new OperationResult { Inspection = PatchEngine.Inspect(args[1]), Message = "文件检查完成。" }; break;
                    case "--repair": result = PatchEngine.Repair(args[1]); break;
                    case "--restore": result = PatchEngine.Restore(args[1]); break;
                    default: throw new ArgumentException("未知命令。");
                }
                WriteReport(report, result, null);
                Console.WriteLine(result.Message);
                return 0;
            }
            catch (Exception ex)
            {
                WriteReport(report, null, ex);
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static void WriteReport(string path, OperationResult result, Exception error)
        {
            if (path == null) return;
            var element = new XElement("result", new XAttribute("success", error == null),
                new XElement("message", error == null ? result.Message : error.Message),
                new XElement("changed", result != null && result.Changed));
            if (result != null)
            {
                var inspection = result.Inspection;
                element.Add(new XElement("state", inspection.State), new XElement("hash", inspection.Hash),
                    new XElement("target", inspection.Path), new XElement("backup", inspection.BackupPath),
                    new XElement("backupExists", inspection.BackupExists), new XElement("backupValid", inspection.BackupValid));
            }
            new XDocument(element).Save(path);
        }
    }
}
