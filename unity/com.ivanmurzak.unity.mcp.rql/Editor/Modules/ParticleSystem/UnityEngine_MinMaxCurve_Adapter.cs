/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Rql.Execution;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Particles
{
    /// <summary>MinMaxCurve: a number (constant) or [min, max] (random between two constants); curves are reported by mode.</summary>
    public sealed class UnityEngine_MinMaxCurve_Adapter : RqlTypeAdapter<ParticleSystem.MinMaxCurve>
    {
        public override string Description => "MinMaxCurve: write a number (constant) or [min, max]";

        protected override JsonNode? Encode(ParticleSystem.MinMaxCurve value, EncodeContext context) => value.mode switch
        {
            ParticleSystemCurveMode.Constant => context.EncodeValue(value.constant),
            ParticleSystemCurveMode.TwoConstants => new JsonArray(context.EncodeValue(value.constantMin), context.EncodeValue(value.constantMax)),
            _ => JsonValue.Create($"{value.mode} ×{value.curveMultiplier}"),
        };

        public override AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
        {
            result = null;
            reason = null;
            if (value is IList<object?> list && list.Count == 2)
            {
                if (!kernel.TryCoerce(list[0], typeof(float), out var min, out reason, "[0]")) return AdapterOutcome.Failed;
                if (!kernel.TryCoerce(list[1], typeof(float), out var max, out reason, "[1]")) return AdapterOutcome.Failed;
                result = new ParticleSystem.MinMaxCurve((float)min!, (float)max!);
                return AdapterOutcome.Ok;
            }
            if (!(value is IList<object?>) && kernel.TryCoerce(value, typeof(float), out var constant, out _))
            {
                result = new ParticleSystem.MinMaxCurve((float)constant!);
                return AdapterOutcome.Ok;
            }
            reason = "expected a number or [min, max]";
            return AdapterOutcome.Failed;
        }
    }
}
