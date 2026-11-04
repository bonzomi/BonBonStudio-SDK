using UnityEngine;

namespace BonStudio.SDK.Characters
{
    /// <summary>
    /// What BonStudio knows an exported model by. Saved on the character, so it's in version control, the same on every
    /// machine and inside the bundle. The Export Character Model window adds it on the first export and edits it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("BonStudio/Model Info")]
    public class ModelInfo : MonoBehaviour
    {
        [Tooltip("A UUID made on the first export. It stays the same through renames, so the app can tell it's the same model.")]
        public string id;
        [Tooltip("What players see in the model list. Empty uses the bundle's name.")]
        public string title;
        [TextArea(2, 6)]
        public string description;
        [Tooltip("Normalize a humanoid model's bones in the exported file, after Modular Avatar has run, so they all share the " +
                 "model's orientation. The scene isn't changed.")]
        public bool normalizeBonesOnExport;
        // The exported file's name without its extension, made on the first export and kept so the file stays the same
        // through title changes and on every machine. Edited in the export window.
        [HideInInspector]
        public string fileName;
        // Which object the ID was made on; a different one means the character was duplicated, ID and all
        [HideInInspector]
        public string owner;
    }
}
