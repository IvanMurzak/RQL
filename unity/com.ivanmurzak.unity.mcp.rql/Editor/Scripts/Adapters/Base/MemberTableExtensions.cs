/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System.Linq;
using Rql.Execution;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>
    /// For members that exist only in some Unity versions (e.g. MonoBehaviour.didStart). Members present in every supported
    /// version use the strict MemberTable calls, which reject unknown names so typos fail loudly.
    /// </summary>
    public static class MemberTableExtensions
    {
        public static MemberTable HideExisting(this MemberTable table, params string[] names) =>
            table.Hide(names.Where(table.Contains).ToArray());
    }
}
