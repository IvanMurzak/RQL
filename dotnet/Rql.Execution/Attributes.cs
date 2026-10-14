using System;

namespace Rql
{
    /// <summary>Hide a member from RQL entirely.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class RqlIgnoreAttribute : Attribute { }

    /// <summary>The member is the object's stable id; used to build <c>$ref</c> = "Type#id".</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class RqlIdAttribute : Attribute { }

    /// <summary>The member is readable but never writable through <c>set</c>.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class RqlReadOnlyAttribute : Attribute { }

    /// <summary>
    /// A non-owning reference (parent link, target, shared asset). Readable, but generic
    /// <c>recurse</c> (without explicit members) does not follow it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class RqlReferenceAttribute : Attribute { }

    /// <summary>The member is the display name included in object summaries.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class RqlDisplayAttribute : Attribute { }

    /// <summary>Method may be invoked with the <c>call</c> mutation stage (when mutations are allowed).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RqlCallableAttribute : Attribute
    {
        public RqlCallableAttribute(string? description = null) { Description = description; }
        public string? Description { get; }
    }

    /// <summary>Registration order for <see cref="RqlEngine.AddAdaptersFrom"/> (lower first; ties by full type name).</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class RqlAdapterOrderAttribute : Attribute
    {
        public RqlAdapterOrderAttribute(int order) { Order = order; }
        public int Order { get; }
    }

    /// <summary>Optional human description of a type or member, surfaced by schema/describe.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum)]
    public sealed class RqlDescriptionAttribute : Attribute
    {
        public RqlDescriptionAttribute(string text) { Text = text; }
        public string Text { get; }
    }
}
