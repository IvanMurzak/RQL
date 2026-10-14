/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using Rql.Execution;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Particles
{
    /// <summary>
    /// A ParticleSystem module struct (ps.main, ps.emission …) is a handle: writing a member of a copy changes the particle
    /// system. Marked writes-through, so `set main.startLifetime = 3` works although `main` is a get-only property.
    /// </summary>
    public sealed class UnityEngine_ParticleSystem_ModuleStruct_Adapter : RqlTypeAdapter
    {
        public UnityEngine_ParticleSystem_ModuleStruct_Adapter(Type moduleStruct) : base(moduleStruct) { }

        public override string Name => "ParticleSystem." + Target.Name;

        public override void ConfigureMembers(MemberTable members) => members.WritesThrough();
    }
}
