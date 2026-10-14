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
    /// <summary>
    /// MinMaxGradient: a color, [color, color] (random between two) or a gradient. Colors and gradients are coerced and
    /// encoded by the kernel — this adapter does not know how colors or gradients are written.
    /// </summary>
    public sealed class UnityEngine_MinMaxGradient_Adapter : RqlTypeAdapter<ParticleSystem.MinMaxGradient>
    {
        public override string Description => "MinMaxGradient: write a color, [color, color] or gradient keys [[color, time], …]";

        protected override JsonNode? Encode(ParticleSystem.MinMaxGradient value, EncodeContext context) => value.mode switch
        {
            ParticleSystemGradientMode.Color => context.EncodeValue(value.color),
            ParticleSystemGradientMode.TwoColors => new JsonArray(context.EncodeValue(value.colorMin), context.EncodeValue(value.colorMax)),
            ParticleSystemGradientMode.Gradient => context.EncodeValue(value.gradient),
            _ => JsonValue.Create(value.mode.ToString()),
        };

        public override AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
        {
            result = null;
            reason = null;
            if (kernel.TryCoerce(value, typeof(Color), out var color, out _))
            {
                result = new ParticleSystem.MinMaxGradient((Color)color!);
                return AdapterOutcome.Ok;
            }
            if (value is IList<object?> list && list.Count == 2
                && kernel.TryCoerce(list[0], typeof(Color), out var min, out _) && kernel.TryCoerce(list[1], typeof(Color), out var max, out _))
            {
                result = new ParticleSystem.MinMaxGradient((Color)min!, (Color)max!);
                return AdapterOutcome.Ok;
            }
            if (kernel.TryCoerce(value, typeof(Gradient), out var gradient, out var why))
            {
                result = new ParticleSystem.MinMaxGradient((Gradient)gradient!);
                return AdapterOutcome.Ok;
            }
            reason = "expected a color, [color, color] or gradient keys (" + why + ")";
            return AdapterOutcome.Failed;
        }
    }
}
