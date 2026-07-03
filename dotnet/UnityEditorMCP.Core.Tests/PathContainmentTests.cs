using System;
using System.IO;
using UnityEditorMCP.Core;
using Xunit;

namespace UnityEditorMCP.Core.Tests
{
    public class PathContainmentTests
    {
        // A rooted, OS-appropriate project root (GetTempPath is absolute), so GetFullPath is deterministic.
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "uemcp-proj");

        [Fact] public void InProject_Relative_Allowed() => Assert.True(PathContainment.IsWithin(Root, "Assets/Foo.cs"));

        [Fact] public void InProject_Absolute_Allowed() => Assert.True(PathContainment.IsWithin(Root, Path.Combine(Root, "Library", "x")));

        [Fact] public void InProject_NonAssetsFolder_Allowed() => Assert.True(PathContainment.IsWithin(Root, "ProjectSettings/EditorSettings.asset"));

        [Fact] public void RelativeTraversal_BackInside_Allowed() => Assert.True(PathContainment.IsWithin(Root, "Assets/../Library/x")); // .. that stays inside is fine

        [Fact] public void RootItself_Allowed() => Assert.True(PathContainment.IsWithin(Root, Root));

        [Fact] public void Traversal_Escape_Denied() => Assert.False(PathContainment.IsWithin(Root, "../outside.txt"));

        [Fact] public void Absolute_Escape_Denied() => Assert.False(PathContainment.IsWithin(Root, Path.Combine(Path.GetTempPath(), "elsewhere.txt")));

        [Fact] public void SiblingPrefix_Escape_Denied() => Assert.False(PathContainment.IsWithin(Root, Root + "-evil" + Path.DirectorySeparatorChar + "x.txt"));

        [Fact]
        public void EmptyOrNull_Denied()
        {
            Assert.False(PathContainment.IsWithin(Root, ""));
            Assert.False(PathContainment.IsWithin(Root, null));
            Assert.False(PathContainment.IsWithin("", "Assets/x"));
            Assert.False(PathContainment.IsWithin(null, "Assets/x"));
        }

        private static readonly bool IsWindows =
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        // Bug hunt Sec-3: drive-relative shapes resolve against the per-drive CWD on WINDOWS and must be denied there;
        // on Linux ':' is a legal filename char, so "C:foo" is just an in-project relative path -> allowed (over-denial fix).
        [Fact]
        public void DriveRelative_DeniedOnWindows()
        {
            Assert.Equal(!IsWindows, PathContainment.IsWithin(Root, "C:foo"));
            Assert.Equal(!IsWindows, PathContainment.IsWithin(Root, "Z:secret.txt"));
        }

        // Bug hunt Sec-6: NTFS ADS colon is a Windows-only hazard — denied on Windows, a legal in-project filename on Linux.
        [Fact]
        public void AlternateDataStream_DeniedOnWindows()
            => Assert.Equal(!IsWindows, PathContainment.IsWithin(Root, "Assets/Foo.cs:hidden"));

        // Case-variant sibling: denied on case-sensitive filesystems (Linux CI), allowed on Windows/macOS — pins the
        // platform-aware comparison instead of the old unconditional ignore-case.
        [Fact]
        public void CaseVariantSibling_MatchesFilesystemCaseRules()
        {
            bool caseInsensitive =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
                || System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
            var variant = Root.ToUpperInvariant() + Path.DirectorySeparatorChar + "secret.txt";
            Assert.Equal(caseInsensitive, PathContainment.IsWithin(Root, variant));
        }
    }
}
