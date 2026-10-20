using System;

namespace PclDownloadPatcher
{
    internal static class PatchProfile
    {
        internal const string Version = "2.99.0.0";
        internal const string OriginalSha256 = "6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386";
        internal const string PatchedSha256 = "f51c5c0211f3345c9d621c8bda946b3c5943c21290eddf4a70fe058e11892e27";
        internal const string BackupSuffix = ".pcl-download.original.bak";
        internal const string RecordSuffix = ".pcl-download.record.xml";
        // Symbolic IL fingerprints from the SHA-256 pinned official 2.99 release.
        internal static readonly string[] OriginalMethodSignatures = {
            "69b2acb10c9f00fdd22f503bcac7119ed963fbf4e5c3d531ae943a8970152665",
            "fee4450efaeed3bf6457f8b92ecdde955f018fb2cb50aac809c9acefa2c0245f",
            "a952c16b45f548dbd9f2c58d8c1452517bef3b6095b69858248dd2418060bedf",
            "8147ee9a481fcfb189e1cfd02b1fda823c1a840b3d80c024751bdbda3e065863",
            "926ebce266e2f9c0f6343c9664a0360b801119d12ff3d1080afc6b7ef4086d51"
        };
        internal static readonly string[] Methods = {
            "System.Void PCL.PageDownloadInstall::LoaderInit()",
            "System.Void PCL.PageDownloadModify::LoadInstanceFinished(PCL.ModLoader/LoaderTask`2<PCL.ModMinecraft/McInstance,System.Object>)",
            "System.Void PCL.PageDownloadResourceDetail::InitializeStackResourceVersions(System.Windows.Controls.StackPanel)",
            "System.Void PCL.ModModpack::ModpackInstall()",
            "PCL.ModLoader/LoaderCombo`1<System.String> PCL.ModModpack::ModpackInstall(System.String,System.String,System.String)"
        };
    }
}
