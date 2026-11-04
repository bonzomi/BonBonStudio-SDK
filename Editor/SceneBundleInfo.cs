using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace BonV.Editor
{
    /// <summary>
    /// What BonStudio shows for an exported scene, written as a .json named like the bundle beside it, the way the
    /// thumbnail is. The publish script copies it into the bucket's index.json.
    /// </summary>
    [Serializable]
    public class SceneBundleInfo
    {
        public const int MaxIdLength = 64;
        public const int MaxTitleLength = 64;
        public const int MaxDescriptionLength = 500;
        private static readonly Regex IdPattern = new(@"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$");

        // The scene's SceneInfo UUID; stays the same through renames, so the app can tell it's the same scene
        public string id;
        // What players see in place of the file name
        public string title;
        public string description;
        // URP, HDRP or BRP; see BundleRenderPipeline
        public string renderPipeline;

        public static string PathFor(string bundlePath) => Path.ChangeExtension(bundlePath, ".json");

        /// <summary>Why an ID typed into the component by hand can't be used, or null. Empty is fine: the export makes one.</summary>
        public static string IdProblem(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.Length > MaxIdLength) return $"The ID can be at most {MaxIdLength} characters. Use New ID on the Scene Info component.";
            if (!IdPattern.IsMatch(id))
                return "The ID can only use lowercase letters, digits, . _ and -. Use New ID on the Scene Info component.";
            return null;
        }

        public static SceneBundleInfo Read(string bundlePath) => ReadFile(PathFor(bundlePath));

        public void Write(string bundlePath) => File.WriteAllText(PathFor(bundlePath), JsonUtility.ToJson(this, true) + "\n");

        private static SceneBundleInfo ReadFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<SceneBundleInfo>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static readonly Dictionary<string, (double time, List<(string bundle, string id)> ids)> FolderIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The file name of another bundle in the folder that already uses this ID, or null.
        /// Each folder is read again at most every two seconds, since the window asks on every repaint.
        /// </summary>
        public static string IdTakenBy(string bundlePath, string id)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(bundlePath));
            if (string.IsNullOrEmpty(id) || !Directory.Exists(folder)) return null;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (!FolderIds.TryGetValue(folder, out var cached) || now - cached.time > 2)
            {
                var ids = new List<(string, string)>();
                foreach (string file in Directory.GetFiles(folder, "*.json"))
                {
                    // Only sidecars whose bundle is still there
                    string bundle = Path.ChangeExtension(file, Path.GetExtension(bundlePath));
                    if (File.Exists(bundle) && ReadFile(file)?.id is { Length: > 0 } other) ids.Add((bundle, other));
                }
                FolderIds[folder] = cached = (now, ids);
            }
            string own = Path.GetFullPath(bundlePath);
            foreach (var (bundle, other) in cached.ids)
            {
                if (other == id && !string.Equals(Path.GetFullPath(bundle), own, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(bundle);
            }
            return null;
        }
    }
}
