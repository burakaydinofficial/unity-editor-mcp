using System.Runtime.InteropServices;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    /// <summary>
    /// HostPlatformInfo classifies the OS without RuntimeInformation.IsOSPlatform. On a real .NET test runner
    /// RuntimeInformation is fully supported, so it serves as an independent oracle: the two MUST agree here,
    /// which pins the new detector to the platform truth.
    /// </summary>
    public class HostPlatformInfoTests
    {
        [Fact]
        public void Detect_AgreesWithRuntimeInformation_OnThisRunner()
        {
            HostPlatform expected;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) expected = HostPlatform.Windows;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) expected = HostPlatform.MacOS;
            else expected = HostPlatform.Other;

            Assert.Equal(expected, HostPlatformInfo.Detect());
            Assert.Equal(HostPlatformInfo.Detect(), HostPlatformInfo.Current);
        }

        [Fact]
        public void IsCaseInsensitiveFileSystem_MatchesWindowsOrMac()
        {
            Assert.Equal(HostPlatformInfo.Current != HostPlatform.Other, HostPlatformInfo.IsCaseInsensitiveFileSystem);
        }
    }
}
