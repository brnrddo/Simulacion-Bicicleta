// ============================================================
// RocketboxImportPostprocessor.cs
// Ajustes de importacion para los avatares Microsoft Rocketbox (licencia MIT,
// Gonzalez-Franco et al., 2020) en Assets/ThirdParty/Rocketbox:
//   - texturas a 1024 px (originales 4096; suficiente para peatones en VR y
//     mucho mas liviano), mapas de normales como NormalMap
//   - rig Generic (esqueleto comun Bip01); los modelos no importan animaciones
//   - animaciones (caminar, idle) en bucle. Las de caminar NO son en el lugar
//     (Bip01 avanza 1.2-1.6 m por ciclo): PedestrianSetupEditor genera copias
//     en el lugar (InPlaceWalkClip) que son las que usan los controladores.
// ============================================================

using UnityEditor;

public class RocketboxImportPostprocessor : AssetPostprocessor
{
    private const string Root = "Assets/ThirdParty/Rocketbox/";

    private void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(Root)) return;
        var ti = (TextureImporter)assetImporter;
        ti.maxTextureSize = 1024;
        ti.mipmapEnabled = true;
        string lower = assetPath.ToLowerInvariant();
        if (lower.Contains("_normal")) ti.textureType = TextureImporterType.NormalMap;
        if (lower.Contains("_opacity")) ti.alphaIsTransparency = true;
    }

    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Root)) return;
        var mi = (ModelImporter)assetImporter;
        bool isAnimation = assetPath.Contains("/Animations/");
        mi.animationType = ModelImporterAnimationType.Generic;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.importAnimation = isAnimation;
        mi.importCameras = false;
        mi.importLights = false;
        mi.importBlendShapes = !isAnimation;
        mi.motionNodeName = "";
        if (isAnimation) mi.materialImportMode = ModelImporterMaterialImportMode.None;
    }

    private void OnPreprocessAnimation()
    {
        if (!assetPath.StartsWith(Root) || !assetPath.Contains("/Animations/")) return;
        var mi = (ModelImporter)assetImporter;
        var clips = mi.defaultClipAnimations;
        foreach (var c in clips)
        {
            c.loopTime = true;
        }
        mi.clipAnimations = clips;
    }
}
