using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BonV.Editor
{
    /// <summary>
    /// The render pipeline an exported bundle is tagged with, so BonStudio can tell what it's made for. It follows the project's
    /// pipeline, which says nothing of the shaders in the bundle, so the export windows can override it.
    /// </summary>
    public static class BundleRenderPipeline
    {
        private static readonly string[] Tags = { null, "URP", "HDRP", "BRP" };
        // Not saved, so a choice made for one export can't quietly mislabel later ones
        private static int _choice;

        // By name, so the SDK needs neither pipeline's package
        private static string Detected
        {
            get
            {
                var asset = GraphicsSettings.currentRenderPipeline;
                if (!asset) return "BRP";
                string type = asset.GetType().Name;
                if (type.StartsWith("Universal")) return "URP";
                return type.StartsWith("HD") ? "HDRP" : "";
            }
        }

        /// <summary>The tag to export with.</summary>
        public static string Selected => _choice == 0 ? Detected : Tags[_choice];

        /// <summary>Draws the choice and returns the tag to export with.</summary>
        public static string Field() => Field(false);

        /// <summary>Draws the choice, with its label above it when stacked, and returns the tag to export with.</summary>
        public static string Field(bool stacked)
        {
            string detected = Detected;
            string[] labels = { detected.Length > 0 ? $"Auto ({detected})" : "Auto (unknown)", Tags[1], Tags[2], Tags[3] };
            var label = new GUIContent("Render Pipeline",
                "The pipeline this bundle is made for. Auto uses the project's; choose another if its shaders were made for a different one.");
            if (stacked)
            {
                GUILayout.Label(label, EditorStyles.boldLabel);
                _choice = EditorGUILayout.Popup(_choice, labels);
            }
            else
            {
                _choice = EditorGUILayout.Popup(label, _choice, labels);
            }
            return Selected;
        }
    }
}
