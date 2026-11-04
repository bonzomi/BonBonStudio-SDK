namespace BonV.Editor
{
    /// <summary>
    /// Publishing for the Export Environment Scene and Export Character Model windows. The SDK only exports; a project that
    /// hosts bundles registers publishers in <see cref="BundleExportPublishing"/>, and the windows then offer to publish each
    /// export with the settings the publisher draws.
    /// </summary>
    public interface IBundleExportPublisher
    {
        /// <summary>
        /// Draws the publish settings for the bundle, named without its extension, on the window's Publish tab, which is the
        /// choice to publish. bundleId is the scene's or model's ID, empty before its first export.
        /// </summary>
        void DrawOptions(string bundleName, string bundleId);

        /// <summary>Why the bundle can't be published as set up, or null when it can.</summary>
        string Problem(string bundleName);

        /// <summary>Where an export has to go to be published; used in place of the save dialog while publishing is on.</summary>
        string BundlePath(string bundleName);

        /// <summary>
        /// Uploads the exported bundle, with the thumbnail and .json next to it. Reports its own errors rather than throwing.
        /// bundleId is the scene's or model's ID, which the export has made by now.
        /// </summary>
        void Publish(string bundleName, string bundlePath, string bundleId);

        /// <summary>Draws the running or last publish's progress bar and log, which ends with whether it succeeded; nothing before the first.</summary>
        void DrawStatus();

        /// <summary>Changes whenever the progress or log does, so a window showing them knows to repaint.</summary>
        int StatusVersion { get; }
    }

    public static class BundleExportPublishing
    {
        public static IBundleExportPublisher Scenes { get; set; }
        public static IBundleExportPublisher Models { get; set; }
    }
}
