using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace HttpTraceAnalyser.Model.Extensibility
{
    /// <summary>
    /// Discovers and loads <see cref="ITraceParserPlugin"/> implementations from DLLs in a
    /// "Plugins" folder next to the application executable. This is the mechanism by which
    /// closed-source or non-redistributable parsers (e.g. an Outlook ETL parser that decodes
    /// custom, internal ETW providers) can be added to HttpTraceAnalyser without being part
    /// of this repository.
    ///
    /// Each plugin DLL is loaded into its own collectible <see cref="AssemblyLoadContext"/> so
    /// that a bad or conflicting plugin can't destabilize the host, while types shared with
    /// the host (e.g. <see cref="HttpTraceFile"/>, <see cref="HttpMessage"/>) resolve back to
    /// the host's already-loaded assembly via <see cref="AssemblyLoadContext.Resolving"/>,
    /// preserving type identity across the plugin/host boundary.
    /// </summary>
    public static class PluginManager
    {
        private static readonly List<ITraceParserPlugin> LoadedPlugins = new();
        private static readonly List<PluginLoadFailure> FailedPluginLoads = new();

        /// <summary>Plugins successfully loaded by the most recent call to <see cref="LoadPlugins"/>.</summary>
        public static IReadOnlyList<ITraceParserPlugin> Plugins => LoadedPlugins;

        /// <summary>Plugins (or plugin DLLs) that failed to load during the most recent call to <see cref="LoadPlugins"/>.</summary>
        public static IReadOnlyList<PluginLoadFailure> FailedPlugins => FailedPluginLoads;

        /// <summary>
        /// Directory scanned for plugin assemblies: a "Plugins" folder next to the
        /// application executable. Created automatically if it does not exist.
        /// </summary>
        public static string PluginsDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Plugins");

        /// <summary>
        /// Scans <see cref="PluginsDirectory"/> for *.dll files, loads each into its own
        /// isolated <see cref="AssemblyLoadContext"/>, instantiates every public,
        /// parameterless-constructible type implementing <see cref="ITraceParserPlugin"/>,
        /// and registers its file loader and extended fields with <see cref="HttpTraceFile"/>.
        /// Safe to call multiple times; already-loaded plugin assemblies are not reloaded.
        /// Errors loading an individual plugin are swallowed (and traced via
        /// <see cref="Debug.WriteLine(string)"/>) so one broken plugin cannot prevent the
        /// application from starting or other plugins from loading.
        /// </summary>
        public static void LoadPlugins()
        {
            LoadedPlugins.Clear();
            FailedPluginLoads.Clear();

            try
            {
                var dir = PluginsDirectory;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    return;
                }

                foreach (var dllPath in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        LoadPluginAssembly(dllPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PluginManager] Failed to load plugin '{dllPath}': {ex}");
                        FailedPluginLoads.Add(new PluginLoadFailure(Path.GetFileName(dllPath), ex.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginManager] Plugin discovery failed: {ex}");
                FailedPluginLoads.Add(new PluginLoadFailure(PluginsDirectory, ex.Message));
            }
        }

        private static void LoadPluginAssembly(string dllPath)
        {
            var context = new PluginLoadContext(dllPath);
            var assembly = context.LoadFromAssemblyPath(dllPath);

            var pluginTypes = assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                            && typeof(ITraceParserPlugin).IsAssignableFrom(t)
                            && t.GetConstructor(Type.EmptyTypes) is not null);

            foreach (var type in pluginTypes)
            {
                ITraceParserPlugin plugin;
                try
                {
                    plugin = (ITraceParserPlugin)Activator.CreateInstance(type)!;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginManager] Failed to instantiate plugin type '{type.FullName}': {ex}");
                    FailedPluginLoads.Add(new PluginLoadFailure($"{Path.GetFileName(dllPath)} ({type.FullName})", ex.Message));
                    continue;
                }

                RegisterPlugin(plugin, dllPath);
            }
        }

        private static void RegisterPlugin(ITraceParserPlugin plugin, string dllPath)
        {
            foreach (var extension in plugin.SupportedExtensions)
            {
                var ext = extension;
                HttpTraceFile.RegisterLoader(ext, path =>
                {
                    if (!plugin.CanLoad(path))
                        throw new NotSupportedException($"Plugin '{plugin.Name}' declined to load '{path}'.");
                    return plugin.Load(path);
                });
            }

            foreach (var field in plugin.ExtendedFields)
            {
                try
                {
                    HttpTraceFile.RegisterExtendedField(field);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginManager] Failed to register extended field '{field.Name}' from plugin '{plugin.Name}' ({dllPath}): {ex.Message}");
                }
            }

            LoadedPlugins.Add(plugin);
            Debug.WriteLine($"[PluginManager] Loaded plugin '{plugin.Name}' from '{dllPath}'.");
        }

        /// <summary>
        /// Collectible <see cref="AssemblyLoadContext"/> used to load a single plugin DLL.
        /// Falls back to the default context for any assembly it cannot resolve itself
        /// (notably the host application assembly), which ensures types like
        /// <see cref="HttpTraceFile"/> referenced by the plugin resolve to the exact same
        /// type as used by the host rather than a duplicate copy.
        /// </summary>
        private sealed class PluginLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;

            public PluginLoadContext(string pluginPath) : base(isCollectible: true)
            {
                _resolver = new AssemblyDependencyResolver(pluginPath);
            }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                var path = _resolver.ResolveAssemblyToPath(assemblyName);
                return path is not null ? LoadFromAssemblyPath(path) : null;
            }
        }
    }

    /// <summary>Describes a plugin (or plugin DLL) that failed to load, for display to the user.</summary>
    public sealed record PluginLoadFailure(string Source, string Error);
}
