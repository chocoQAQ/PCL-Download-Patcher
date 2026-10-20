using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PclDownloadPatcher
{
    internal sealed class MainForm : Form
    {
        private readonly TextBox pathBox;
        private readonly Label statusLabel;
        private readonly Label detailLabel;
        private readonly TextBox logBox;
        private readonly Button browseButton;
        private readonly Button repairButton;
        private readonly Button restoreButton;
        private readonly string initialPath;
        private Inspection inspection;
        private bool busy;

        internal MainForm(string path = null)
        {
            initialPath = path;
            Text = "PCL 下载修复工具";
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(246, 248, 252);
            ForeColor = Color.FromArgb(38, 49, 66);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(740, 510);
            MinimumSize = new Size(700, 520);
            AutoScaleMode = AutoScaleMode.Font;
            Icon = SystemIcons.Application;
            AllowDrop = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 22, 24, 15), ColumnCount = 1, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var height in new[] { 42F, 37F, 46F, 84F, 52F, 23F }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "让 PCL 恢复下载", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty }, 0, 0);
            layout.Controls.Add(new Label { Text = "修复前自动备份 · 支持一键还原 · 无需安装", AutoSize = true, ForeColor = Color.FromArgb(96, 111, 132), Margin = new Padding(1, 4, 0, 0) }, 0, 1);

            var fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4) };
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            pathBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, Margin = new Padding(0, 7, 12, 0), TabStop = false };
            browseButton = MakeButton("选择 PCL.exe", false);
            browseButton.Dock = DockStyle.Fill;
            browseButton.Margin = new Padding(0, 0, 0, 4);
            browseButton.Click += Browse;
            fileRow.Controls.Add(pathBox, 0, 0);
            fileRow.Controls.Add(browseButton, 1, 0);
            layout.Controls.Add(fileRow, 0, 2);

            var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 1, Padding = new Padding(13, 10, 10, 5), Margin = new Padding(0, 0, 0, 8) };
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            statusLabel = new Label { Text = "请选择或拖入你的 PCL 启动器", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = Padding.Empty };
            detailLabel = new Label { Text = "支持官方正式版 2.99.0.0，将自动核对文件是否匹配。", AutoSize = true, ForeColor = Color.FromArgb(96, 111, 132), Margin = Padding.Empty, Dock = DockStyle.Fill };
            statusPanel.Controls.Add(statusLabel, 0, 0);
            statusPanel.Controls.Add(detailLabel, 0, 1);
            layout.Controls.Add(statusPanel, 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, WrapContents = false };
            repairButton = MakeButton("一键修复", true);
            restoreButton = MakeButton("还原原版", false);
            repairButton.EnabledChanged += delegate
            {
                repairButton.BackColor = repairButton.Enabled ? Color.FromArgb(25, 91, 190) : Color.FromArgb(224, 230, 239);
                repairButton.FlatAppearance.BorderColor = repairButton.BackColor;
            };
            repairButton.Size = restoreButton.Size = new Size(158, 40);
            repairButton.Margin = new Padding(0, 0, 12, 0);
            restoreButton.Margin = Padding.Empty;
            repairButton.Enabled = restoreButton.Enabled = false;
            repairButton.Click += async delegate { await Operate(false); };
            restoreButton.Click += async delegate { await Operate(true); };
            buttons.Controls.Add(repairButton);
            buttons.Controls.Add(restoreButton);
            layout.Controls.Add(buttons, 0, 4);
            layout.Controls.Add(new Label { Text = "操作记录", AutoSize = true, ForeColor = Color.FromArgb(96, 111, 132), Margin = Padding.Empty }, 0, 5);
            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty, TabStop = false };
            layout.Controls.Add(logBox, 0, 6);
            AddLog("准备就绪。请先关闭 PCL，再选择需要修复的启动器。");

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 10, 0, 0), WrapContents = false };
            footer.Controls.Add(new Label { Text = "第三方修复工具 · PCL 原作者：龙腾猫跃", AutoSize = true, ForeColor = Color.FromArgb(96, 111, 132), Margin = new Padding(0, 0, 16, 0) });
            var about = new LinkLabel { Text = "关于与许可证", AutoSize = true, Margin = Padding.Empty, LinkColor = Color.FromArgb(25, 91, 190) };
            about.LinkClicked += delegate { ShowAbout(); };
            footer.Controls.Add(about);
            layout.Controls.Add(footer, 0, 7);

            DragEnter += delegate(object sender, DragEventArgs e) { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += async delegate(object sender, DragEventArgs e)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (!busy && files != null && files.Length == 1) await InspectFile(files[0]);
            };
            Shown += async delegate
            {
                if (initialPath != null) { await InspectFile(initialPath); return; }
                var adjacent = new[] { "PCL.exe", "Plain Craft Launcher 2.exe" }.Select(name => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name)).FirstOrDefault(File.Exists);
                if (adjacent != null) await InspectFile(adjacent);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; AddLog("正在处理文件，请等待当前操作完成。"); }
            };
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(25, 91, 190) : Color.White, ForeColor = primary ? Color.White : ForeColor, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(25, 91, 190) : Color.FromArgb(206, 216, 229);
            return button;
        }

        private async void Browse(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Title = "选择已有的 PCL 启动器", Filter = "PCL 启动器 (*.exe)|*.exe", CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) await InspectFile(dialog.FileName);
        }

        private void AddLog(string text) { logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine); }

        private void SetBusy(bool value)
        {
            busy = value;
            browseButton.Enabled = !value;
            UseWaitCursor = value;
            RefreshButtons();
        }

        private void RefreshButtons()
        {
            repairButton.Enabled = !busy && inspection != null && inspection.State == LauncherState.Original && (!inspection.BackupExists || inspection.BackupValid);
            restoreButton.Enabled = !busy && inspection != null && inspection.BackupValid && (inspection.State == LauncherState.Patched || inspection.State == LauncherState.Missing);
        }

        private async Task InspectFile(string path)
        {
            SetBusy(true);
            inspection = null;
            pathBox.Text = path;
            statusLabel.Text = "正在检查文件……";
            try { Display(await Task.Run(() => PatchEngine.Inspect(path))); }
            catch (Exception ex) { ShowError(ex); }
            finally { SetBusy(false); }
        }

        private void Display(Inspection value)
        {
            inspection = value;
            pathBox.Text = value.Path;
            statusLabel.ForeColor = value.State == LauncherState.Unsupported ? Color.FromArgb(179, 61, 43) : Color.FromArgb(38, 49, 66);
            switch (value.State)
            {
                case LauncherState.Original:
                    statusLabel.Text = "已识别官方正式版 " + PatchProfile.Version;
                    detailLabel.Text = value.BackupExists && !value.BackupValid ? "已有备份未通过校验，已停止修复以保护原备份。" : "可以修复。修改前会自动保存完整的原版备份。";
                    break;
                case LauncherState.Patched:
                    statusLabel.Text = "下载功能已经修复";
                    detailLabel.Text = value.BackupValid ? "原版备份校验通过，可随时还原。" : "原版备份缺失或损坏，暂时无法自动还原。";
                    break;
                case LauncherState.Missing:
                    statusLabel.Text = "未找到启动器文件";
                    detailLabel.Text = value.BackupValid ? "找到了有效的原版备份，可以还原此文件。" : "请重新选择已有的 PCL.exe。";
                    break;
                default:
                    statusLabel.Text = "此文件不在已验证的支持列表中";
                    detailLabel.Text = "仅支持官方正式版 2.99.0.0；文件未被修改。";
                    break;
            }
            AddLog(statusLabel.Text);
            if (value.BackupValid) AddLog("原版备份：" + value.BackupPath);
            RefreshButtons();
        }

        private async Task Operate(bool restore)
        {
            if (busy || inspection == null) return;
            var path = inspection.Path;
            SetBusy(true);
            AddLog(restore ? "正在校验备份并还原原版……" : "正在准备补丁并自动备份原版……");
            try
            {
                var result = await Task.Run(() => restore ? PatchEngine.Restore(path) : PatchEngine.Repair(path));
                Display(result.Inspection);
                AddLog(result.Message);
                statusLabel.ForeColor = Color.FromArgb(31, 125, 77);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally { SetBusy(false); }
        }

        private void ShowError(Exception ex)
        {
            statusLabel.Text = "操作未完成";
            statusLabel.ForeColor = Color.FromArgb(179, 61, 43);
            detailLabel.Text = ex is IOException ? "文件可能正在使用。请关闭 PCL，并确认目录可以写入后重试。" : ex.Message;
            AddLog(ex.Message);
        }

        private void ShowAbout()
        {
            using (var dialog = new Form { Text = "关于与许可证", StartPosition = FormStartPosition.CenterParent, Size = new Size(680, 500), Font = Font, MinimizeBox = false, MaximizeBox = false })
            {
                var text = "PCL 下载修复工具 1.0.0\r\n由 chocoQAQ 维护的第三方工具，非 PCL 官方发行。\r\n原作 Plain Craft Launcher：龙腾猫跃。\r\n原作源码：https://github.com/Meloong-Git/PCL\r\n赞助原作者：https://meloong.com/afd/a/LTCat\r\n\r\n" + Program.ResourceText("PclLicense") + "\r\n\r\nMono.Cecil 0.11.6\r\n" + Program.ResourceText("CecilLicense");
                dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = text, BackColor = Color.White, Padding = new Padding(10) });
                dialog.ShowDialog(this);
            }
        }
    }
}
