// ============================================================
// AudioSetupEditor.cs
// SimBici > Build Audio Setup (2026-11-11): importacion de las grabaciones y objeto
// "Audio" de la escena con TrafficAudio (ver TrafficAudio.cs).
//   - Ambiente (2 min, estereo): Streaming, Vorbis; no ocupa memoria al cargarse.
//   - Motor (28 s, mono): Decompress On Load (se reproduce en varias voces a la vez).
// El espacializador del proyecto debe ser "Meta XR Audio" (lo deja el paquete
// com.meta.xr.sdk.audio); se revisa y se avisa si no.
// ============================================================

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AudioSetupEditor
{
    private const string AmbientPath = "Assets/ThirdParty/Audio/AmbienteCiudad/ambiente_ciudad_loop.wav";
    private const string EnginePath = "Assets/ThirdParty/Audio/MotorAuto/motor_auto_ralenti_loop.wav";
    public const string RootName = "Audio";

    [MenuItem("SimBici/Build Audio Setup")]
    private static void Menu() => Debug.Log(Build());

    public static string Build()
    {
        if (EditorApplication.isPlaying) return "[Audio] Salir de Play antes de generar.";
        var log = new System.Text.StringBuilder("[Audio]\n");

        Import(AmbientPath, AudioClipLoadType.Streaming, 0.6f, false, log);
        Import(EnginePath, AudioClipLoadType.DecompressOnLoad, 0.8f, true, log);

        var old = GameObject.Find(RootName);
        if (old != null) Undo.DestroyObjectImmediate(old);
        var go = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(go, RootName);
        var ta = go.AddComponent<TrafficAudio>();
        var so = new SerializedObject(ta);
        so.FindProperty("ambientClip").objectReferenceValue = AssetDatabase.LoadAssetAtPath<AudioClip>(AmbientPath);
        so.FindProperty("engineClip").objectReferenceValue = AssetDatabase.LoadAssetAtPath<AudioClip>(EnginePath);
        so.ApplyModifiedPropertiesWithoutUndo();
        log.AppendLine("  Objeto 'Audio' con TrafficAudio creado");

        string spat = AudioSettings.GetSpatializerPluginName();
        log.AppendLine(spat == "Meta XR Audio" ? "  Espacializador: Meta XR Audio"
                                              : $"  AVISO: el espacializador es '{spat}'; elegir 'Meta XR Audio' en Project Settings > Audio");
        if (Object.FindFirstObjectByType<AudioListener>() == null) log.AppendLine("  AVISO: no hay AudioListener en la escena");

        EditorSceneManager.MarkSceneDirty(go.scene);
        return log.ToString();
    }

    private static void Import(string path, AudioClipLoadType load, float quality, bool mono, System.Text.StringBuilder log)
    {
        var imp = AssetImporter.GetAtPath(path) as AudioImporter;
        if (imp == null) { log.AppendLine($"  AVISO: no se encontro {path}"); return; }
        var s = imp.defaultSampleSettings;
        s.loadType = load;
        s.compressionFormat = AudioCompressionFormat.Vorbis;
        s.quality = quality;
        s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
        imp.defaultSampleSettings = s;
        imp.forceToMono = mono;
        imp.loadInBackground = load == AudioClipLoadType.Streaming;
        imp.SaveAndReimport();
        log.AppendLine($"  {System.IO.Path.GetFileName(path)}: {load}, Vorbis {quality:F1}{(mono ? ", mono" : "")}");
    }
}
