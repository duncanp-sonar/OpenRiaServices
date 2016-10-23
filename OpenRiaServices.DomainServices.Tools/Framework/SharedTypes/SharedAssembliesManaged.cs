using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Managed.Reflection;
using OpenRiaServices.DomainServices;
using Type = Managed.Reflection.Type;

namespace OpenRiaServices.DomainServices.Tools.SharedTypes
{

    /// <summary>
    /// Internal class to maintain a set of known assemblies
    /// and allow clients to ask whether types or methods are
    /// in that set.
    /// </summary>
    internal class SharedAssembliesManaged : ISharedAssemblies
    {
        private readonly string[] _assemblySearchPaths;
        private readonly Dictionary<string, Type> _sharedTypeByName;
        private readonly Dictionary<string, Assembly> _assembliesByName;
        private readonly Dictionary<string, Assembly> _assembliesByFullName;

        private readonly ILogger _logger;
        private readonly Universe _universe;

        /// <summary>
        /// Creates a new instance of this type.
        /// </summary>
        /// <param name="assemblyFileNames">The set of assemblies to use</param>
        /// <param name="assemblySearchPaths">Optional set of paths to search for referenced assemblies.</param>
        /// <param name="logger">Optional logger to use to report errors or warnings</param>
        public SharedAssembliesManaged(IEnumerable<string> assemblyFileNames, IEnumerable<string> assemblySearchPaths, ILogger logger)
        {
            if (assemblyFileNames == null)
            {
                throw new ArgumentNullException("assemblyFileNames");
            }
            this._logger = logger;
            this._assemblySearchPaths = assemblySearchPaths?.ToArray() ?? new string[0];
            this._sharedTypeByName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            this._assembliesByName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            this._assembliesByFullName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            this._universe = new Universe(UniverseOptions.MetadataOnly | UniverseOptions.DisableDefaultAssembliesLookup);
            this._universe.AssemblyResolve += _universe_AssemblyResolve;

            var assemblies = LoadAssemblies(assemblyFileNames);

            PopulateSharedTypesDictionary(logger, assemblies);
        }

        private void PopulateSharedTypesDictionary(ILogger logger, List<Assembly> assemblies)
        {
            // Populate type cache with all found types
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetExportedTypes())
                {
                    try
                    {
                        _sharedTypeByName.Add(type.FullName, type);
                    }
                    catch (ArgumentException)
                    {
                        // Ignore types which are part of multiple assemblies
                        if (logger != null)
                            logger.LogMessage($"Ignoring type {type.FullName} found in assembly {assembly.FullName}");
                    }
                }
            }
        }

        private Assembly _universe_AssemblyResolve(object sender, Managed.Reflection.ResolveEventArgs args)
        {
            var universe = (Universe)sender;
            var assemblyName = new AssemblyName(args.Name);

            Assembly result;
            if (_assembliesByName.TryGetValue(assemblyName.Name, out result)
                 && CanResolveNameWithAssembly(assemblyName, result))
                return result;

            if (args.RequestingAssembly != null)
            {
                var searchPaths = new List<string>(this._assemblySearchPaths.Length + 1);
                searchPaths.Add(Path.GetDirectoryName(args.RequestingAssembly.Location));
                searchPaths.AddRange(_assemblySearchPaths);

                result = ReflectionOnlyLoadFromSearchPaths(assemblyName, searchPaths);
            }
            else
            {
                result = ReflectionOnlyLoadFromSearchPaths(assemblyName, _assemblySearchPaths);
            }

            if (CanResolveNameWithAssembly(assemblyName, result))
            {
                _assembliesByName[assemblyName.Name] = result;
            }

            if (result == null)
                return universe.CreateMissingAssembly(args.Name);
                

            return result;
        }

        private bool CanResolveNameWithAssembly(AssemblyName assemblyName, Assembly result)
        {
            return result != null
                            && result.GetName().Version >= assemblyName.Version;
        }

        /// <summary>
        /// Returns the location of the shared assembly containing the
        /// code member described by <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The description of the code element.</param>
        /// <returns>The location of the assembly that contains it or <c>null</c> if it is not in a shared assembly.</returns>
        public string GetSharedAssemblyPath(CodeMemberKey key)
        {
            Debug.Assert(key != null, "key cannot be null");
            string location = null;

            Type type = this.GetSharedType(key.TypeName);
            if (type != null)
            {
                switch (key.KeyKind)
                {
                    case CodeMemberKey.CodeMemberKeyKind.TypeKey:
                        location = type.Assembly.Location;
                        break;

                    case CodeMemberKey.CodeMemberKeyKind.PropertyKey:
                        PropertyInfo propertyInfo = type.GetProperty(key.MemberName);
                        if (propertyInfo != null)
                        {
                            location = propertyInfo.DeclaringType.Assembly.Location;
                        }
                        break;

                    case CodeMemberKey.CodeMemberKeyKind.MethodKey:
                        Type[] parameterTypes = this.GetSharedTypes(key.ParameterTypeNames);
                        if (parameterTypes != null)
                        {
                            MethodBase methodBase = this.FindSharedMethodOrConstructor(type, key);
                            if (methodBase != null)
                            {
                                location = methodBase.DeclaringType.Assembly.Location;
                            }
                        }
                        break;

                    default:
                        Debug.Fail("unsupported key kind");
                        break;
                }
            }
            return location;
        }

        /// <summary>
        /// Locates the <see cref="MethodBase"/> in the set of shared assemblies that
        /// corresponds to the method described by <paramref name="key"/>.
        /// </summary>
        /// <param name="sharedType">The <see cref="Type"/> we have already located in our set of shared assemblies.</param>
        /// <param name="key">The key describing the method to find.</param>
        /// <returns>The matching <see cref="MethodBase"/> or <c>null</c> if no match is found.</returns>
        private MethodBase FindSharedMethodOrConstructor(Type sharedType, CodeMemberKey key)
        {
            Type[] parameterTypes = this.GetSharedTypes(key.ParameterTypeNames);
            if (parameterTypes == null)
            {
                return null;
            }
            bool isConstructor = key.IsConstructor;
            IEnumerable<MethodBase> methods = isConstructor ? sharedType.GetConstructors().Cast<MethodBase>() : sharedType.GetMethods().Cast<MethodBase>();
            foreach (MethodBase method in methods)
            {
                if (!isConstructor && !string.Equals(method.Name, key.MemberName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ParameterInfo[] parameterInfos = method.GetParameters();
                if (parameterInfos.Length != parameterTypes.Length)
                {
                    continue;
                }
                int matchedParameters = 0;
                for (int i = 0; i < parameterInfos.Length; ++i)
                {
                    if (string.Equals(parameterInfos[i].ParameterType.FullName, parameterTypes[i].FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        ++matchedParameters;
                    }
                    else
                    {
                        break;
                    }
                }

                if (matchedParameters == parameterInfos.Length)
                {
                    return method;
                }
            }
            return null;
        }

        /// <summary>
        /// Given a collection of <see cref="Type"/> names, this method returns
        /// an array of all those types from the set of shared assemblies.
        /// </summary>
        /// <param name="typeNames">The collection of type names.  It can be null.</param>
        /// <returns>The collection of types in the shared assemblies.
        /// A <c>null</c> means one or more types in the list were not shared.</returns>
        internal Type[] GetSharedTypes(IEnumerable<string> typeNames)
        {
            List<Type> types = new List<Type>();
            if (typeNames != null)
            {
                foreach (string typeName in typeNames)
                {
                    Type type = this.GetSharedType(typeName);
                    if (type == null)
                    {
                        return null;
                    }
                    types.Add(type);
                }
            }
            return types.ToArray();
        }

        /// <summary>
        /// Returns the <see cref="Type"/> from the set of shared assemblies of the given name.
        /// </summary>
        /// <param name="typeName">The fully qualified type name.</param>
        /// <returns>The <see cref="Type"/> from the shared assemblies if it exists, otherwise <c>null</c>.</returns>
        private Type GetSharedType(string typeName)
        {
            Type sharedType;
            if (!_sharedTypeByName.TryGetValue(typeName, out sharedType))
            {
                // Check if typename is assembly qualified, and try resolving without assembly qualified name
                int assemblyNameStart = typeName.IndexOf(',', 0);
                if (assemblyNameStart != -1)
                {
                    var shortTypeName = typeName.Substring(0, assemblyNameStart);
                    sharedType = GetSharedType(shortTypeName);
                }

                if (sharedType == null)
                {
                    // Try resolving type using universe, but fallback to old mscorlib special case
                    sharedType = _universe.GetType(typeName, throwOnError: false);
                }

                _sharedTypeByName.Add(typeName, sharedType);
            }

            return sharedType;
        }

        /// <summary>
        /// Loads all the named assemblies into a cache of known assemblies
        /// </summary>
        private List<Assembly> LoadAssemblies(IEnumerable<string> assemblyFileNames)
        {
            var assemblies = new List<Assembly>();
            Dictionary<string, Assembly> loadedAssemblies = _assembliesByFullName;

            foreach (string file in assemblyFileNames)
            {
                // Pass 1 -- load all the assemblies we have been given.  No referenced assemblies yet.
                Assembly assembly = ReflectionOnlyLoadFrom(file, this._logger);
                if (assembly != null)
                {
                    assemblies.Add(assembly);

                    // Keep track of loaded assemblies for next step
                    _assembliesByFullName[assembly.FullName] = assembly;
                    _assembliesByName[assembly.GetName().Name] = assembly;
                }
            }

            // Pass 2 -- recursively load all reference assemblies from the assemblies we loaded.
            foreach (Assembly assembly in assemblies)
            {
                ReflectionOnlyLoadReferences(assembly, this._assemblySearchPaths, _assembliesByFullName, /*recursive*/ true, this._logger);
            }

            foreach (var assembly in loadedAssemblies.Values)
            {
                try
                {
                    if(assembly != null)
                        _assembliesByName.Add(assembly.GetName().Name, assembly);
                }
                catch (Exception)
                {
                    // For duplicates only include first
                }
            }

            return assemblies;
        }

        private Assembly ReflectionOnlyLoadFrom(string assemblyFileName, ILogger logger)
        {
            System.Diagnostics.Debug.Assert(!string.IsNullOrEmpty(assemblyFileName), "assemblyFileName is required");

            Assembly assembly = null;

            try
            {
                assembly = _universe.LoadFile(assemblyFileName);
            }
            catch (Exception ex)
            {
                // Some common exceptions log a warning and keep running
                if (ex is System.IO.FileNotFoundException ||
                    ex is System.IO.FileLoadException ||
                    ex is System.IO.PathTooLongException ||
                    ex is Managed.Reflection.BadImageFormatException ||
                    ex is System.Security.SecurityException)
                {
                    if (logger != null)
                    {
                        logger.LogMessage(string.Format(CultureInfo.CurrentCulture, Resource.ClientCodeGen_Assembly_Load_Error, assemblyFileName, ex.Message));
                    }
                }
                else
                {
                    throw;
                }
            }
            return assembly;
        }


        /// <summary>
        /// Does a "reflection only load" of all the reference assemblies for the given <paramref name="assembly"/>
        /// </summary>
        /// <param name="assembly">The assembly whose references need to be loaded.</param>
        /// <param name="assemblySearchPaths">Optional list of folders to search for assembly references.</param>
        /// <param name="loadedAssemblies">Dictionary to track already loaded assemblies.</param>
        /// <param name="recursive">If <c>true</c> recursively load references from the references.</param>
        /// <param name="logger">The optional logger to use to report known load failures.</param>
        /// <returns><c>true</c> means all loads succeeded, <c>false</c> means some errors occurred and were logged.
        /// </returns>
        internal bool ReflectionOnlyLoadReferences(Assembly assembly, IEnumerable<string> assemblySearchPaths, Dictionary<string, Assembly> loadedAssemblies, bool recursive, ILogger logger)
        {
            System.Diagnostics.Debug.Assert(assembly != null, "assembly is required");
            System.Diagnostics.Debug.Assert(loadedAssemblies != null, "loadedAssemblies is required");

            bool result = true;

            // Ensure this assembly itself is shown in our loaded list
            loadedAssemblies[assembly.FullName] = assembly;

            AssemblyName[] assemblyReferences = assembly.GetReferencedAssemblies();
            foreach (AssemblyName name in assemblyReferences)
            {
                Assembly referenceAssembly = null;

                // It may have been loaded already.  If so, assume that means we already
                // followed down its references.  Otherwise, load it and honor the
                // request to recursively load its references.
                if (!TryGetLoadedAssembly(name, out referenceAssembly))
                {
                    referenceAssembly = ReflectionOnlyLoad(name, loadedAssemblies, logger);

                    // Note: we always put the result into our cache, even for failure.
                    // This prevents us from attempting to load it multiple times
                    loadedAssemblies[name.FullName] = referenceAssembly;

                    if (referenceAssembly != null && recursive)
                    {
                        if (!ReflectionOnlyLoadReferences(referenceAssembly, assemblySearchPaths, loadedAssemblies, recursive, logger))
                        {
                            result = false;
                        }
                    }
                    else
                    {
                        // failure to load anything give false return
                        result = false;
                    }
                }
            }
            return result;
        }

        private bool TryGetLoadedAssembly(AssemblyName name, out Assembly referenceAssembly)
        {
            if (_assembliesByFullName.TryGetValue(name.FullName, out referenceAssembly))
                return true;

            //// Try to find an assembly with the same name, but a higher version
            //if (_assembliesByName.TryGetValue(name.Name, out referenceAssembly) 
            //    && referenceAssembly != null)
            //{
            //    // TODO: Look at public key token
            //    var loadedName = referenceAssembly.GetName();
            //    if (CanResolveAssemblyWith(name, loadedName))
            //    {
            //        _assembliesByFullName[name.FullName] = referenceAssembly;
            //        return true;
            //    }
            //    else
            //        referenceAssembly = null;
            //}

            return false;
        }

        private static bool CanResolveAssemblyWith(AssemblyName name, AssemblyName other)
        {
            return other.Version >= name.Version;
        }

        /// <summary>
        /// Does a "reflection only load" of the given <paramref name="assemblyName"/>.
        /// </summary>
        /// <param name="assemblyName">The name of the assembly to load.</param>
        /// <param name="loadedAssemblies">Dictionary to track already loaded assemblies.</param>
        /// <param name="logger">The optional logger to use to report known load failures.</param>
        /// 
        /// <returns>The loaded <see cref="Assembly"/> if successful, null if it could not be loaded for a known reason
        /// (and a warning message will have been logged).
        /// </returns>
        internal Assembly ReflectionOnlyLoad(AssemblyName assemblyName, IDictionary<string, Assembly> loadedAssemblies, ILogger logger)
        {
            System.Diagnostics.Debug.Assert(assemblyName != null, "assemblyName is required");

            Assembly assembly = null;

            string errorMessage = null;

            try
            {
                if (assembly == null)
                {
                    assembly = _universe.Load(assemblyName.FullName);
                }
            }
            catch (Exception ex)
            {
                // Some common exceptions log a warning and keep running
                if (ex is System.IO.FileNotFoundException ||
                    ex is System.IO.FileLoadException ||
                    ex is System.IO.PathTooLongException ||
                    ex is Managed.Reflection.BadImageFormatException ||
                    ex is System.Security.SecurityException)
                {
                    errorMessage = ex.Message;
                }
                else
                {
                    throw;
                }
            }

            // If we fail but were provided search paths, go search for it.  No warnings are logged.
            if (assembly == null && _assemblySearchPaths != null)
            {
                assembly = ReflectionOnlyLoadFromSearchPaths(assemblyName, _assemblySearchPaths);
            }

            // If we failed, log a warning with the original load failure
            if (assembly == null && !string.IsNullOrEmpty(errorMessage) && logger != null)
            {
                logger.LogMessage(string.Format(CultureInfo.CurrentCulture, Resource.ClientCodeGen_Assembly_Load_Error, assemblyName, errorMessage));
            }

            return assembly;
        }

        /// <summary>
        /// Attempts to load the specified <paramref name="assemblyName"/> by searching through
        /// the specified <paramref name="assemblySearchPaths"/>. The first successful load is returned.
        /// </summary>
        /// <param name="assemblyName">The assembly to locate.</param>
        /// <param name="assemblySearchPaths">List of full paths to folders to search.</param>
        /// <returns><c>null</c> for failure, else the first assembly loaded.</returns>
        internal Assembly ReflectionOnlyLoadFromSearchPaths(AssemblyName assemblyName, IEnumerable<string> assemblySearchPaths)
        {
            string baseName = assemblyName.Name;
            foreach (string path in assemblySearchPaths)
            {
                string fullPath = Path.Combine(path, baseName) + ".dll";
                if (File.Exists(fullPath))
                {
                    Assembly assembly = ReflectionOnlyLoadFrom(fullPath, null);
                    if (assembly != null)
                    {
                        return assembly;
                    }
                }
            }
            return null;
        }
    }
}
