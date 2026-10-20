using System;

namespace PclDownloadPatcher
{
    internal static class PatchProfile
    {
        internal const string Version = "2.99.0.0";
        internal const string OriginalSha256 = "6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386";
        internal const string PatchedSha256 = "f51c5c0211f3345c9d621c8bda946b3c5943c21290eddf4a70fe058e11892e27";
        internal const string BackupSuffix = ".pcl-download.original.bak";
        internal static readonly string[] Methods = {
            "System.Void PCL.PageDownloadInstall::LoaderInit()",
            "System.Void PCL.PageDownloadModify::LoadInstanceFinished(PCL.ModLoader/LoaderTask`2<PCL.ModMinecraft/McInstance,System.Object>)",
            "System.Void PCL.PageDownloadResourceDetail::InitializeStackResourceVersions(System.Windows.Controls.StackPanel)",
            "System.Void PCL.ModModpack::ModpackInstall()",
            "PCL.ModLoader/LoaderCombo`1<System.String> PCL.ModModpack::ModpackInstall(System.String,System.String,System.String)"
        };
    }
}
