using Pulsar.Common.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;

namespace Pulsar.Server.Plugins
{
    internal sealed class ClientPluginDescriptor
    {
        public ClientPluginDescriptor(string pluginId, string typeName, byte[] assemblyBytes, string version, byte[] initData)
        {
            PluginId = pluginId;
            TypeName = typeName;
            AssemblyBytes = assemblyBytes;
            Version = version;
            InitData = initData;
        }

        public string PluginId { get; }
        public string TypeName { get; }
        public byte[] AssemblyBytes { get; }
        public byte[] InitData { get; }
        public string Version { get; }
        public string CacheKey => $"{PluginId}:{Version}";
    }

    internal sealed class ClientPluginCatalog : IDisposable
    {
        private readonly IServerContext _context;
        private readonly object _sync = new object();
        private FileSystemWatcher _watcher;
        private string _folder = string.Empty;
        private ClientPluginDescriptor[] _plugins = Array.Empty<ClientPluginDescriptor>();

        public event EventHandler PluginsChanged;

        public ClientPluginCatalog(IServerContext context)
        {
            _context = context;
        }

        public IReadOnlyList<ClientPluginDescriptor> Plugins => _plugins;

        public void LoadFrom(string folder)
        {
            lock (_sync)
            {
                _folder = folder;
                EnsureDirectoryExists();
                ReloadInternal();
                StartWatcher();
            }

            PluginsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Reload()
        {
            lock (_sync)
            {
                if (string.IsNullOrEmpty(_folder))
                {
                    return;
                }

                ReloadInternal();
            }

            PluginsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _watcher?.Dispose();
        }

        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_folder))
            {
                Directory.CreateDirectory(_folder);
                _context?.Log("Created client plugin directory: " + _folder);
            }
        }

        private void ReloadInternal()
        {
            var descriptors = new List<ClientPluginDescriptor>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var files = Directory.EnumerateFiles(_folder, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(IsClientPluginFile)
                .OrderBy(Path.GetFileName);

            foreach (var file in files)
            {
                var descriptor = TryLoad(file);
                if (descriptor == null)
                {
                    continue;
                }

                if (!seenIds.Add(descriptor.PluginId))
                {
                    _context?.Log($"Duplicate client plugin id '{descriptor.PluginId}' detected in '{Path.GetFileName(file)}'; skipping duplicate.");
                    continue;
                }

                descriptors.Add(descriptor);
                _context?.Log($"Loaded client plugin '{descriptor.PluginId}' v{descriptor.Version} from {Path.GetFileName(file)}");
            }

            _plugins = descriptors.ToArray();
        }

        private ClientPluginDescriptor TryLoad(string path)
        {
            AssemblyLoadContext loadContext = new AssemblyLoadContext("ClientPluginLoadContext", true);
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            try
            {
                if (!IsClientPluginFile(path))
                {
                    return null;
                }

                
                using MemoryStream executableStream = new(File.ReadAllBytes(path));
                var bytes = executableStream.ToArray();
                // Load the assembly from the memory stream
                var asm = loadContext.LoadFromStream(executableStream);

                var pluginType = asm.GetTypes()
                    .FirstOrDefault(t => typeof(IUniversalPlugin).IsAssignableFrom(t) && !t.IsAbstract);

                if (pluginType == null)
                {
                    _context?.Log($"Client plugin '{Path.GetFileName(path)}' does not expose an IUniversalPlugin implementation.");
                    return null;
                }

                if (Activator.CreateInstance(pluginType) is not IUniversalPlugin pluginInstance)
                {
                    _context?.Log($"Client plugin '{Path.GetFileName(path)}' could not be instantiated.");
                    return null;
                }

                var pluginId = string.IsNullOrWhiteSpace(pluginInstance.PluginId)
                    ? pluginType.FullName ?? Path.GetFileNameWithoutExtension(path)
                    : pluginInstance.PluginId;

                var version = string.IsNullOrWhiteSpace(pluginInstance.Version)
                    ? "1.0.0"
                    : pluginInstance.Version;

                var initPath = Path.ChangeExtension(path, ".init");
                byte[] initBytes = null;
                if (File.Exists(initPath))
                {
                    initBytes = File.ReadAllBytes(initPath);
                }

                return new ClientPluginDescriptor(
                    pluginId,
                    pluginType.FullName ?? pluginType.Name,
                    bytes,
                    version,
                    initBytes);
            }
            catch (ReflectionTypeLoadException rtle)
            {
                _context?.Log($"Client plugin load error ({Path.GetFileName(path)}): {rtle.Message}");
                foreach (var exception in rtle.LoaderExceptions)
                {
                    if (!string.IsNullOrWhiteSpace(exception?.Message))
                    {
                        _context?.Log("  " + exception.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _context?.Log($"Client plugin load error ({Path.GetFileName(path)}): {ex.Message}");
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve; // Clean up event subscription
            }

            return null;
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name).Name;

            try
            {
                var loadingPluginAssembly = args.RequestingAssembly;
                var resources = loadingPluginAssembly.GetManifestResourceNames();

                // Log available resources
                _context.Log($"Available resources in assembly '{loadingPluginAssembly.FullName}': {string.Join(", ", resources)}");

                // Find resources that match the assembly name by extracting the base name
                var nameFormatted = resources
                    .FirstOrDefault(resource =>
                    {
                        // Extract the base name before .g.resources or .resources
                        // .Replace(".g.resources", "").Replace(".resources", "").
                        var baseName = resource.Replace(".dll.compressed", "").Replace("costura.", "");
                        var eq = baseName.Equals(assemblyName, StringComparison.OrdinalIgnoreCase);
                        // Check if the base name matches the assembly name
                        return baseName.Equals(assemblyName.Replace(".dll.compressed", "").Replace("costura.", ""),
                            StringComparison.OrdinalIgnoreCase) || baseName == assemblyName;
                    });

                // usually embeded resources like dlls (cosutra.dllname.dll.compressed) starts with costura but others like .
                if (nameFormatted != null && nameFormatted.StartsWith("costura"))
                {
                    _context.Log($"Found matching resource: {nameFormatted}");
                    _context.Log($"Invoking LoadStream with: {nameFormatted}");

                    var costuraAssemblyLoaderType = loadingPluginAssembly.GetType("Costura.AssemblyLoader");
                    var loadStreamMethod = costuraAssemblyLoaderType.GetMethod(
                        "LoadStream",
                        BindingFlags.NonPublic | BindingFlags.Static,
                        null,
                        new Type[] { typeof(string) },
                        null
                    );

                    // Invoke LoadStream to get the assembly stream
                    var assemblyStream = (Stream)loadStreamMethod.Invoke(null, new object[] { nameFormatted });

                    if (assemblyStream == null)
                    {
                        _context.Log($"Failed to load stream for assembly: {nameFormatted}");
                        return null;
                    }

                    using (var memoryStream = new MemoryStream())
                    {
                        assemblyStream.CopyTo(memoryStream);
                        return Assembly.Load(memoryStream.ToArray());
                    }
                }
                else
                {
                    _context.Log($"No matching resource found for assembly: {assemblyName}");
                }
            }
            catch (TargetInvocationException tie)
            {
                _context.Log($"Target invocation exception: {tie.InnerException?.Message}");
            }
            catch (Exception ex)
            {
                _context.Log($"Error resolving assembly '{assemblyName}': {ex.Message}");
            }

            

            var dependsPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Depends");
            var fallbackPath = Path.Combine(dependsPath, $"{assemblyName}.dll");

            if (!Directory.Exists(dependsPath)) Directory.CreateDirectory(dependsPath);
            
            if (File.Exists(fallbackPath))
            {
                return Assembly.LoadFrom(fallbackPath);
            }

            _context.Log($"Unable to resolve assembly: {assemblyName}");
            return null;
        }


        private void StartWatcher()
        {
            _watcher?.Dispose();

            _watcher = new FileSystemWatcher(_folder)
            {
                IncludeSubdirectories = false,
                Filter = "*.dll*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };

            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;

            _context?.Log("Client plugin watcher started for: " + _folder);
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsClientPluginFile(e.FullPath))
            {
                return;
            }
            ScheduleReload($"Client plugin file {e.ChangeType}: {e.Name}");
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            if (!IsClientPluginFile(e.FullPath) && !IsClientPluginFile(e.OldFullPath))
            {
                return;
            }
            ScheduleReload($"Client plugin file renamed: {e.OldName} -> {e.Name}");
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _context?.Log($"Client plugin watcher error: {e.GetException().Message}");
        }

        private void ScheduleReload(string reason)
        {
            _context?.Log(reason);
            Task.Delay(500).ContinueWith(_ =>
            {
                try
                {
                    Reload();
                }
                catch (Exception ex)
                {
                    _context?.Log($"Client plugin reload error: {ex.Message}");
                }
            });
        }

        private static bool IsClientPluginFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var fileName = Path.GetFileName(path);
            return fileName != null && fileName.EndsWith(".Client.dll", StringComparison.OrdinalIgnoreCase);
        }
    }
}
