using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Bakes one or more outfits/models into a base avatar, like Modular Avatar's Merge Armature followed by
/// "Manual Bake Avatar".
///
/// Works on copies: the base is duplicated as "&lt;base&gt; (Merged)" and each outfit copy becomes a child of it.
/// Outfit bones whose names match base bones (same path below the matched parent) are merged into the base
/// armature; the rest (skirt, hair, accessories...) are moved under the matching base bone. Skinned meshes and
/// every component reference (NiloToon head bone, colliders lists...) are remapped to the base bones.
///
/// Bones that line up with their base bone are removed outright. Bones that do not (different orientation or
/// position) are either rebound (meshes get copied with corrected bindposes, so the outfit looks the same but
/// is skinned straight to the base bones) or kept as offset bones under the base bone, like MA does.
/// </summary>
public class MergeArmature : EditorWindow
{
    const string DefaultOutputFolder = "Assets/ZZZ_GeneratedAssets/Merged";
    const string LogPrefix = "[MergeArmature]";

    [SerializeField] GameObject baseAvatar;
    [SerializeField] List<GameObject> outfits = new List<GameObject>();
    [SerializeField] string prefix = "";
    [SerializeField] string suffix = "";
    [SerializeField] bool rebindMeshes = true;
    [SerializeField] bool removeOutfitAnimators = true;
    [SerializeField] bool hideOriginals = true;
    [SerializeField] string outputFolder = DefaultOutputFolder;

    SerializedObject serializedWindow;
    Vector2 scroll;

    [MenuItem("BonStudio SDK/Helpers/Avatar Merger")]
    static void Open()
    {
        var window = GetWindow<MergeArmature>("Avatar Merger");
        window.FillFromSelection();
    }

    void FillFromSelection()
    {
        var selected = Selection.gameObjects.Where(g => !EditorUtility.IsPersistent(g)).ToList();
        if (selected.Count == 0)
        {
            // Defaults for Assets/Melody/NormalizeTest.unity.
            baseAvatar = GameObject.Find("/Melody_Body_Face");
            var outfit = GameObject.Find("/Melody_20260803");
            outfits = outfit != null ? new List<GameObject> { outfit } : new List<GameObject>();
            return;
        }

        // The humanoid is the base, everything else is an outfit.
        baseAvatar = selected.FirstOrDefault(IsHumanoid) ?? Selection.activeGameObject;
        outfits = selected.Where(g => g != baseAvatar).ToList();
    }

    void OnGUI()
    {
        if (serializedWindow == null) serializedWindow = new SerializedObject(this);
        serializedWindow.Update();

        EditorGUILayout.HelpBox(
            "Creates a merged copy of the base avatar with the outfits baked in: matching bones are merged, " +
            "meshes and component references are remapped to the base armature. The originals are not modified.",
            MessageType.Info);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.PropertyField(serializedWindow.FindProperty(nameof(baseAvatar)), new GUIContent("Base Avatar"));
        EditorGUILayout.PropertyField(serializedWindow.FindProperty(nameof(outfits)), new GUIContent("Outfits"), true);
        EditorGUILayout.PropertyField(serializedWindow.FindProperty(nameof(prefix)),
            new GUIContent("Outfit Bone Prefix", "Stripped from outfit bone names before matching (like MA's prefix)."));
        EditorGUILayout.PropertyField(serializedWindow.FindProperty(nameof(suffix)),
            new GUIContent("Outfit Bone Suffix", "Stripped from outfit bone names before matching (like MA's suffix)."));
        EditorGUILayout.PropertyField(serializedWindow.FindProperty(nameof(rebindMeshes)),
            new GUIContent("Rebind Meshes",
                "Matched bones that don't line up with the base bone are removed and the outfit meshes are copied " +
                "with corrected bindposes. Off: such bones are kept as offset bones under the base bone instead."));
        EditorGUILayout.PropertyField(serializedWindow.FindProperty(nameof(removeOutfitAnimators)),
            new GUIContent("Remove Outfit Animators", "Animators on outfit roots would fight the base Animator."));
        EditorGUILayout.PropertyField(serializedWindow.FindProperty(nameof(hideOriginals)));
        EditorGUILayout.PropertyField(serializedWindow.FindProperty(nameof(outputFolder)));
        EditorGUILayout.EndScrollView();
        serializedWindow.ApplyModifiedProperties();

        if (GUILayout.Button("Use Selection")) FillFromSelection();

        string error = Validate();
        if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);

        using (new EditorGUI.DisabledScope(error != null))
        {
            if (GUILayout.Button("Merge", GUILayout.Height(30)))
            {
                Merge(baseAvatar, outfits.Where(o => o != null).ToList(), new Options
                {
                    prefix = prefix,
                    suffix = suffix,
                    rebindMeshes = rebindMeshes,
                    removeOutfitAnimators = removeOutfitAnimators,
                    hideOriginals = hideOriginals,
                    outputFolder = outputFolder,
                });
            }
        }
    }

    string Validate()
    {
        if (baseAvatar == null) return "Set a base avatar.";
        if (EditorUtility.IsPersistent(baseAvatar)) return "Use a base avatar instance in the scene, not an asset.";
        var valid = outfits.Where(o => o != null).ToList();
        if (valid.Count == 0) return "Add at least one outfit.";
        foreach (var outfit in valid)
        {
            if (EditorUtility.IsPersistent(outfit)) return $"'{outfit.name}' is an asset. Use an instance in the scene.";
            if (outfit == baseAvatar) return "The base avatar can't be one of its outfits.";
            if (outfit.transform.IsChildOf(baseAvatar.transform) || baseAvatar.transform.IsChildOf(outfit.transform))
                return $"'{outfit.name}' and the base avatar must be separate hierarchies.";
        }
        return null;
    }

    static bool IsHumanoid(GameObject go)
    {
        var animator = go.GetComponent<Animator>();
        return animator != null && animator.avatar != null && animator.avatar.isHuman;
    }

    public struct Options
    {
        public string prefix;
        public string suffix;
        public bool rebindMeshes;
        public bool removeOutfitAnimators;
        public bool hideOriginals;
        public string outputFolder;
    }

    public static GameObject Merge(GameObject baseSource, List<GameObject> outfitSources, Options options)
    {
        var result = Instantiate(baseSource, baseSource.transform.parent);
        result.name = baseSource.name + " (Merged)";
        result.transform.SetSiblingIndex(baseSource.transform.GetSiblingIndex() + 1);
        result.SetActive(true);

        string assetFolder = $"{options.outputFolder}/{baseSource.name}";
        foreach (var outfitSource in outfitSources)
        {
            var outfit = Instantiate(outfitSource, outfitSource.transform.parent);
            outfit.name = outfitSource.name;
            outfit.SetActive(true);
            outfit.transform.SetParent(result.transform, true);
            MergeOutfit(result.transform, outfit.transform, options, assetFolder);
        }
        AssetDatabase.SaveAssets();

        Undo.RegisterCreatedObjectUndo(result, "Merge Avatar");
        if (options.hideOriginals)
        {
            foreach (var go in outfitSources.Append(baseSource))
            {
                Undo.RecordObject(go, "Merge Avatar");
                go.SetActive(false);
            }
        }

        Selection.activeGameObject = result;
        return result;
    }

    static void MergeOutfit(Transform baseRoot, Transform outfitRoot, Options options, string assetFolder)
    {
        if (options.removeOutfitAnimators)
        {
            foreach (var animator in outfitRoot.GetComponentsInChildren<Animator>(true)) DestroyImmediate(animator);
        }

        // Outfit bone -> base bone, parents before children.
        var matches = new List<(Transform outfit, Transform bone)>();
        MatchChildren(outfitRoot, baseRoot, options, matches);

        // Fallback for outfits whose armature isn't laid out like the base's: start from the hips.
        var baseAnimator = baseRoot.GetComponent<Animator>();
        var baseHips = baseAnimator != null && baseAnimator.isHuman ? baseAnimator.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (baseHips != null && matches.All(m => m.bone != baseHips))
        {
            var outfitHips = outfitRoot.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t != outfitRoot && StripName(t.name, options) == baseHips.name && !HasRenderer(t));
            if (outfitHips != null)
            {
                matches.Add((outfitHips, baseHips));
                MatchChildren(outfitHips, baseHips, options, matches);
            }
        }

        if (matches.Count == 0)
        {
            Debug.LogWarning($"{LogPrefix} No bones of '{outfitRoot.name}' match the base armature. " +
                             "Check the prefix/suffix. The outfit was added as is.", outfitRoot);
            return;
        }

        // Decide what happens to each matched bone.
        var removed = new Dictionary<Transform, Transform>();   // outfit bone -> base bone that replaces it
        var offsets = new Dictionary<Transform, Matrix4x4>();   // outfit bone in its base bone's space
        var kept = new List<(Transform outfit, Transform bone)>();
        foreach (var (outfit, bone) in matches)
        {
            var offset = bone.worldToLocalMatrix * outfit.localToWorldMatrix;
            bool aligned = IsAligned(outfit, bone);
            bool hasComponents = outfit.GetComponents<Component>().Length > 1;
            if (aligned || (options.rebindMeshes && !hasComponents))
            {
                removed[outfit] = bone;
                offsets[outfit] = offset;
            }
            else
            {
                kept.Add((outfit, bone));
            }
        }

        // Re-parent everything that stays, before any removed bone is destroyed. World poses are kept.
        var unmatchedChildren = outfitRoot.GetComponentsInChildren<Transform>(true)
            .Where(t => t.parent != null && removed.ContainsKey(t.parent) && !removed.ContainsKey(t) && kept.All(k => k.outfit != t))
            .ToList();
        foreach (var (outfit, bone) in kept)
        {
            outfit.SetParent(bone, true);
            outfit.name = $"{outfit.name} ({outfitRoot.name})";
        }
        foreach (var t in unmatchedChildren) t.SetParent(removed[t.parent], true);

        // Outfit bone -> base bone, plus moved components, for the reference remap below.
        var remap = new Dictionary<Object, Object>();
        foreach (var pair in removed)
        {
            remap[pair.Key] = pair.Value;
            remap[pair.Key.gameObject] = pair.Value.gameObject;
            foreach (var component in pair.Key.GetComponents<Component>())
            {
                if (component is Transform) continue;
                if (ComponentUtility.CopyComponent(component) && ComponentUtility.PasteComponentAsNew(pair.Value.gameObject))
                {
                    var components = pair.Value.GetComponents(component.GetType());
                    remap[component] = components[components.Length - 1];
                }
                else
                {
                    Debug.LogWarning($"{LogPrefix} Couldn't move {component.GetType().Name} from '{pair.Key.name}' to '{pair.Value.name}'.", pair.Value);
                }
            }
        }

        int rebound = RebindMeshes(outfitRoot, removed, offsets, assetFolder);
        RemapReferences(baseRoot, remap);

        foreach (var outfit in removed.Keys)
        {
            if (outfit != null) DestroyImmediate(outfit.gameObject);
        }

        Debug.Log($"{LogPrefix} Merged '{outfitRoot.name}': {removed.Count} bones merged, {kept.Count} kept as offset bones, " +
                  $"{unmatchedChildren.Count} transforms moved to the base armature, {rebound} meshes rebound" +
                  (rebound > 0 ? $" (saved to {assetFolder})." : "."), outfitRoot);
    }

    // Matches the children of two matched transforms by name, recursively.
    static void MatchChildren(Transform outfit, Transform bone, Options options, List<(Transform, Transform)> matches)
    {
        foreach (Transform child in outfit)
        {
            if (HasRenderer(child)) continue;
            string name = StripName(child.name, options);
            Transform match = null;
            foreach (Transform candidate in bone)
            {
                if (candidate.name == name && !HasRenderer(candidate)) { match = candidate; break; }
            }
            if (match == null) continue;
            matches.Add((child, match));
            MatchChildren(child, match, options, matches);
        }
    }

    static string StripName(string name, Options options)
    {
        if (!string.IsNullOrEmpty(options.prefix) && name.StartsWith(options.prefix)) name = name.Substring(options.prefix.Length);
        if (!string.IsNullOrEmpty(options.suffix) && name.EndsWith(options.suffix)) name = name.Substring(0, name.Length - options.suffix.Length);
        return name;
    }

    // Mesh objects are never merged, even if a base mesh has the same name ("Body"...).
    static bool HasRenderer(Transform t) => t.GetComponent<Renderer>() != null;

    static bool IsAligned(Transform a, Transform b)
    {
        return (a.position - b.position).sqrMagnitude < 1e-8f
               && Quaternion.Angle(a.rotation, b.rotation) < 0.05f
               && (a.lossyScale - b.lossyScale).sqrMagnitude < 1e-6f;
    }

    // Points outfit meshes at the base bones. Bones that weren't aligned get corrected bindposes, which
    // needs a copy of the mesh. Returns the number of meshes copied.
    static int RebindMeshes(Transform outfitRoot, Dictionary<Transform, Transform> removed,
        Dictionary<Transform, Matrix4x4> offsets, string assetFolder)
    {
        int count = 0;
        var meshes = new List<(Mesh source, Matrix4x4[] bindposes, Mesh mesh)>();
        foreach (var renderer in outfitRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var bones = renderer.bones;
            var sourceMesh = renderer.sharedMesh;
            var bindposes = sourceMesh != null ? sourceMesh.bindposes : new Matrix4x4[0];
            bool changed = false;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || !removed.TryGetValue(bones[i], out var bone)) continue;
                // Vertices must stay at boneLocalToWorld * bindpose: newBindpose = offset * bindpose.
                if (i < bindposes.Length && !offsets[bones[i]].isIdentity && !IsAligned(bones[i], bone))
                {
                    bindposes[i] = offsets[bones[i]] * bindposes[i];
                    changed = true;
                }
                bones[i] = bone;
            }

            if (changed)
            {
                // Renderers sharing a mesh and its new bindposes share the copy.
                var existing = meshes.FirstOrDefault(m => m.source == sourceMesh && m.bindposes.SequenceEqual(bindposes));
                var mesh = existing.mesh;
                if (mesh == null)
                {
                    EnsureFolder(assetFolder);
                    mesh = Instantiate(sourceMesh);
                    mesh.name = sourceMesh.name + " (Merged)";
                    mesh.bindposes = bindposes;
                    AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath($"{assetFolder}/{sourceMesh.name}.asset"));
                    meshes.Add((sourceMesh, bindposes, mesh));
                    count++;
                }
                renderer.sharedMesh = mesh;
            }
            renderer.bones = bones;

            // Bounds are in the root bone's space.
            var rootBone = renderer.rootBone;
            if (rootBone != null && removed.TryGetValue(rootBone, out var newRoot))
            {
                renderer.localBounds = TransformBounds(offsets[rootBone], renderer.localBounds);
                renderer.rootBone = newRoot;
            }
        }
        return count;
    }

    // Replaces references to removed outfit bones (and their moved components) in every component of the avatar.
    static void RemapReferences(Transform root, Dictionary<Object, Object> remap)
    {
        foreach (var component in root.GetComponentsInChildren<Component>(true))
        {
            // Components on removed bones are destroyed with them.
            if (component == null || component is Transform || remap.ContainsKey(component.gameObject)) continue;
            var so = new SerializedObject(component);
            var property = so.GetIterator();
            bool changed = false;
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_GameObject") continue;
                var value = property.objectReferenceValue;
                if (value != null && remap.TryGetValue(value, out var replacement))
                {
                    property.objectReferenceValue = replacement;
                    changed = true;
                }
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
    {
        var result = new Bounds(matrix.MultiplyPoint3x4(bounds.center), Vector3.zero);
        for (int i = 0; i < 8; i++)
        {
            var corner = bounds.center + Vector3.Scale(bounds.extents,
                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            result.Encapsulate(matrix.MultiplyPoint3x4(corner));
        }
        return result;
    }

    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
}
