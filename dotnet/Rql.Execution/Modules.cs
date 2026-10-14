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
        internal RqlModuleContext(RqlEngine engine, string module) { Engine = engine; Module = module; }

        /// <summary>The engine being configured (for anything not covered by the helpers below).</summary>
        public RqlEngine Engine { get; }

        /// <summary>Full name of the module being configured.</summary>
        public string Module { get; }

        public RqlModuleContext AddAdapter(RqlTypeAdapter adapter) { Engine.AddAdapter(adapter); return this; }

        public RqlModuleContext ExcludeTypes(Func<Type, bool> excluded) { Engine.ExcludeTypes(excluded); return this; }

        public RqlModuleContext TreatAsObject(Type type) { TypeModel.TreatAsObject(type); return this; }

        public RqlModuleContext AddSource(string name, Func<object?> getter, string? description = null) { Engine.AddSource(name, getter, description); return this; }

        public RqlModuleContext AddFunction(string name, Func<object?[], object?> fn) { Engine.AddFunction(name, fn); return this; }
    }

    /// <summary>Outcome of configuring one discovered module.</summary>
    public sealed class RqlModuleReport
    {
        internal RqlModuleReport(string module, int order, string? error) { Module = module; Order = order; Error = error; }

        public string Module { get; }
        public int Order { get; }
        /// <summary>Null when the module configured successfully; otherwise why it was skipped.</summary>
        public string? Error { get; }
        public bool Ok => Error == null;
        public override string ToString() => Ok ? $"{Module} (order {Order})" : $"{Module}: FAILED — {Error}";
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
            var modules = new List<(IRqlModule module, string name)>();
            foreach (var asm in assemblies.Distinct())
            {
                if (skipAssembly != null && skipAssembly(asm)) continue;
                foreach (var t in TypeModel.LoadableTypes(asm, err => reports.Add(new RqlModuleReport(asm.GetName().Name ?? "?", 0, err))))
                {
                    if (!t.IsClass || t.IsAbstract || !t.IsVisible || t.ContainsGenericParameters || !typeof(IRqlModule).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                    try { modules.Add(((IRqlModule)Activator.CreateInstance(t)!, t.FullName ?? t.Name)); }
                    catch (Exception ex) { reports.Add(new RqlModuleReport(t.FullName ?? t.Name, 0, (ex.InnerException ?? ex).Message)); }
                }
            }
            foreach (var (module, name) in modules.OrderBy(m => SafeOrder(m.module)).ThenBy(m => m.name, StringComparer.Ordinal))
            {
                var order = SafeOrder(module);
                try
                {
                    module.Configure(new RqlModuleContext(engine, name));
                    reports.Add(new RqlModuleReport(name, order, null));
                }
                catch (Exception ex) { reports.Add(new RqlModuleReport(name, order, (ex.InnerException ?? ex).Message)); }
            }
            return reports;
        }

        private static int SafeOrder(IRqlModule m)
        {
            try { return m.Order; } catch { return int.MaxValue; }
        }
    }
}
