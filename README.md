# PCL 下载修复工具

独立的第三方 Windows 工具，修复已安装的 **官方 PCL 正式版 2.99.0.0** 的原版 Minecraft 下载和相关整合包安装入口。由 chocoQAQ 维护，AI 编程助手 Codex 辅助开发。原作 Plain Craft Launcher 的作者为龙腾猫跃，原作源码和赞助地址分别为 https://github.com/Meloong-Git/PCL 和 https://meloong.com/afd/a/LTCat。

- [直接下载 EXE](https://github.com/chocoQAQ/PCL-Download-Patcher/releases/latest/download/PCL-Download-Patcher.exe)
- [查看构建和测试](https://github.com/chocoQAQ/PCL-Download-Patcher/actions/workflows/build.yml)

## 使用

从 Releases 下载并运行 `PCL-Download-Patcher.exe`，选择已有的 PCL EXE，点击“一键修复”。也可以把启动器拖入工具窗口，或把工具放在 `PCL.exe` / `Plain Craft Launcher 2.exe` 旁边自动识别。操作前请关闭 PCL。

修改前，工具会在启动器旁保存完整原版备份，命名为 `<原文件名>.pcl-download.original.bak`。点击“还原原版”可以恢复修改前的字节；还原后继续保留备份。不要删除备份。

工具及生成的启动器均不需要额外 DLL，依赖 Windows 上的 .NET Framework 4.8。工具离线运行，不启动被选择的程序，不登录账号，也不读取游戏账号配置。

当前只接受经过验证的官方文件 SHA-256：

```text
6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386
```

文件来自上游 `2.99.0.0` 标签的 `最新正式版.zip`，压缩包 Git blob 为 `52843e0ba1c97a888630be8326fbc5a132157ccc`。版本名相同但内容不同的文件、其他版本、损坏文件都不会被修改。PCL 自身更新后产生的新文件需要重新检查并适配。

## 修改范围与保护

补丁恢复 5 个下载相关方法，并清除下载限制常量。它保留现有登录、CurseForge、主题、更新等实现和所有内嵌资源；这些在线服务的可用性仍取决于原版服务和账号状态，未在本次进行真人登录验证。

只在原版和补丁输出的完整哈希均匹配时替换文件。先落盘并校验备份，再用同目录临时文件原子替换；替换期间锁定已验证的备份。重复修复/还原为无操作。备份损坏、目标被其他程序更新/修改、文件占用或只读时会停止。备份有效但目标丢失时，也支持恢复文件。

源码中的 `MethodTransplant` 以方法、字段和类型的符号名称重新绑定补丁代码，`EmbeddedDependencyResolver` 仅在内存中解析目标内嵌依赖的元数据。补丁负载只包含选定的下载方法，不包含完整启动器、微软登录或 CurseForge 凭据。

## 构建与验证

```powershell
pwsh -File Build.ps1
# 完整验证：自动取得并校验固定官方版本，再执行全部检查
pwsh -File tests/Test-All.ps1
# 也可以使用本地官方 EXE 作为测试输入
pwsh -File tests/Test-Patcher.ps1 -OriginalLauncher '官方 PCL.exe 的完整路径'
# 使用上一个测试记录中的 patched_launcher 路径
pwsh -File tests/Test-Launcher.ps1 -PatchedLauncher '已修复 PCL.exe 的完整路径'
```

构建脚本使用 Windows 自带的 .NET Framework C# 编译器，将 Mono.Cecil 和补丁负载嵌入单个 EXE；不依赖本机 SDK 或相邻 PCL-fix 项目。也可用 Visual Studio / MSBuild 打开 `PCL-Download-Patcher.csproj`。

构建前会检查编辑库与补丁负载的固定 SHA-256；输出位于 `dist`，并附有本地校验值和构建信息。完整验证从上游固定提交下载官方压缩包，核对压缩包及 EXE 的 SHA-256，再使用隔离副本测试。

`Test-Patcher.ps1` 在隔离目录中以单个 EXE 执行 17 次检查，覆盖正常修复、字节一致的备份和还原、重复操作、中文及空格路径、未知文件、损坏备份、外部修改、占用、只读和目标丢失恢复。`VerifyPreservation.cs` 比较规范化方法体及字段、内嵌资源，确认只有约定的下载方法及标志发生改变。`LauncherSmoke.cs` 会 JIT 编译全部 5 个补丁方法，并通过修复后的实际下载器下载 Minecraft 1.21.1 客户端、资源索引和抽样文件，核对 Mojang SHA-1。

补丁生成来源为 `chocoQAQ/PCL-fix` 的 `24b575d`，与官方同版公开源码的已恢复下载流程一致。`payload/DownloadMethods.dll` 只含 5 个方法体，源码及生成工具见 `tools/PreparePayload.cs`。重新生成需要先构建该来源版本，再以源码编译器编译生成工具及 `src/MethodTransplant.cs`、`src/PatchProfile.cs`，传入来源 EXE 和负载输出路径。更改负载后必须重新计算、审核并更新 `PatchedSha256`。

可选命令行接口（用于测试和批处理）：

```powershell
.\dist\PCL-Download-Patcher.exe --inspect 'C:\Minecraft\PCL.exe' --report result.xml
.\dist\PCL-Download-Patcher.exe --repair 'C:\Minecraft\PCL.exe' --report result.xml
.\dist\PCL-Download-Patcher.exe --restore 'C:\Minecraft\PCL.exe' --report result.xml
```

## 自动构建与发布

推送到 `main`、提交 Pull Request 或在 Actions 手动运行都会触发 Windows 构建及完整验证。只有本仓库主分支通过所有检查后才创建 Release；Pull Request 只构建测试。

Release 仅附上 `PCL-Download-Patcher.exe`，不打压缩包、不附带完整官方启动器，SHA-256 放在 Release 说明中。发布以源码提交号生成标签，上传完成后再公开并标记为最新版本。工作流使用仓库自带的 `GITHUB_TOKEN`，无需额外 Secret。

## 来源与许可

PCL 派生方法继续遵循随附的 [PCL-LICENCE.txt](PCL-LICENCE.txt)。Mono.Cecil 0.11.6 来自其官方 NuGet 包，遵循 [MIT 许可](vendor/Mono.Cecil.LICENSE.txt)。这些许可同时嵌入 EXE，可在“关于与许可证”中查看。工具与 PCL 官方无隶属关系。
