﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity.PlugIns;

namespace Volo.Abp.Modularity
{
    public class ModuleLoader : IModuleLoader
    {
        public static void Log(string text)
        {
            File.AppendAllText("C:\\Temp\\ModuleLoader.txt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " - " + text + Environment.NewLine);
        }
        
        public IAbpModuleDescriptor[] LoadModules(
            IServiceCollection services,
            Type startupModuleType,
            PlugInSourceList plugInSources)
        {
            Check.NotNull(services, nameof(services));
            Check.NotNull(startupModuleType, nameof(startupModuleType));
            Check.NotNull(plugInSources, nameof(plugInSources));

            var modules = GetDescriptors(services, startupModuleType, plugInSources);

            modules = SortByDependency(modules, startupModuleType);
            ConfigureServices(modules, services);

            return modules.ToArray();
        }

        private List<IAbpModuleDescriptor> GetDescriptors(
            IServiceCollection services, 
            Type startupModuleType,
            PlugInSourceList plugInSources)
        {
            var modules = new List<AbpModuleDescriptor>();

            FillModules(modules, services, startupModuleType, plugInSources);
            SetDependencies(modules);

            return modules.Cast<IAbpModuleDescriptor>().ToList();
        }

        protected virtual void FillModules(
            List<AbpModuleDescriptor> modules,
            IServiceCollection services,
            Type startupModuleType,
            PlugInSourceList plugInSources)
        {
            //All modules starting from the startup module
            foreach (var moduleType in AbpModuleHelper.FindAllModuleTypes(startupModuleType))
            {
                modules.Add(CreateModuleDescriptor(services, moduleType));
            }

            //Plugin modules
            foreach (var moduleType in plugInSources.GetAllModules())
            {
                if (modules.Any(m => m.Type == moduleType))
                {
                    continue;
                }

                modules.Add(CreateModuleDescriptor(services, moduleType, isLoadedAsPlugIn: true));
            }
        }

        protected virtual void SetDependencies(List<AbpModuleDescriptor> modules)
        {
            foreach (var module in modules)
            {
                SetDependencies(modules, module);
            }
        }

        protected virtual List<IAbpModuleDescriptor> SortByDependency(List<IAbpModuleDescriptor> modules, Type startupModuleType)
        {
            var sortedModules = modules.SortByDependencies(m => m.Dependencies);
            sortedModules.MoveItem(m => m.Type == startupModuleType, modules.Count - 1);
            return sortedModules;
        }

        protected virtual AbpModuleDescriptor CreateModuleDescriptor(IServiceCollection services, Type moduleType, bool isLoadedAsPlugIn = false)
        {
            return new AbpModuleDescriptor(moduleType, CreateAndRegisterModule(services, moduleType), isLoadedAsPlugIn);
        }

        protected virtual IAbpModule CreateAndRegisterModule(IServiceCollection services, Type moduleType)
        {
            var module = (IAbpModule)Activator.CreateInstance(moduleType);
            services.AddSingleton(moduleType, module);
            return module;
        }

        protected virtual void ConfigureServices(List<IAbpModuleDescriptor> modules, IServiceCollection services)
        {
            var context = new ServiceConfigurationContext(services);
            services.AddSingleton(context);

            foreach (var module in modules)
            {
                if (module.Instance is AbpModule abpModule)
                {
                    abpModule.ServiceConfigurationContext = context;
                }
            }

            //PreConfigureServices
            foreach (var module in modules.Where(m => m.Instance is IPreConfigureServices))
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    ((IPreConfigureServices) module.Instance).PreConfigureServices(context);
                }
                catch (Exception ex)
                {
                    throw new AbpInitializationException(
                        $"An error occurred during {nameof(IPreConfigureServices.PreConfigureServices)} phase of the module {module.Type.AssemblyQualifiedName}. See the inner exception for details.",
                        ex);
                }
                finally
                {
                    Log($"PreConfigureServices: {module.Type.FullName} in {sw.Elapsed.TotalMilliseconds:000}ms");
                }
            }

            //ConfigureServices
            foreach (var module in modules)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    if (module.Instance is AbpModule abpModule)
                    {
                        if (!abpModule.SkipAutoServiceRegistration)
                        {
                            services.AddAssembly(module.Type.Assembly);
                        }
                    }

                    module.Instance.ConfigureServices(context);
                }
                catch (Exception ex)
                {
                    throw new AbpInitializationException($"An error occurred during {nameof(IAbpModule.ConfigureServices)} phase of the module {module.Type.AssemblyQualifiedName}. See the inner exception for details.", ex);
                }
                finally
                {
                    Log($"ConfigureServices: {module.Type.FullName} in {sw.Elapsed.TotalMilliseconds:000}ms");
                }
            }

            //PostConfigureServices
            foreach (var module in modules.Where(m => m.Instance is IPostConfigureServices))
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    ((IPostConfigureServices)module.Instance).PostConfigureServices(context);
                }
                catch (Exception ex)
                {
                    throw new AbpInitializationException($"An error occurred during {nameof(IPostConfigureServices.PostConfigureServices)} phase of the module {module.Type.AssemblyQualifiedName}. See the inner exception for details.", ex);
                }
                finally
                {
                    Log($"PostConfigureServices: {module.Type.FullName} in {sw.Elapsed.TotalMilliseconds:000}ms");
                }
            }

            foreach (var module in modules)
            {
                if (module.Instance is AbpModule abpModule)
                {
                    abpModule.ServiceConfigurationContext = null;
                }
            }
        }

        protected virtual void SetDependencies(List<AbpModuleDescriptor> modules, AbpModuleDescriptor module)
        {
            foreach (var dependedModuleType in AbpModuleHelper.FindDependedModuleTypes(module.Type))
            {
                var dependedModule = modules.FirstOrDefault(m => m.Type == dependedModuleType);
                if (dependedModule == null)
                {
                    throw new AbpException("Could not find a depended module " + dependedModuleType.AssemblyQualifiedName + " for " + module.Type.AssemblyQualifiedName);
                }

                module.AddDependency(dependedModule);
            }
        }
    }
}