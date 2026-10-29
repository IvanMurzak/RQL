using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Rql.Execution
{
    /// <summary>
    /// A unit of host extension (spec §13 "Modules"): a set of adapters, type exclusions, sources and functions that is
    /// present or absent as a whole — e.g. one per optional engine package (Unity's Particle System, Terrain, Splines …).
    /// Modules are discovered by assembly scan, so installing a package that contains one is enough; a module whose
    /// package is absent simply does not exist. Modules register explicitly in <see cref="Configure"/>.
    /// </summary>
    public interface IRqlModule
    {
        /// <summary>Configuration order: lower first; ties by full type name. Core modules use 0, extensions above.</summary>
        int Order { get; }

        void Configure(RqlModuleContext context);
    }

    /// <summary>What a module may contribute while it is configured.</summary>
    public sealed class RqlModuleContext
    {
        internal RqlModuleContext(RqlEngine engine) { Engine = engine; }

        /// <summary>The engine being configured (for anything not covered by the helpers below).</summary>
        public RqlEngine Engine { get; }

        public RqlModuleContext AddAdapter(RqlTypeAdapter adapter) { Engine.AddAdapter(adapter); return this; }

        public RqlModuleContext ExcludeTypes(Func<Type, bool> excluded) { Engine.ExcludeTypes(excluded); return this; }

        public RqlModuleContext TreatAsObject(Type type) { TypeModel.TreatAsObject(type); return this; }

        public RqlModuleContext AddSource(string name, Func<object?> getter, string? description = null) { Engine.AddSource(name, getter, description); return this; }

        /// <summary>A source with a declared value type (see <see cref="RqlEngine.AddSource(string, Func{object}, string, Type)"/>).</summary>
        public RqlModuleContext AddSource(string name, Func<object?> getter, string? description, Type? declaredType) { Engine.AddSource(name, getter, description, declaredType); return this; }

        public RqlModuleContext AddFunction(string name, Func<object?[], object?> fn, string? description = null) { Engine.AddFunction(name, fn, description); return this; }

        /// <summary>Agent-facing guidance for what this module adds (rendered into the host's help / skill files).</summary>
        public RqlModuleContext AddGuide(string topic, string markdown) { Engine.AddGuide(topic, markdown); return this; }
    }

    /// <summary>Outcome of configuring one discovered module.</summary>
    public sealed class RqlModuleReport
    {
        internal RqlModuleReport(string module, int order, string? error) { Module = module; Order = order; Error = error; }

        internal RqlModuleReport(string module, int order, string? error, Registrations registered) : this(module, order, error)
        {
            Sources = registered.Sources;
            Functions = registered.Functions;
            Stages = registered.Stages;
            Guides = registered.Guides;
            Adapters = registered.Adapters;
        }

        public string Module { get; }

        /// <summary>The sources this module registered (names), so a host can tell which module owns a source.</summary>
        public IReadOnlyList<string> Sources { get; } = Array.Empty<string>();
        /// <summary>The host functions this module registered (names).</summary>
        public IReadOnlyList<string> Functions { get; } = Array.Empty<string>();
        /// <summary>The extension stages this module registered (names).</summary>
        public IReadOnlyList<string> Stages { get; } = Array.Empty<string>();
        /// <summary>The guide topics this module added.</summary>
        public IReadOnlyList<string> Guides { get; } = Array.Empty<string>();
        /// <summary>The type adapters this module added.</summary>
        public IReadOnlyList<RqlTypeAdapter> Adapters { get; } = Array.Empty<RqlTypeAdapter>();
        public int Order { get; }
        /// <summary>Null when the module configured successfully; otherwise why it was skipped.</summary>
        public string? Error { get; }
        public bool Ok => Error == null;
        public override string ToString() => Ok ? $"{Module} (order {Order})" : $"{Module}: FAILED — {Error}";
    }

    /// <summary>What one module registered on the engine, recorded by the engine while the module configures.</summary>
    internal sealed class Registrations
    {
        public readonly List<string> Sources = new List<string>();
        public readonly List<string> Functions = new List<string>();
        public readonly List<string> Stages = new List<string>();
        public readonly List<string> Guides = new List<string>();
        public readonly List<RqlTypeAdapter> Adapters = new List<RqlTypeAdapter>();

        public static void Note(List<string> list, string name)
        {
            if (!list.Contains(name)) list.Add(name);
        }
    }

    internal static class ModuleDiscovery
    {
        /// <summary>
        /// Instantiate and configure every public, concrete <see cref="IRqlModule"/> with a public parameterless constructor in
        /// <paramref name="assemblies"/> (skipping those <paramref name="skipAssembly"/> rejects), in deterministic order.
        /// Failures are isolated: a module that throws is reported and the others still run.
        /// </summary>
        public static IReadOnlyList<RqlModuleReport> Configure(RqlEngine engine, IEnumerable<Assembly> assemblies, Func<Assembly, bool>? skipAssembly)
        {
            var reports = new List<RqlModuleReport>();
            var types = new List<Type>();
            foreach (var asm in assemblies.Distinct())
            {
                if (skipAssembly != null && skipAssembly(asm)) continue;
                types.AddRange(TypeModel.LoadableTypes(asm, err => reports.Add(new RqlModuleReport(asm.GetName().Name ?? "?", 0, err))));
            }
            return Configure(engine, types, reports);
        }

        /// <summary>Configure the module types among <paramref name="candidates"/>; non-module types are ignored.</summary>
        public static IReadOnlyList<RqlModuleReport> Configure(RqlEngine engine, IEnumerable<Type> candidates, List<RqlModuleReport> reports)
        {
            var modules = new List<(IRqlModule module, string name, int order)>();
            foreach (var t in candidates.Distinct())
            {
                if (!t.IsClass || t.IsAbstract || !t.IsVisible || t.ContainsGenericParameters || !typeof(IRqlModule).IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                var name = t.FullName ?? t.Name;
                try
                {
                    var module = (IRqlModule)Activator.CreateInstance(t)!;
                    modules.Add((module, name, SafeOrder(module)));
                }
                catch (Exception ex) { reports.Add(new RqlModuleReport(name, 0, (ex.InnerException ?? ex).Message)); }
            }
            foreach (var (module, name, order) in modules.OrderBy(m => m.order).ThenBy(m => m.name, StringComparer.Ordinal))
            {
                var outer = engine.Recording; // a module may configure modules of its own
                var registered = engine.Recording = new Registrations();
                try
                {
                    module.Configure(new RqlModuleContext(engine));
                    reports.Add(new RqlModuleReport(name, order, null, registered));
                }
                catch (Exception ex) { reports.Add(new RqlModuleReport(name, order, (ex.InnerException ?? ex).Message, registered)); }
                finally { engine.Recording = outer; }
            }
            return reports;
        }

        private static int SafeOrder(IRqlModule m)
        {
            try { return m.Order; } catch { return int.MaxValue; }
        }
    }
}
