using System.Runtime.CompilerServices;

// Lets the EditMode test assembly reach internal types of the Editor assembly —
// notably the anonymous objects (new { error = ... }) that handlers return — via
// dynamic. The runtime binder otherwise refuses cross-assembly access to an
// internal type's members, which is why SceneHandler/LoadScene tests throw
// RuntimeBinderException ('object' does not contain a definition for 'error').
[assembly: InternalsVisibleTo("UnityEditorMCP.Tests")]
