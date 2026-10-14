/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System.Linq;
using Rql.Execution;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>Member configuration that tolerates members missing in some Unity versions.</summary>
    public static class MemberTableExtensions
    {
        public static MemberTable OnlyExisting(this MemberTable table, params string[] names)
        {
            var present = names.Where(table.Contains).ToArray();
            return present.Length == 0 ? table : table.Only(present);
        }

        public static MemberTable HideExisting(this MemberTable table, params string[] names) =>
            table.Hide(names.Where(table.Contains).ToArray());

        public static MemberTable ExplicitOnlyExisting(this MemberTable table, params string[] names) =>
            table.ExplicitOnly(names.Where(table.Contains).ToArray());

        public static MemberTable ReferenceExisting(this MemberTable table, params string[] names) =>
            table.Reference(names.Where(table.Contains).ToArray());
    }
}
