using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BonV.Editor
{
    /// <summary>
    /// The exported file's name: a slug of the title and the first 8 characters of the ID, such as
    /// "melody-cyberscientist-3f2a9c1e". Made on the first export and kept in the Scene Info or Model Info from then on, so
    /// it's the same on every machine, doesn't follow later title changes, and two scenes or models with the same title
    /// still get different files. Names from before this, kept in EditorPrefs or found by the ID among exported bundles,
    /// are kept as they are, so nothing published is renamed.
    /// </summary>
    public static class BundleFileName
    {
        private const int MaxSlugLength = 40;
        private const int IdChars = 8;
        // Stands in for the ID's characters before the first export makes the ID
        private const string PendingId = "????????";

        /// <summary>Where a scene's or model's file name comes from.</summary>
        public sealed class Source
        {
            /// <summary>The name kept in the Scene Info or Model Info; empty before the first export.</summary>
            public string Stored;
            /// <summary>Where names were kept in EditorPrefs before they moved to the component; null when there's none.</summary>
            public string LegacyPrefsKey;
            /// <summary>An exported bundle already using this ID, as a path, or null. Asked once per ID each editor session.</summary>
            public Func<string> FindPublished;
            public string Title;
            /// <summary>Slugged when the title gives nothing, such as the scene's or character's object name.</summary>
            public string Fallback;
            /// <summary>The ID; empty before the first export.</summary>
            public string Id;
            /// <summary>"scene" or "model": the name when neither the title nor the fallback has any Latin letters or digits.</summary>
            public string Kind;
            /// <summary>Keeps a name set by hand in the component, or clears it with null.</summary>
            public Action<string> Store;
        }

        /// <summary>
        /// Lowercase a-z, 0-9 and single hyphens, at most 40 characters. Accents are dropped (é becomes e); other scripts
        /// give nothing, so a title in Japanese falls back to the kind.
        /// </summary>
        public static string Slug(string text)
        {
            var slug = new StringBuilder();
            foreach (char c in (text ?? "").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                char lower = char.ToLowerInvariant(c);
                if (lower is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(lower);
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
            }
            string result = slug.ToString().Trim('-');
            if (result.Length > MaxSlugLength)
            {
                result = result.Substring(0, MaxSlugLength);
                // Cut at a word when there's one in the second half
                int cut = result.LastIndexOf('-');
                if (cut > MaxSlugLength / 2) result = result.Substring(0, cut);
                result = result.Trim('-');
            }
            return result;
        }

        /// <summary>The first 8 letters and digits of the ID, or null without one.</summary>
        private static string IdPart(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var part = new StringBuilder();
            foreach (char c in id.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) part.Append(c);
                if (part.Length == IdChars) break;
            }
            return part.Length > 0 ? part.ToString() : null;
        }

        /// <summary>A new name from the title and ID; with no ID yet, "????????" stands in for its characters.</summary>
        public static string Generate(string title, string fallback, string id, string kind)
        {
            string slug = Slug(title);
            if (slug.Length == 0) slug = Slug(fallback);
            if (slug.Length == 0) slug = kind;
            return $"{slug}-{IdPart(id) ?? PendingId}";
        }

        private static readonly Dictionary<string, string> PublishedById = new();

        /// <summary>
        /// The name to export under, without the extension: the kept one, an older one still in use, or a new one.
        /// pending is true when it's new and there's no ID yet, so it isn't final.
        /// </summary>
        public static string Resolve(Source source, out bool pending)
        {
            pending = false;
            if (!string.IsNullOrEmpty(source.Stored)) return source.Stored;
            if (source.LegacyPrefsKey != null && EditorPrefs.GetString(source.LegacyPrefsKey, "") is { Length: > 0 } legacy) return legacy;
            if (!string.IsNullOrEmpty(source.Id) && source.FindPublished != null)
            {
                if (!PublishedById.TryGetValue(source.Id, out var published))
                    PublishedById[source.Id] = published = source.FindPublished() is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : null;
                if (published != null) return published;
            }
            pending = string.IsNullOrEmpty(source.Id);
            return Generate(source.Title, source.Fallback, source.Id, source.Kind);
        }

        public static string Problem(string name) =>
            name.Length == 0 ? "The file name can't be empty."
            : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0
                ? "The file name can't contain / \\ : * ? \" < > or |."
            : name.EndsWith(".") ? "The file name can't end with a dot."
            : null;

        /// <summary>
        /// Draws the file name as a foldout and returns the name to export under, without the extension. pending is true
        /// while the name still waits for the ID the first export makes.
        /// </summary>
        public static string Field(Source source, string extension, ref bool expanded, out string problem, out bool pending)
        {
            string name = Resolve(source, out pending);
            bool kept = !string.IsNullOrEmpty(source.Stored);
            expanded = EditorGUILayout.Foldout(expanded, new GUIContent($"File name: {name}{extension}",
                "Made from the title and the ID on the first export, then kept, so later title changes don't make a second file"), true);
            if (expanded)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    string edited = EditorGUILayout.DelayedTextField(new GUIContent("File Name", $"Without {extension}. Empty makes one from the title and ID."),
                        name).Trim();
                    if (edited != name)
                    {
                        source.Store(edited.Length > 0 ? edited : null);
                        name = edited.Length > 0 ? edited : Generate(source.Title, source.Fallback, source.Id, source.Kind);
                        pending = edited.Length == 0 && string.IsNullOrEmpty(source.Id);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(pending ? "The ID's first 8 characters are added on the first export."
                            : kept ? "Kept in the Info component." : "Kept on the next export.", EditorStyles.miniLabel);
                        string fresh = Generate(source.Title, source.Fallback, source.Id, source.Kind);
                        using (new EditorGUI.DisabledScope(pending || fresh == name))
                        {
                            if (GUILayout.Button(new GUIContent("From Title", "Make the name again from the current title. Publishing it " +
                                                 "uploads a new file; the one under the old name stays in the bucket until it's removed"), GUILayout.Width(80)))
                            {
                                source.Store(fresh);
                                name = fresh;
                                GUI.FocusControl(null);
                            }
                        }
                    }
                }
            }
            problem = Problem(name);
            if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Error);
            return name;
        }

        [Serializable]
        private class IdOnly
        {
            public string id;
        }

        private static readonly Dictionary<string, (double time, string id)> ExportedIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The ID in the .json beside the bundle at bundlePath, written when it was exported, or null. Read again at most
        /// every two seconds, since the windows ask on every repaint.
        /// </summary>
        public static string ExportedId(string bundlePath)
        {
            string full = Path.GetFullPath(bundlePath);
            double now = EditorApplication.timeSinceStartup;
            if (ExportedIds.TryGetValue(full, out var cached) && now - cached.time <= 2) return cached.id;
            string id = null;
            string json = Path.ChangeExtension(full, ".json");
            if (File.Exists(full) && File.Exists(json))
            {
                try { id = JsonUtility.FromJson<IdOnly>(File.ReadAllText(json))?.id; }
                catch (Exception) { /* Not a sidecar */ }
            }
            ExportedIds[full] = (now, string.IsNullOrEmpty(id) ? null : id);
            return ExportedIds[full].id;
        }

        /// <summary>
        /// Why exporting to bundlePath would replace a different scene or model (one with another ID), or null. noun is
        /// "scene" or "model".
        /// </summary>
        public static string ReplacesOther(string bundlePath, string id, string noun) =>
            !string.IsNullOrEmpty(id) && ExportedId(bundlePath) is { } other && other != id
                ? $"{Path.GetFileName(bundlePath)} is already another {noun} (ID {other}). Pick another file name, or if it's the same " +
                  $"{noun}, give this one that ID."
                : null;
    }
}
