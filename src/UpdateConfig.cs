namespace PortableBuilder
{
    /// <summary>
    /// GitHub repository that hosts PortableBuilder releases.
    /// The release workflow overwrites the placeholders with the real
    /// github.repository values before building, so the shipped EXE knows
    /// exactly where to look for updates.
    /// </summary>
    internal static class UpdateConfig
    {
        public const string Owner = "xmoustafa";
        public const string Repo = "PortableBuilder";
        public const string AssetName = "PortableBuilder.exe";
        public const string ShaAssetSuffix = ".sha256";
    }
}
