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
    }
}
