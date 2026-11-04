# BonBonStudio SDK

Tools for making characters for [BonStudio](https://www.bonzomi.net/) in Unity. Set your content
up with BonStudio's components, export it from the BonStudio SDK menu, and load the exported file into BonStudio.

> BonStudio and the BonStudio SDK are not affiliated with, endorsed by or supported by VRChat Inc.

## Requirements

- Unity 2022.3 or later
- URP is supported but not required, NiloToon and PotaToon are both supported.

## Installing

1. In the Unity Editor, select Window > Package Management > Package Manager.
2. Select top left '+' icon, 'Install package from git URL...', then enter 'https://github.com/bonzomi/BonBonStudio-SDK.git'

## Characters

1. Put your model in a scene.
2. Open **BonStudio SDK > Export Character Model**, pick the model and export.
   - The first export adds a **Model Info** component holding the model's ID, title and description. Keep it on the
     model: BonStudio uses the ID to recognise the model across exports and renames.
   - For humanoid models the window checks whether the bones are normalized (all sharing the model's orientation).
     If they aren't, **Normalize Bones on Export** normalizes them in the exported file, after Modular Avatar has run.
     The scene isn't changed, and animations of bones and objects under the armature are pointed at where they moved.
     The setting is saved in the model's Model Info.
3. Click 'Build Character' and export the .vsm file.

Modular Avatar and other NDMF tools are applied during export, the way VRChat's build applies them, and Poiyomi
materials are locked. EditorOnly objects are left out.

## Helpers

Under **BonStudio SDK > Helpers**:

- **Avatar Merger**: bakes outfits into a base avatar on copies, like Modular Avatar's Merge Armature followed by a
  manual bake.
- **Bone Normalizer**: gives a copy of a model an armature whose bones all share the root's orientation, without
  touching its meshes (also on **GameObject > Normalize Bones**).


## License

MIT. See [LICENSE.md](LICENSE.md).
