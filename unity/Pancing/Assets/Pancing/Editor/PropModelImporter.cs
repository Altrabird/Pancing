using UnityEditor;
using UnityEngine;

namespace Pancing.EditorTools
{
    /// <summary>
    /// Import rules for the low-poly props in Resources/Models (exported from
    /// art/kolam_props.blend). Colour lives in the vertex colours, so the FBX
    /// materials are dropped, and the meshes stay readable because PropScatter
    /// merges them into one mesh at runtime — CombineMeshes fails on
    /// non-readable meshes in a player build.
    /// </summary>
    public sealed class PropModelImporter : AssetPostprocessor
    {
        /// <summary>
        /// Audio from art/make_audio.py: the long ambience loops stay compressed in
        /// memory (Vorbis), the short effects decompress on load so they fire with
        /// no decode latency — the bite cue cannot afford any.
        /// </summary>
        private void OnPreprocessAudio()
        {
            if (!assetPath.Contains("/Resources/Audio/")) return;
            var importer = (AudioImporter)assetImporter;
            importer.forceToMono = true;
            var s = importer.defaultSampleSettings;
            bool ambience = System.IO.Path.GetFileName(assetPath).StartsWith("amb_");
            s.loadType = ambience ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
            s.compressionFormat = ambience ? AudioCompressionFormat.Vorbis : AudioCompressionFormat.PCM;
            s.quality = 0.55f;
            importer.defaultSampleSettings = s;
        }

        private void OnPreprocessModel()
        {
            if (!assetPath.Contains("/Resources/Models/") && !assetPath.Contains("/Resources/Fish/")) return;
            var importer = (ModelImporter)assetImporter;
            importer.isReadable = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;

            // The angler is skinned. Generic keeps the SkinnedMeshRenderer (None
            // flattens it to a static mesh); no clips, the pose is driven from code
            // (AnglerView). Baking the axis conversion keeps bone frames sane.
            if (assetPath.EndsWith("/angler.fbx"))
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
                importer.bakeAxisConversion = true;
                importer.importBlendShapes = false;
                importer.isReadable = false;
            }
        }
    }
}
