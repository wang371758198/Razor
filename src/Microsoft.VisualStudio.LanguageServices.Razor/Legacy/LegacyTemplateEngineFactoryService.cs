// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.IO;
using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.VisualStudio.LanguageServices.Razor
{
    // ----------------------------------------------------------------------------------------------------
    // NOTE: This is only here for VisualStudio binary compatibility. This type should not be used; instead
    // use DefaultTemplateEngineFactoryService.
    // ----------------------------------------------------------------------------------------------------
    [Export(typeof(RazorTemplateEngineFactoryService))]
    internal class LegacyTemplateEngineFactoryService : RazorTemplateEngineFactoryService
    {
        private readonly IServiceProvider _services;
        
        [ImportingConstructor]
        public LegacyTemplateEngineFactoryService([Import(typeof(SVsServiceProvider))] IServiceProvider services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            _services = services;
        }
        
        public override RazorTemplateEngine Create(string projectPath, Action<IRazorEngineBuilder> configure)
        {
            if (projectPath == null)
            {
                throw new ArgumentNullException(nameof(projectPath));
            }

            var project = GetProject(projectPath);
            if (project == null)
            {

            }

            var hierarchy = (IVsHierarchy)project;
            ErrorHandler.ThrowOnFailure(hierarchy.GetProperty((uint)VSConstants.VSITEMID.Root, (int)__VSHPROPID.VSHPROPID_ExtObject, out var obj));

            var dteProject = (EnvDTE.Project)obj;
            var vsProject = (VSLangProj.VSProject)dteProject.Object;

            var count = vsProject.References.Count;
            foreach (VSLangProj.Reference reference in vsProject.References)
            {
                if (reference.Name == "Microsoft.AspNetCore.Mvc.Razor")
                {
                    Console.WriteLine(reference);
                }
                else if (reference.Name == "Microsoft.AspNetCore.Razor.Language")
                {
                    Console.WriteLine(reference);
                }
            }



            var engine = RazorEngine.CreateDesignTime(b =>
            {
                configure?.Invoke(b);

                // For now we're hardcoded to use MVC's extensibility.
                RazorExtensions.Register(b);
            });

            var templateEngine = new MvcRazorTemplateEngine(engine, RazorProject.Create(projectPath));
            templateEngine.Options.ImportsFileName = "_ViewImports.cshtml";
            return templateEngine;
        }

        private IVsProject GetProject(string projectPath)
        {
            var solution = (IVsSolution)_services.GetService(typeof(SVsSolution));

            var guid = Guid.Empty;
            ErrorHandler.ThrowOnFailure(solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref guid, out var enumerator));
            ErrorHandler.ThrowOnFailure(enumerator.Reset());

            var hierarchy = new IVsHierarchy[1] { null };
            while (ErrorHandler.ThrowOnFailure(enumerator.Next(1, hierarchy, out var fetched)) == VSConstants.S_OK && fetched == 1)
            {
                var project = (IVsProject)hierarchy[0];
                ErrorHandler.ThrowOnFailure(project.GetMkDocument((uint)VSConstants.VSITEMID.Root, out var path), VSConstants.E_NOTIMPL);

                if (path != null)
                {
                    // The path returned by GetMkDocument is the path to the project file. We want the path of the project directory.
                    path = Path.GetDirectoryName(path);
                }

                if (string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    return project;
                }
            }

            return null;
        }
    }
}
