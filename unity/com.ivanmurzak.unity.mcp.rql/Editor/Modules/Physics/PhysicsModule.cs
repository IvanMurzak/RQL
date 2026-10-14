/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using Rql.Execution;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Physics3D
{
    /// <summary>
    /// Optional module: lives in an assembly that only compiles when com.unity.modules.physics is enabled (asmdef
    /// versionDefines + defineConstraints), so its adapters exist exactly when the project has 3D physics.
    /// </summary>
    public sealed class PhysicsModule : IRqlModule
    {
        public int Order => 100;

        public void Configure(RqlModuleContext ctx) => ctx.AddAdapter(new UnityEngine_Collider_Adapter());
    }
}
