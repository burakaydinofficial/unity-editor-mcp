using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// E2E harness probe: mirrors play-mode lifecycle to a JSONL file the Node harness reads.
// Re-subscribes on every domain reload via [InitializeOnLoad], so events that straddle a reload are still captured.
// No-op unless the E2E_PROBE_FILE env var is set, so a human opening the project is unaffected.
[InitializeOnLoad]
public static class E2EProbe
{
    static readonly string ProbeFile = Environment.GetEnvironmentVariable("E2E_PROBE_FILE");

    static E2EProbe()
    {
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.pauseStateChanged += OnPause;
        Append("{\"event\":\"probeLoaded\"}");
    }

    static void OnPlayMode(PlayModeStateChange s)
    {
        // s is one of EnteredPlayMode / ExitingPlayMode / EnteredEditMode / ExitingEditMode — stable enum names.
        Append("{\"event\":\"" + s + "\"}");
    }

    static void OnPause(PauseState s)
    {
        Append("{\"event\":\"pause\",\"paused\":" + (s == PauseState.Paused ? "true" : "false") + "}");
    }

    static void Append(string line)
    {
        if (string.IsNullOrEmpty(ProbeFile)) return;
        try { File.AppendAllText(ProbeFile, line + "\n"); }
        catch { /* never let the probe break the editor */ }
    }
}
