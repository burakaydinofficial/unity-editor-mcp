using System;
using System.IO;

namespace UnityEditorMCP.Core
{
    /// <summary>The host OS, for filesystem conventions (the registry directory and path-case comparison).</summary>
    public enum HostPlatform { Windows, MacOS, Other }

    /// <summary>
    /// Reliable host-OS classification shared by the discovery-registry directory (<see cref="InstanceRegistry"/>)
    /// and path-containment case handling (<see cref="PathContainment"/>). The registry directory must match the
    /// Node side (<c>process.platform</c>) exactly, so this uses only primitives that behave the same on every Unity
    /// Mono down to the 2019.4 floor (<c>Path.DirectorySeparatorChar</c>, <c>Environment.OSVersion.Platform</c>).
    /// </summary>
    public static class HostPlatformInfo
    {
        // The OS can't change within a process: detect once (Detect() probes the filesystem off Windows).
        private static readonly HostPlatform CurrentPlatform = Detect();

        /// <summary>The current host platform (detected once per process).</summary>
        public static HostPlatform Current => CurrentPlatform;

        /// <summary>Classifies the host OS without RuntimeInformation (see the type remarks).</summary>
        public static HostPlatform Detect()
        {
            if (Path.DirectorySeparatorChar == '\\' || Environment.OSVersion.Platform == PlatformID.Win32NT)
                return HostPlatform.Windows;
            // OSVersion.Platform reports Unix for both macOS and Linux; a macOS-only system path disambiguates.
            if (Directory.Exists("/System/Library/CoreServices"))
                return HostPlatform.MacOS;
            return HostPlatform.Other;
        }

        /// <summary>True where the OS filesystem folds case (Windows + macOS); false on Linux.</summary>
        public static bool IsCaseInsensitiveFileSystem => CurrentPlatform != HostPlatform.Other;
    }
}
