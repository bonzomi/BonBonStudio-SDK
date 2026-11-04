using System;
using System.IO;
using BonStudio.SDK.Characters;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Keeps the ID read-only, so it only changes on purpose; the title and description are edited in the export window
[CustomEditor(typeof(ModelInfo))]
internal class ModelInfoEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var info = (ModelInfo)target;
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(new GUIContent("ID", "Made on the first export; stays the same through renames"), info.id);
            if (GUILayout.Button("New ID", GUILayout.Width(60))) ModelInfoUtility.ConfirmNewId(info);
        }
        if (ModelInfoUtility.IsCopy(info))
            EditorGUILayout.HelpBox("This character looks like a copy of another, so both share this ID. Give it a new ID.", MessageType.Warning);

        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ModelInfo.normalizeBonesOnExport)));
        serializedObject.ApplyModifiedProperties();
    }
}

internal static class ModelInfoUtility
{
    public const int MaxTitleLength = 64;
    public const int MaxDescriptionLength = 500;

    // What's written beside the exported bundle, for the publish script and the app's model list
    [Serializable]
    private class Sidecar
    {
        public string id;
        public string title;
        public string description;
        // URP, HDRP or BRP; see BundleRenderPipeline
        public string renderPipeline;
    }

    public static string NewId() => Guid.NewGuid().ToString("D");

    // The object the ID belongs to: the prefab the character comes from, or the character itself
    public static string OwnerOf(ModelInfo info)
    {
        Object home = PrefabUtility.IsPartOfPrefabInstance(info) ? PrefabUtility.GetCorrespondingObjectFromSource(info) : null;
        var id = GlobalObjectId.GetGlobalObjectIdSlow(home ? home : info);
        // A scene that was never saved has no GUID yet, so it can't be told apart from a copy
        return id.assetGUID.Empty() ? null : id.ToString();
    }

    public static bool IsCopy(ModelInfo info) =>
        !string.IsNullOrEmpty(info.id) && !string.IsNullOrEmpty(info.owner) && OwnerOf(info) is { } owner && owner != info.owner;

    public static void ConfirmNewId(ModelInfo info)
    {
        if (!string.IsNullOrEmpty(info.id) && !IsCopy(info) && !EditorUtility.DisplayDialog("New Model ID",
                "The app will treat the next export as a different model from the one already published. Make a new ID?",
                "New ID", "Cancel")) return;
        Undo.RecordObject(info, "New Model ID");
        info.id = NewId();
        info.owner = OwnerOf(info);
        EditorUtility.SetDirty(info);
    }

    public static void WriteSidecar(string bundlePath, string id, string title, string description, string renderPipeline) =>
        File.WriteAllText(Path.ChangeExtension(bundlePath, ".json"),
            JsonUtility.ToJson(new Sidecar { id = id, title = title, description = description, renderPipeline = renderPipeline }, true) + "\n");

    /// <summary>The file name of another bundle in the folder whose .json has this ID, or null.</summary>
    public static string IdTakenBy(string bundlePath, string id)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(bundlePath));
        if (string.IsNullOrEmpty(id) || !Directory.Exists(folder)) return null;
        string own = Path.GetFullPath(bundlePath);
        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            string bundle = Path.ChangeExtension(file, Path.GetExtension(bundlePath));
            if (!File.Exists(bundle) || string.Equals(Path.GetFullPath(bundle), own, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (JsonUtility.FromJson<Sidecar>(File.ReadAllText(file))?.id == id) return Path.GetFileName(bundle);
            }
            catch (Exception)
            {
                // Not a sidecar
            }
        }
        return null;
    }
}
