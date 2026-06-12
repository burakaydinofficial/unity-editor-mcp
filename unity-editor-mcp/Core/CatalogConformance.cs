using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>The outcome of a catalog-conformance check.</summary>
    public sealed class ConformanceReport
    {
        /// <summary>Catalog editor commands with no registered handler (excluding known gaps).</summary>
        public IReadOnlyList<string> MissingHandlers { get; }

        /// <summary>Registered handlers for command types the catalog does not declare.</summary>
        public IReadOnlyList<string> UnexpectedHandlers { get; }

        public bool Ok => MissingHandlers.Count == 0 && UnexpectedHandlers.Count == 0;

        public ConformanceReport(IReadOnlyList<string> missing, IReadOnlyList<string> unexpected)
        {
            MissingHandlers = missing ?? Array.Empty<string>();
            UnexpectedHandlers = unexpected ?? Array.Empty<string>();
        }

        public override string ToString()
        {
            if (Ok) return "catalog conformance: OK";
            var parts = new List<string>();
            if (MissingHandlers.Count > 0) parts.Add($"missing handlers: {string.Join(", ", MissingHandlers)}");
            if (UnexpectedHandlers.Count > 0) parts.Add($"unexpected handlers: {string.Join(", ", UnexpectedHandlers)}");
            return "catalog conformance FAILED — " + string.Join("; ", parts);
        }
    }

    /// <summary>
    /// Verifies that the handlers registered at editor startup match the commands
    /// the protocol catalog declares for the editor side — the editor-side analog
    /// of the Node drift gate. The bootstrap can call this against
    /// <see cref="CommandCatalog.EditorCommands"/> and fail fast (or log) instead
    /// of discovering a missing handler at runtime via <c>UNKNOWN_COMMAND</c>.
    /// </summary>
    public static class CatalogConformance
    {
        public static ConformanceReport Check(
            IEnumerable<string> catalogEditorCommands,
            IEnumerable<string> registeredTypes,
            IEnumerable<string> knownGaps = null)
        {
            var catalog = new HashSet<string>(catalogEditorCommands ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var registered = new HashSet<string>(registeredTypes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var gaps = new HashSet<string>(knownGaps ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var missing = catalog
                .Where(c => !registered.Contains(c) && !gaps.Contains(c))
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
            var unexpected = registered
                .Where(c => !catalog.Contains(c))
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            return new ConformanceReport(missing, unexpected);
        }

        /// <summary>Convenience overload that reads the registered types from a dispatcher.</summary>
        public static ConformanceReport Check(
            IEnumerable<string> catalogEditorCommands,
            CommandDispatcher dispatcher,
            IEnumerable<string> knownGaps = null)
            => Check(catalogEditorCommands, dispatcher?.RegisteredTypes, knownGaps);
    }
}
