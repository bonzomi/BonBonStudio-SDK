using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BonStudio.SDK.Characters
{
    /// <summary>
    /// Normalizes the exported copy's bones when its Model Info asks for it. Runs after Modular Avatar, which needs the
    /// bones where its Merge Armature and Bone Proxy setups expect them, so the scene is never changed.
    ///
    /// Normalizing moves the original bones under new ones, so animations that address something under the armature by
    /// path (a tail bone's rotation, an object parented to a bone) would lose it. Clips that do are copied with their
    /// paths pointed at where things moved, and the controllers playing them get an override controller swapping in the
    /// copies. Humanoid muscle animation goes through the Avatar and needs nothing. The copies, the overrides and the new
    /// Avatar are temporary assets, deleted once the bundle is built.
    /// </summary>
    internal class NormalizeBonesCallback : IPreprocessCharacterCallback
    {
        // After Modular Avatar (-11000) and EditorOnly stripping (-1024), before Poiyomi locks materials (100)
        public int callbackOrder => 0;

        const string TempRoot = "Assets/ZZZ_GeneratedAssets/BonStudioExport";

        public bool OnPreprocessCharacter(GameObject character)
        {
            var info = character.GetComponent<ModelInfo>();
            if (!info || !info.normalizeBonesOnExport) return true;
            // Null: no humanoid Avatar to normalize against; true: already normalized
            if (BoneNormalizer.HumanoidBonesNormalized(character, out _, out _) != false) return true;

            BoneNormalizer.EnsureFolder(TempRoot);
            string folder = AssetDatabase.GenerateUniqueAssetPath($"{TempRoot}/Normalize");
            BoneNormalizer.EnsureFolder(folder);
            CharacterBuildPipeline.AfterBuild(() =>
            {
                AssetDatabase.DeleteAsset(folder);
                if (AssetDatabase.IsValidFolder(TempRoot) && AssetDatabase.FindAssets("", new[] { TempRoot }).Length == 0)
                    AssetDatabase.DeleteAsset(TempRoot);
            });

            string error = BoneNormalizer.NormalizeInPlace(character, $"{folder}/{character.name} Normalized Avatar.asset", out var oldPaths);
            if (error != null) throw new CharacterPreprocessException($"Couldn't normalize {character.name}'s bones: {error}");
            new AnimationPathRemap(character.transform, oldPaths, folder).Apply();
            return true;
        }
    }

    /// <summary>Points the animations a character plays at where its transforms moved, through copies.</summary>
    internal class AnimationPathRemap
    {
        readonly Transform root;
        readonly Dictionary<Transform, string> oldPaths;
        readonly Dictionary<Transform, string> newPaths;
        readonly string folder;
        readonly Dictionary<Transform, Dictionary<string, string>> mapsByBase = new();
        readonly Dictionary<(Object, Transform), Object> copies = new();

        public AnimationPathRemap(Transform root, Dictionary<Transform, string> oldPaths, string folder)
        {
            this.root = root;
            this.oldPaths = oldPaths;
            this.folder = folder;
            newPaths = oldPaths.Keys.Where(t => t).ToDictionary(t => t, t => AnimationUtility.CalculateTransformPath(t, root));
        }

        public void Apply()
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component || component is Transform) continue;
                // An Animator's clips address paths from its own object; controllers and clips held anywhere else (such as
                // the Model Descriptor's) are played on the model
                var animationBase = component is Animator ? component.transform : root;
                var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                bool changed = false;
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    Object replacement = value switch
                    {
                        RuntimeAnimatorController controller => Remapped(controller, animationBase),
                        AnimationClip clip => Remapped(clip, animationBase),
                        _ => value,
                    };
                    if (replacement == value) continue;
                    property.objectReferenceValue = replacement;
                    changed = true;
                }
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // Old path to new, relative to animationBase, for the transforms that moved
        Dictionary<string, string> MapFor(Transform animationBase)
        {
            if (mapsByBase.TryGetValue(animationBase, out var map)) return map;
            map = new Dictionary<string, string>();
            if (oldPaths.TryGetValue(animationBase, out var baseOld) && newPaths.TryGetValue(animationBase, out var baseNew))
            {
                foreach (var pair in newPaths)
                {
                    string oldRelative = Relative(oldPaths[pair.Key], baseOld);
                    string newRelative = Relative(pair.Value, baseNew);
                    if (oldRelative != null && newRelative != null && oldRelative != newRelative) map[oldRelative] = newRelative;
                }
            }
            return mapsByBase[animationBase] = map;
        }

        static string Relative(string path, string basePath) =>
            basePath.Length == 0 ? path
            : path == basePath ? ""
            : path.StartsWith(basePath + "/") ? path.Substring(basePath.Length + 1)
            : null;

        RuntimeAnimatorController Remapped(RuntimeAnimatorController controller, Transform animationBase)
        {
            if (copies.TryGetValue((controller, animationBase), out var done)) return (RuntimeAnimatorController)done;
            RuntimeAnimatorController result = controller;
            if (MapFor(animationBase).Count > 0)
            {
                // An override's own overrides carry over onto a new override of the same base, with remapped clips swapped in
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                RuntimeAnimatorController baseController;
                if (controller is AnimatorOverrideController existing)
                {
                    baseController = existing.runtimeAnimatorController;
                    existing.GetOverrides(overrides);
                }
                else
                {
                    baseController = controller;
                    overrides.AddRange(controller.animationClips.Distinct().Select(clip => new KeyValuePair<AnimationClip, AnimationClip>(clip, null)));
                }

                bool changed = false;
                for (int i = 0; i < overrides.Count; i++)
                {
                    var played = overrides[i].Value ? overrides[i].Value : overrides[i].Key;
                    if (!played) continue;
                    var remapped = Remapped(played, animationBase);
                    if (remapped == played) continue;
                    overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(overrides[i].Key, remapped);
                    changed = true;
                }

                if (changed && baseController)
                {
                    var swap = new AnimatorOverrideController(baseController) { name = controller.name + " (Normalized)" };
                    swap.ApplyOverrides(overrides);
                    AssetDatabase.CreateAsset(swap, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Safe(swap.name)}.overrideController"));
                    result = swap;
                }
            }
            copies[(controller, animationBase)] = result;
            return result;
        }

        AnimationClip Remapped(AnimationClip clip, Transform animationBase)
        {
            if (copies.TryGetValue((clip, animationBase), out var done)) return (AnimationClip)done;
            var map = MapFor(animationBase);
            var curves = AnimationUtility.GetCurveBindings(clip);
            var references = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            AnimationClip result = clip;
            if (curves.Concat(references).Any(binding => map.ContainsKey(binding.path)))
            {
                result = Object.Instantiate(clip);
                result.name = clip.name;
                foreach (var binding in curves)
                {
                    if (!map.TryGetValue(binding.path, out var path)) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    AnimationUtility.SetEditorCurve(result, binding, null);
                    var moved = binding;
                    moved.path = path;
                    AnimationUtility.SetEditorCurve(result, moved, curve);
                }
                foreach (var binding in references)
                {
                    if (!map.TryGetValue(binding.path, out var path)) continue;
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    AnimationUtility.SetObjectReferenceCurve(result, binding, null);
                    var moved = binding;
                    moved.path = path;
                    AnimationUtility.SetObjectReferenceCurve(result, moved, keys);
                }
                AssetDatabase.CreateAsset(result, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Safe(clip.name)}.anim"));
            }
            copies[(clip, animationBase)] = result;
            return result;
        }

        static string Safe(string name) =>
            string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
