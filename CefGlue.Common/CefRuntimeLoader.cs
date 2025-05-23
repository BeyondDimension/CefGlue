﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using Xilium.CefGlue.Common.Handlers;
using Xilium.CefGlue.Common.Shared;

namespace Xilium.CefGlue.Common
{
    public static class CefRuntimeLoader
    {
        private const string DefaultBrowserProcessDirectory = "CefGlueBrowserProcess";

        private static Action<BrowserProcessHandler> _delayedInitialization;

        public static void Initialize(CefSettings settings = null, KeyValuePair<string, string>[] flags = null, CustomScheme[] customSchemes = null, string? basePath = null)
        {
            _delayedInitialization = (browserProcessHandler) => InternalInitialize(settings, flags, customSchemes, browserProcessHandler, basePath);
        }

        private static void InternalInitialize(CefSettings settings = null, KeyValuePair<string, string>[] flags = null, CustomScheme[] customSchemes = null, BrowserProcessHandler browserProcessHandler = null, string? basePath = null)
        {
            CefRuntime.Load();

            if (settings == null)
            {
                settings = new CefSettings();
            }

            settings.UncaughtExceptionStackSize = 100; // for uncaught exception event work properly

            basePath ??= AppContext.BaseDirectory;
            var probingPaths = GetSubProcessPaths(basePath).ToHashSet();
            var subProcessPath = probingPaths.FirstOrDefault(p => File.Exists(p));
            if (subProcessPath == null)
                throw new FileNotFoundException($"Unable to find SubProcess. Probed locations: {string.Join(Environment.NewLine, probingPaths)}");

            settings.BrowserSubprocessPath = subProcessPath;

            switch (CefRuntime.Platform)
            {
                case CefRuntimePlatform.Windows:
                    settings.MultiThreadedMessageLoop = true;
                    break;

                case CefRuntimePlatform.MacOS:
                    var resourcesPath = Path.Combine(basePath, "Resources");
                    if (!Directory.Exists(resourcesPath))
                    {
                        throw new FileNotFoundException($"Unable to find Resources folder");
                    }

                    settings.NoSandbox = true;
                    settings.MultiThreadedMessageLoop = false;
                    settings.ExternalMessagePump = true;
                    settings.MainBundlePath = basePath;
                    settings.FrameworkDirPath = basePath;
                    settings.ResourcesDirPath = resourcesPath;
                    break;

                case CefRuntimePlatform.Linux:
                    settings.NoSandbox = true;
                    settings.MultiThreadedMessageLoop = true;
                    break;
            }

            AppDomain.CurrentDomain.ProcessExit += delegate { CefRuntime.Shutdown(); };

            IsOSREnabled = settings.WindowlessRenderingEnabled;

            // On Linux, with osr disable, the filename in CefMainArgs will be used as accessible name.
            // If the name is empty, chromium will crash at ui::AXNodeData:SetNamechecked.
            var exeFileName =
#if NET6_0_OR_GREATER
                Environment.ProcessPath;
#else
                Process.GetCurrentProcess().MainModule.FileName;
#endif
            if (string.IsNullOrEmpty(exeFileName))
            {
                exeFileName = "CefGlue";
            }

            // Fix crash with youtube https://github.com/chromiumembedded/cef/issues/3643
            {
#if DEBUG
                if (CefRuntime.ChromeVersion.Split(".").First() != "120")
                {
                    throw new Exception("Remove this fix block after CEF upgrade");
                }
#endif
                flags = (flags ?? []).Append(KeyValuePair.Create("disable-features", "FirstPartySets")).ToArray();
            }

            CefRuntime.Initialize(new CefMainArgs(new[] { exeFileName }), settings, new BrowserCefApp(customSchemes, flags, browserProcessHandler), IntPtr.Zero);

            if (customSchemes != null)
            {
                foreach (var scheme in customSchemes)
                {
                    CefRuntime.RegisterSchemeHandlerFactory(scheme.SchemeName, scheme.DomainName, scheme.SchemeHandlerFactory);
                }
            }
        }

        private static IEnumerable<string> GetSubProcessPaths(string baseDirectory)
        {
            yield return Path.Combine(baseDirectory, DefaultBrowserProcessDirectory, BrowserProcessFileName);
            yield return Path.Combine(baseDirectory, BrowserProcessFileName);

            // The executing DLL might not be in the current domain directory (plugins scenario)
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(assemblyLocation))
            {
                baseDirectory = Path.GetDirectoryName(assemblyLocation);
                if (!string.IsNullOrEmpty(baseDirectory))
                {
                    yield return Path.Combine(baseDirectory, DefaultBrowserProcessDirectory, BrowserProcessFileName);
                    yield return Path.Combine(baseDirectory, BrowserProcessFileName);
                }
            }
        }

        internal static void Load(BrowserProcessHandler browserProcessHandler = null)
        {
            if (_delayedInitialization != null)
            {
                _delayedInitialization.Invoke(browserProcessHandler);
                _delayedInitialization = null;
            }
            else
            {
                InternalInitialize(browserProcessHandler: browserProcessHandler);
            }
        }

        public static bool IsLoaded => CefRuntime.IsInitialized;

        internal static bool IsOSREnabled { get; private set; }

        private static string BrowserProcessFileName
        {
            get
            {
                const string Filename = "Xilium.CefGlue.BrowserProcess";
                switch (CefRuntime.Platform)
                {
                    case CefRuntimePlatform.Windows:
                        return Filename + ".exe";
                    default:
                        return Filename;
                }
            }
        }
    }
}
