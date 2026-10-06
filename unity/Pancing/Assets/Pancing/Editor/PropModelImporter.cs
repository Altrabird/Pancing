using UnityEditor;

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
        private void OnPreprocessModel()
        {
            if (!assetPath.Contains("/Resources/Models/")) return;
            var importer = (ModelImporter)assetImporter;
            importer.isReadable = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
        }
    }
}
